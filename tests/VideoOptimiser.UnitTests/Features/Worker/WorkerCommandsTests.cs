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
        var lifecycle = new CreatingLifecycle(CliFakes.Worker, quote: null);

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, services => services.AddSingleton<ISettingsValidator>(new FailingValidator()), "worker", "create");

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("[WatchRootMissing] Watch root clip-a is missing\n[ToolMissing] ffprobe was not found\n");
        lifecycle.Calls.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsksTheQuoteThenPrintsTheProgressAndTheReadyWorker()
    {
        var quote = new RemoteWorkerCreationQuote(new("cx43", "hel1", 0.0123m), new("cx43", "hel1", 0.0123m));
        var lifecycle = new CreatingLifecycle(CliFakes.Worker, quote);

        var (code, stdout, stderr) = await RunAsync(lifecycle, "y\n", CliFakes.AcceptAnySettings, "worker", "create");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        lifecycle.Calls.Should().Be(1);
        lifecycle.DatabasePath.Should().Be(DatabasePath);
        lifecycle.Approved.Should().BeTrue();
        // The prompt is written with no newline, and the typed answer is not echoed, so the progress line follows it directly.
        stdout.ReplaceLineEndings("\n").Should().Be(
            quote.FormatPrompt().ReplaceLineEndings("\n") +
            "Creating Hetzner cx43 in hel1.\n" +
            "Worker ready: video-optimiser-1 (42) 203.0.113.10  cx43 in hel1\n");
    }

    [Fact]
    public async Task CreateWithNoAnswerDeclinesTheQuoteAndSaysNothingWasCreated()
    {
        var quote = new RemoteWorkerCreationQuote(new("cx43", "hel1", 0.0123m), new("cx43", "hel1", 0.0123m));
        var lifecycle = new CreatingLifecycle(CliFakes.Worker, quote);

        var (code, stdout, stderr) = await RunAsync(lifecycle, null, CliFakes.AcceptAnySettings, "worker", "create");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        lifecycle.Approved.Should().BeFalse();
        stdout.ReplaceLineEndings("\n").Should().Be(quote.FormatPrompt().ReplaceLineEndings("\n") + "\nNo server was created.\n");
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
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, string.Join('\n',
            "database:",
            $"  path: '{DatabasePath}'",
            "processing:",
            "  mode: \"local\"",
            "  remoteSsh:",
            "    lifecycle: \"hetzner\"",
            ""));
        return await ConsoleCapture.RunCliWithInputAsync(input, services =>
        {
            settings(services);
            services.AddSingleton(lifecycle);
        }, [.. args, "--config", config]);
    }

    private sealed class FailingValidator : ISettingsValidator
    {
        public IReadOnlyList<Diagnostic> Validate(AppSettings settings) =>
        [
            new(DiagnosticCategory.FileSystem, DiagnosticStatus.Fail, "WatchRootMissing", "Watch root clip-a is missing"),
            new(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, "ToolMissing", "ffprobe was not found")
        ];
    }

    /// <summary>Asks the creation question like the real lifecycle, reports one progress line, and creates nothing.</summary>
    private sealed class CreatingLifecycle(RemoteWorkerInfo worker, RemoteWorkerCreationQuote? quote) : IRemoteWorkerLifecycle
    {
        public int Calls { get; private set; }
        public string? DatabasePath { get; private set; }
        public bool? Approved { get; private set; }

        public bool IsManaged(AppSettings settings) => true;

        public async Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, Func<RemoteWorkerCreationQuote, CancellationToken, Task<bool>>? confirmCreation = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            DatabasePath = databasePath;
            if (confirmCreation is not null && quote is not null)
            {
                Approved = await confirmCreation(quote, cancellationToken);
                if (!Approved.Value) throw new RemoteWorkerCreationDeclinedException();
            }
            progress?.Report("Creating Hetzner cx43 in hel1.");
            return worker;
        }

        public Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
