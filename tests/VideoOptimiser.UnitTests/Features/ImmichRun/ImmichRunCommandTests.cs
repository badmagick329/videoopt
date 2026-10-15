using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests.Features.ImmichRun;

/// <summary>
/// The branches of <c>immich run</c> that no other CLI test pins, with whole output and exit code, so the move of the command
/// into its slice (VOPT-100) is checked against today's behaviour: the refusals before the lock, the guard's lines when
/// discovery fails, the guard summary's three blocks, and the rules that turn a clean run into <c>PartialSuccess</c>. The
/// run service and the queue are faked; their own behaviour is in <c>ImmichRunTests</c> and the queue tests. The run lock,
/// its holder name and Ctrl+C during discovery are pinned by <c>RunLockTests</c>, <c>RunCommandTests</c> and <c>CtrlCRunTests</c>.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class ImmichRunCommandTests : IDisposable
{
    private static readonly DateTime Taken = new(2026, 3, 4, 5, 6, 7);
    private static readonly string[] Banner = ["Encoding on this PC."];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly FakeRunService _run = new();
    private readonly FakeQueue _queue = new();

    public ImmichRunCommandTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public async Task WithoutImmichSetUpItSaysSoAndDoesNothing()
    {
        var config = await WriteConfigAsync(immich: false);

        var (code, stdout, stderr) = await RunAsync(config);

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("Set immich.baseUrl in the configuration first; 'video-optimiser doctor' then checks the connection and the API key.\n");
        _run.Calls.Should().Be(0);
    }

    [Fact]
    public async Task InvalidSettingsAreListedAndNothingRuns()
    {
        var config = await WriteConfigAsync();

        var (code, stdout, stderr) = await RunAsync(config, configure: services => services.AddSingleton<ISettingsValidator>(new FixedDiagnostics(
            new Diagnostic(DiagnosticCategory.Configuration, DiagnosticStatus.Fail, "ConfigA", "first problem"),
            new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, "ConfigB", "second problem"))));

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("[ConfigA] first problem\n[ConfigB] second problem\n");
        _run.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ARunThatFindsNothingPrintsTheBannerAndTheSummaryAndSucceeds()
    {
        var config = await WriteConfigAsync();

        var (code, stdout, stderr) = await RunAsync(config);

        (code, stdout, stderr).Should().Be((0, Lines([.. Banner, "", "Found 0 videos to convert after checking 0.", "Nothing to finalise."]), ""));
        _queue.Runs.Should().Be(0);
    }

    [Fact]
    public async Task TheGuardSummaryNamesEachBlockAndItsIssuesMakeTheRunAPartialSuccess()
    {
        var config = await WriteConfigAsync();
        _run.Guard = Guard(trashed: [Hit("clip-a.mp4")], restored: [Hit("clip-b.mp4")], issues: ["Immich named an asset that did not match"]);

        var (code, stdout, stderr) = await RunAsync(config);

        code.Should().Be((int)ExitCode.PartialSuccess);
        stderr.Should().BeEmpty();
        stdout.Should().Be(Lines(
            [.. Banner,
            "Re-upload guard: 1 converted original was uploaded again and moved to Immich's trash; 1 converted original is out of Immich's trash, so both versions exist; 1 issue.",
            "",
            "Found 0 videos to convert after checking 0.",
            "Nothing to finalise.",
            "",
            "The phone uploaded these converted originals again. They are back in Immich's trash; delete them from the phone:",
            "  clip-a.mp4  taken 2026-03-04 05:06",
            "",
            "These converted originals are out of Immich's trash, so both versions exist. The guard left them alone:",
            "  clip-b.mp4  taken 2026-03-04 05:06",
            "",
            "Re-upload guard issues:",
            "  Immich named an asset that did not match"]));
    }

    [Fact]
    public async Task TrashedAndRestoredOriginalsAreReportedButTheRunStillSucceeds()
    {
        var config = await WriteConfigAsync();
        _run.Guard = Guard(trashed: [Hit("clip-a.mp4"), Hit("clip-b.mp4")], restored: [Hit("clip-c.mp4")]);

        var (code, stdout, _) = await RunAsync(config);

        code.Should().Be((int)ExitCode.Success);
        stdout.Should().Be(Lines(
            [.. Banner,
            "Re-upload guard: 2 converted originals were uploaded again and moved to Immich's trash; 1 converted original is out of Immich's trash, so both versions exist.",
            "",
            "Found 0 videos to convert after checking 0.",
            "Nothing to finalise.",
            "",
            "The phone uploaded these converted originals again. They are back in Immich's trash; delete them from the phone:",
            "  clip-a.mp4  taken 2026-03-04 05:06",
            "  clip-b.mp4  taken 2026-03-04 05:06",
            "",
            "These converted originals are out of Immich's trash, so both versions exist. The guard left them alone:",
            "  clip-c.mp4  taken 2026-03-04 05:06"]));
    }

    [Fact]
    public async Task DiscoveryIssuesMakeTheRunAPartialSuccess()
    {
        var config = await WriteConfigAsync();
        _run.Discovery = Discovery(issues: 2);

        var (code, stdout, stderr) = await RunAsync(config);

        code.Should().Be((int)ExitCode.PartialSuccess);
        stderr.Should().BeEmpty();
        stdout.Should().Be(Lines([.. Banner, "", "Found 0 videos to convert after checking 0.", "Also: 2 issues (above).", "Nothing to finalise."]));
    }

    [Fact]
    public async Task ExcludedAssetsAreWarnedAboutOnStderrAndMakeTheRunAPartialSuccess()
    {
        var config = await WriteConfigAsync();
        _run.Discovery = Discovery(excluded: 2);

        var (code, stdout, stderr) = await RunAsync(config);

        code.Should().Be((int)ExitCode.PartialSuccess);
        stdout.Should().Be(Lines([.. Banner, "", "Found 0 videos to convert after checking 0.", "Not downloaded: 2 excluded.", "Nothing to finalise."]));
        stderr.ReplaceLineEndings("\n").Should().Be("Immich's search returned 2 assets its filter should have left out. They were left alone, but the search may behave differently in this Immich version: check before running again.\n");
    }

    [Fact]
    public async Task AFailedJobKeepsTheProcessingFailureExitWhateverTheGuardFound()
    {
        var config = await WriteConfigAsync();
        var failed = new FailedQueueJob(Guid.NewGuid(), Path.Combine(_directory, "clip-a.mp4"), "SourceMissing", "The staged original is missing.");
        _run.Guard = Guard(issues: ["an issue"]);
        _run.Queue = new QueuePreparationResult([], [failed], [], [], JobSourceKind.Immich, FollowQueue: false);

        var (code, stdout, _) = await RunAsync(config);

        code.Should().Be((int)ExitCode.ProcessingFailure);
        stdout.Should().Be(Lines(
            [.. Banner,
            "Re-upload guard: 1 issue.",
            "",
            "Found 0 videos to convert after checking 0.",
            "Failed: 1.",
            "",
            "Failed jobs:",
            $"{failed.Id:N}  clip-a.mp4",
            "SourceMissing: The staged original is missing.",
            "",
            "Re-upload guard issues:",
            "  an issue"]));
    }

    [Fact]
    public async Task ConvertedVideosPointAtFinalizeAfterTheGuardSummary()
    {
        var config = await WriteConfigAsync();
        _run.Guard = Guard(trashed: [Hit("clip-a.mp4")]);
        _run.Queue = OneJob();
        _queue.Result = RunResult(ready: 1);

        var (code, stdout, stderr) = await RunAsync(config);

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.Should().Be(Lines(
            [.. Banner,
            "Re-upload guard: 1 converted original was uploaded again and moved to Immich's trash.",
            "",
            "Found 0 videos to convert after checking 0.",
            "Converting 1 video.",
            "Ready to finalise: 1.",
            "",
            "The phone uploaded these converted originals again. They are back in Immich's trash; delete them from the phone:",
            "  clip-a.mp4  taken 2026-03-04 05:06",
            "Next: review the outputs, then run 'video-optimiser immich finalize'."]));
    }

    [Fact]
    public async Task AFailureBesideAReadyVideoIsAPartialSuccessFromTheQueueNotTheGuard()
    {
        var config = await WriteConfigAsync();
        _run.Queue = OneJob();
        _queue.Result = RunResult(ready: 1, failed: new FailedQueueJob(Guid.NewGuid(), Path.Combine(_directory, "clip-b.mp4"), "ProcessingFailed", "ffmpeg failed"));

        var (code, _, _) = await RunAsync(config);

        code.Should().Be((int)ExitCode.PartialSuccess);
    }

    [Fact]
    public async Task APressedStopAfterTheVideoEndsTheRunCancelled()
    {
        var config = await WriteConfigAsync();
        var last = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "clip-a.mp4"), Status = JobStatus.ReadyToFinalize, SourceKind = JobSourceKind.Immich };
        _run.Queue = OneJob();
        _queue.Result = RunResult(ready: 1) with { StoppedAfter = last, NotStarted = 2 };

        var (code, stdout, _) = await RunAsync(config);

        code.Should().Be((int)ExitCode.Cancelled);
        stdout.Should().Be(Lines(
            [.. Banner,
            "",
            "Found 0 videos to convert after checking 0.",
            "Converting 1 video.",
            "",
            "Stopped after clip-a.mp4; 2 videos are still waiting.",
            "Ready to finalise: 1.",
            "Next: review the outputs, then run 'video-optimiser immich finalize'.",
            $"To convert the rest: video-optimiser immich run --config \"{config}\""]));
    }

    [Fact]
    public async Task AWorkerThatCannotBeDeletedMakesAFullyConvertedRunAGeneralFailure()
    {
        var config = await WriteConfigAsync(managedWorker: true);
        var lifecycle = new FakeWorkerLifecycle { DeleteFailure = new HttpRequestException("Hetzner answered 503") };
        _run.Queue = OneJob();
        _queue.Result = RunResult(ready: 1);

        var (code, _, stderr) = await RunAsync(config, services =>
        {
            services.AddSingleton<IRemoteWorkerLifecycle>(lifecycle);
            services.AddSingleton<IJobProcessor>(new NeedsWorker());
        });

        code.Should().Be((int)ExitCode.GeneralFailure);
        lifecycle.DeleteCalls.Should().Be(1);
        stderr.Should().Contain("Hetzner answered 503");
    }

    [Fact]
    public async Task AnErrorWhileConvertingStillPrintsTheGuardSummaryBeforeItIsReported()
    {
        var config = await WriteConfigAsync();
        _run.Guard = Guard(trashed: [Hit("clip-a.mp4")], issues: ["an issue"]);
        _run.Queue = OneJob();
        _queue.Failure = new InvalidOperationException("the encoder blew up");

        var (code, stdout, stderr) = await RunAsync(config);

        code.Should().NotBe((int)ExitCode.Success);
        stdout.Should().EndWith(Lines(
            [
            "Converting 1 video.",
            "",
            "The phone uploaded these converted originals again. They are back in Immich's trash; delete them from the phone:",
            "  clip-a.mp4  taken 2026-03-04 05:06",
            "",
            "Re-upload guard issues:",
            "  an issue"]));
        stderr.Should().Contain("the encoder blew up");
    }

    [Fact]
    public async Task ACtrlCWhileConvertingStillPrintsTheGuardSummaryAndEndsCancelled()
    {
        var config = await WriteConfigAsync();
        _run.Guard = Guard(restored: [Hit("clip-b.mp4")]);
        _run.Queue = OneJob();
        _queue.Failure = new OperationCanceledException();

        var (code, stdout, _) = await RunAsync(config);

        code.Should().Be((int)ExitCode.Cancelled);
        stdout.Should().EndWith(Lines(
            [
            "Converting 1 video.",
            "",
            "These converted originals are out of Immich's trash, so both versions exist. The guard left them alone:",
            "  clip-b.mp4  taken 2026-03-04 05:06",
            "",
            "Stopped now.",
            $"To resume: video-optimiser immich run --config \"{config}\""]));
    }

    [Fact]
    public async Task WhenPreparationFailsAfterTheGuardReportedTheGuardsLinesAreRepeatedAfterTheError()
    {
        var config = await WriteConfigAsync();
        _run.Reports = [new ScanProgress("p", "Trashed", "clip-a.mp4"), new ScanProgress("p", "Issue", "Immich said no")];
        _run.Failure = new InvalidOperationException("Immich went away");

        var (code, stdout, stderr) = await RunAsync(config);

        code.Should().NotBe((int)ExitCode.Success);
        stdout.Should().Be(Lines(
            [.. Banner,
            "Re-upload guard: trashed clip-a.mp4",
            "Re-upload guard issue: Immich said no",
            "",
            "Before the run ended, the re-upload guard reported:",
            "Re-upload guard: trashed clip-a.mp4",
            "Re-upload guard issue: Immich said no"]));
        stderr.Should().Contain("Immich went away");
    }

    [Fact]
    public async Task WhenPreparationFailsBeforeTheGuardReportedNothingIsRepeated()
    {
        var config = await WriteConfigAsync();
        _run.Failure = new InvalidOperationException("Immich went away");

        var (_, stdout, stderr) = await RunAsync(config);

        stdout.Should().Be(Lines(Banner));
        stderr.Should().Contain("Immich went away");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Lines(IEnumerable<string> lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string config, Action<IServiceCollection>? configure = null) =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton<IImmichRunService>(_run);
            services.AddSingleton<IQueueService>(_queue);
            configure?.Invoke(services);
        }, "immich", "run", "--config", config);

    private async Task<string> WriteConfigAsync(bool immich = true, bool managedWorker = false)
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, string.Join('\n',
            "database:",
            $"  path: '{DatabasePath}'",
            immich ? "immich:\n  baseUrl: 'http://immich.test'" : "",
            managedWorker ? "processing:\n  mode: \"remoteSsh\"\n  remoteSsh:\n    lifecycle: \"hetzner\"" : "",
            ""));
        return config;
    }

    private static ImmichReuploadHit Hit(string name) => new(Guid.NewGuid(), Guid.NewGuid().ToString("D"), name, Taken);

    private static ImmichReuploadGuardResult Guard(ImmichReuploadHit[]? trashed = null, ImmichReuploadHit[]? restored = null, string[]? issues = null) =>
        new(0, trashed ?? [], restored ?? [], [.. (issues ?? []).Select(reason => new ImmichReuploadIssue(Guid.NewGuid(), Guid.NewGuid().ToString("D"), reason))]);

    private static ImmichDiscoveryResult Discovery(int excluded = 0, int issues = 0) => new([], 0, 0, 0, 0, 0, excluded, 0, 0, issues, []);

    private QueuePreparationResult OneJob() => new(
        [new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "clip-a.mp4"), Status = JobStatus.Queued, SourceKind = JobSourceKind.Immich }],
        [], [], [], JobSourceKind.Immich, FollowQueue: false);

    private static QueueRunResult RunResult(int ready, FailedQueueJob? failed = null) =>
        new(ready, failed is null ? [] : [failed], [], null, 0, ExitCode.Success);

    /// <summary>Returns what the test set, after handing the guard's progress to the command as the real service does.</summary>
    private sealed class FakeRunService : IImmichRunService
    {
        public int Calls { get; private set; }
        public ImmichReuploadGuardResult Guard { get; set; } = new(0, [], [], []);
        public ImmichDiscoveryResult Discovery { get; set; } = new([], 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
        public QueuePreparationResult Queue { get; set; } = new([], [], [], [], JobSourceKind.Immich, FollowQueue: false);
        public ScanProgress[] Reports { get; set; } = [];
        public Exception? Failure { get; set; }

        public Task<ImmichRunPreparation> PrepareAsync(string databasePath, AppSettings settings, int? limit, IProgress<ScanProgress>? guardProgress = null, IProgress<ImmichProgress>? discoveryProgress = null, bool modeOverridden = false, CancellationToken cancellationToken = default)
        {
            Calls++;
            foreach (var report in Reports) guardProgress?.Report(report);
            return Failure is null ? Task.FromResult(new ImmichRunPreparation(Guard, Discovery, Queue)) : Task.FromException<ImmichRunPreparation>(Failure);
        }
    }

    /// <summary>The run half of the queue: ends as the test says without touching a job.</summary>
    private sealed class FakeQueue : IQueueService
    {
        public int Runs { get; private set; }
        public QueueRunResult Result { get; set; } = RunResult(ready: 0);
        public Exception? Failure { get; set; }

        public Task<QueuePreparationResult> PrepareAsync(string databasePath, AppSettings settings, JobSourceKind? sourceKind, bool modeOverridden = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<QueueRunResult> RunAsync(string databasePath, AppSettings settings, QueuePreparationResult preparation, Func<JobRecord, CancellationToken, Task<bool>>? passOver = null, IProgress<JobProgress>? progress = null, CancellationToken stopAfterThisVideo = default, CancellationToken cancellationToken = default)
        {
            Runs++;
            return Failure is null ? Task.FromResult(Result) : Task.FromException<QueueRunResult>(Failure);
        }
    }

    /// <summary>Says every video needs the worker, so a managed run creates one and has one to delete.</summary>
    private sealed class NeedsWorker : IJobProcessor
    {
        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedDiagnostics(params Diagnostic[] diagnostics) : ISettingsValidator
    {
        public IReadOnlyList<Diagnostic> Validate(AppSettings settings) => diagnostics;
    }
}
