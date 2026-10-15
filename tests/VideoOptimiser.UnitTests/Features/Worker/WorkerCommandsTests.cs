using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Cli.Features.Run;
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
            "Create the worker? [y/N] " +
            "Creating Hetzner cx43 in hel1.\n" +
            "Worker ready: video-optimiser-1 203.0.113.10  cx43 in hel1\n");
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
        stdout.ReplaceLineEndings("\n").Should().Be("Will create cpx42 in fsn1 at EUR 0.134/h incl. VAT.\nCreate the worker? [y/N] \nNo worker was created.\n");
    }

    /// <summary>
    /// A press after the server exists leaves it billing, so the command says so and how to delete it, as a run does. The
    /// exit code stays 9.
    /// </summary>
    [Fact]
    public async Task CtrlCWhileTheWorkerSetsUpSaysItStaysUpAndHowToDeleteIt()
    {
        var lifecycle = new FakeWorkerLifecycle { Exists = false, DuringSetup = _ => { Stop.Press(); return Task.CompletedTask; } };

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, CliFakes.AcceptAnySettings, "worker", "create");

        code.Should().Be((int)ExitCode.Cancelled);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            "The worker video-optimiser-1 stays up, billed hourly, until it is deleted.\n" +
            $"To delete it: video-optimiser worker delete --config \"{_config}\"\n");
    }

    /// <summary>Nothing exists yet at the price prompt, so there is nothing to say about a worker.</summary>
    [Fact]
    public async Task CtrlCAtThePricePromptSaysNothingAndCreatesNothing()
    {
        var lifecycle = new FakeWorkerLifecycle { Exists = false, Quote = new RemoteWorkerCreationQuote(new("cx43", "hel1", 0.031m), new("cx43", "hel1", 0.031m)), AtPrompt = Stop.Press };

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, CliFakes.AcceptAnySettings, "worker", "create");

        code.Should().Be((int)ExitCode.Cancelled);
        stdout.Should().BeEmpty();
        stderr.Should().BeEmpty();
        lifecycle.Exists.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, "video-optimiser-1  running  203.0.113.10  cx43 in hel1\n")]
    [InlineData(false, "No Hetzner worker exists.\n")]
    public async Task StatusPrintsTheWorkerLineOrSaysThereIsNone(bool exists, string expected)
    {
        var lifecycle = new FakeWorkerLifecycle { Exists = exists };

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, CliFakes.AcceptAnySettings, "worker", "status");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(expected);
        lifecycle.DatabasePaths.Should().Equal(DatabasePath);
        // Status reads through the check, which never takes the lifecycle lock a run holds while it sets a worker up.
        lifecycle.CheckCalls.Should().Be(1);
    }

    [Fact]
    public async Task StatusSaysWhyHetznerWasNotAskedAndShowsTheRecordedWorker()
    {
        var lifecycle = new FakeWorkerLifecycle { Check = new WorkerCheck(CliFakes.Worker with { Status = RemoteWorkerStatuses.Recorded }, null, "no API token is set") };

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, CliFakes.AcceptAnySettings, "worker", "status");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            "video-optimiser-1  as recorded on this PC  203.0.113.10  cx43 in hel1\n" +
            "Hetzner wasn't checked: no API token is set.\n");
    }

    [Fact]
    public async Task StatusWithNoRecordAndNoAnswerFromHetznerDoesNotClaimThereIsNoWorker()
    {
        var lifecycle = new FakeWorkerLifecycle { Check = new WorkerCheck(null, null, "Hetzner didn't answer within 15 seconds") };

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, CliFakes.AcceptAnySettings, "worker", "status");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            "No Hetzner worker is recorded on this PC.\n" +
            "Hetzner wasn't checked: Hetzner didn't answer within 15 seconds.\n");
    }

    [Theory]
    [InlineData(true, "Deleting the worker video-optimiser-1.\nWorker deleted; billing for it and its IPv4 address has stopped.\n")]
    [InlineData(false, "No Hetzner worker exists.\n")]
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

    private string _config = "";

    /// <summary>The Ctrl+C of the command under test.</summary>
    private RunStop Stop { get; } = new();

    public void Dispose()
    {
        Stop.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(IRemoteWorkerLifecycle lifecycle, string? input, Action<IServiceCollection> settings, params string[] args)
    {
        _config = await RunCli.WriteConfigAsync(DatabasePath, "processing:", "  mode: \"local\"", "  remoteSsh:", "    lifecycle: \"hetzner\"");
        return await ConsoleCapture.RunCliWithInputAsync(input, services =>
        {
            settings(services);
            services.AddSingleton(lifecycle);
            services.AddSingleton(Stop);
        }, [.. args, "--config", _config]);
    }
}
