using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests.Features.Worker;

/// <summary>
/// What <c>worker create</c>, <c>status</c> and <c>delete</c> print and return once the lifecycle gate is passed. A fake
/// lifecycle stands in for Hetzner, so no test can create a server.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class WorkerCommandsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public WorkerCommandsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CreateWithInvalidSettingsReportsEachDiagnosticAndNeverCallsTheLifecycle()
    {
        var lifecycle = new FakeWorkerLifecycle();
        var failures = CliFakes.Diagnostics(
            new(DiagnosticCategory.FileSystem, DiagnosticStatus.Fail, "WatchRootMissing", "Watch root clip-a is missing"),
            new(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, "ToolMissing", "ffprobe was not found"));

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, failures, "worker", "create");

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("[WatchRootMissing] Watch root clip-a is missing\n[ToolMissing] ffprobe was not found\n");
        lifecycle.EnsureCalls.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsksTheQuoteThenPrintsTheProgressAndTheReadyWorker()
    {
        var lifecycle = new FakeWorkerLifecycle { Quote = new RemoteWorkerCreationQuote(new("cx43", "hel1", 0.031m), new("ccx33", "hel1", 0.266m)) };

        var (code, stdout, stderr) = await RunAsync(lifecycle, "y\n", CliFakes.AcceptAnySettings, "worker", "create");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        lifecycle.EnsureCalls.Should().Be(1);
        lifecycle.DatabasePaths.Should().Equal(DatabasePath);
        lifecycle.Approved.Should().BeTrue();
        // The prompt is written with no newline, and the typed answer is not echoed, so the progress line follows it directly.
        // Literal on purpose: building it from FormatPrompt() would pass whatever the prompt said. A costlier fallback is quoted on its own line.
        stdout.ReplaceLineEndings("\n").Should().Be(
            "Will create cx43 in hel1 at EUR 0.031/h incl. VAT.\n" +
            "If that placement fails, a fallback may cost up to EUR 0.266/h (ccx33 in hel1).\n" +
            "Create the server? [y/N] " +
            "Creating Hetzner cx43 in hel1.\n" +
            "Worker ready: video-optimiser-1 (42) 203.0.113.10  cx43 in hel1\n");
    }

    [Fact]
    public async Task CreateWithNoAnswerDeclinesTheQuoteAndSaysNothingWasCreated()
    {
        // The first candidate is also the dearest, so the quote has no fallback line.
        var only = new RemoteWorkerCreationOption("cpx42", "fsn1", 0.134m);
        var lifecycle = new FakeWorkerLifecycle { Quote = new RemoteWorkerCreationQuote(only, only) };

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, CliFakes.AcceptAnySettings, "worker", "create");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        lifecycle.Approved.Should().BeFalse();
        stdout.ReplaceLineEndings("\n").Should().Be("Will create cpx42 in fsn1 at EUR 0.134/h incl. VAT.\nCreate the server? [y/N] \nNo server was created.\n");
    }

    [Theory]
    [InlineData(true, "video-optimiser-1  server 42  running  203.0.113.10  cx43 in hel1\n")]
    [InlineData(false, "No managed Hetzner worker exists.\n")]
    public async Task StatusPrintsTheWorkerLineOrSaysThereIsNone(bool exists, string expected)
    {
        var lifecycle = new FakeWorkerLifecycle { Exists = exists };

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, CliFakes.AcceptAnySettings, "worker", "status");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(expected);
        lifecycle.DatabasePaths.Should().Equal(DatabasePath);
    }

    [Theory]
    [InlineData(true, "Deleting Hetzner server 42.\nManaged Hetzner worker deleted; billing for its server and Primary IPv4 has stopped.\n")]
    [InlineData(false, "No managed Hetzner worker exists.\n")]
    public async Task DeletePrintsTheLifecyclesLinesOrSaysThereIsNothingToDelete(bool exists, string expected)
    {
        var lifecycle = new FakeWorkerLifecycle { Exists = exists };

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, CliFakes.AcceptAnySettings, "worker", "delete");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(expected);
        // The database path picks the state file and the owner label; another path would act on a different worker.
        lifecycle.DatabasePaths.Should().Equal(DatabasePath);
    }

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(IRemoteWorkerLifecycle lifecycle, string? input, Action<IServiceCollection> settings, params string[] args)
    {
        var config = await RunCli.WriteConfigAsync(DatabasePath, "processing:", "  mode: \"local\"", "  remoteSsh:", "    lifecycle: \"hetzner\"");
        return await ConsoleCapture.RunCliWithInputAsync(input, services =>
        {
            settings(services);
            services.AddSingleton(lifecycle);
        }, [.. args, "--config", config]);
    }
}
