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
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.ImmichRun;

/// <summary>
/// The branches of <c>immich run</c> that <c>ImmichRunTranscriptTests</c> does not pin, with whole output and exit code: the
/// refusals before the lock, the guard's lines when discovery fails, the guard summary's three blocks, and the rules that turn
/// a clean run into <c>PartialSuccess</c>. The run service and the queue are faked; their own behaviour is in
/// <c>ImmichRunTests</c> and the queue tests. The run lock and Ctrl+C during discovery are pinned by <c>RunLockTests</c> and
/// <c>CtrlCRunTests</c>.
/// </summary>
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
        stderr.ReplaceLineEndings("\n").Should().Be($"Set immich.baseUrl in the configuration first; 'video-optimiser doctor --config \"{config}\"' then checks the connection and the API key.\n");
        _run.Calls.Should().Be(0);
    }

    [Fact]
    public async Task InvalidSettingsAreListedAndNothingRuns()
    {
        var config = await WriteConfigAsync();

        var (code, stdout, stderr) = await RunAsync(config, configure: CliFakes.Diagnostics(
            new Diagnostic(DiagnosticCategory.Configuration, DiagnosticStatus.Fail, "ConfigA", "first problem"),
            new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, "ConfigB", "second problem")));

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("[ConfigA] first problem\n[ConfigB] second problem\n");
        _run.Calls.Should().Be(0);
    }

    [Fact]
    public async Task TheGuardSummaryNamesEachBlockAndItsIssuesMakeTheRunAPartialSuccess()
    {
        var config = await WriteConfigAsync();
        _run.Guard = Guard(trashed: [Hit("clip-a.mp4")], restored: [Hit("clip-b.mp4")], issues: ["Immich named an asset that did not match"]);

        var (code, stdout, stderr) = await RunAsync(config);

        code.Should().Be((int)ExitCode.PartialSuccess);
        stderr.Should().BeEmpty();
        stdout.Should().Be(Transcript(
            [.. Banner,
            "Re-upload guard: 1 converted original was uploaded again and moved to Immich's trash; 1 converted original is out of Immich's trash, so both versions exist; 1 issue.",
            "Queued: 0.",
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

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 2)]
    public async Task DiscoveryIssuesAndExcludedVideosMakeTheRunAPartialSuccess(int issues, int excluded)
    {
        var config = await WriteConfigAsync();
        _run.Discovery = Discovery(excluded: excluded, issues: issues);

        var (code, _, _) = await RunAsync(config);

        code.Should().Be((int)ExitCode.PartialSuccess);
    }

    [Fact]
    public async Task AFailedJobKeepsTheProcessingFailureExitWhateverTheGuardFound()
    {
        var config = await WriteConfigAsync();
        var failed = new FailedQueueJob(Guid.NewGuid(), Path.Combine(_directory, "clip-a.mp4"), "SourceMissing", "The staged original is missing.");
        _run.Guard = Guard(issues: ["an issue", "another issue"]);
        _queue.Queue = new QueuePreparationResult([], [failed], [], []);

        var (code, stdout, _) = await RunAsync(config);

        code.Should().Be((int)ExitCode.ProcessingFailure);
        stdout.Should().Be(Transcript(
            [.. Banner,
            "Re-upload guard: 2 issues.",
            "Queued: 0.",
            "Failed: 1.",
            "",
            "Failed jobs:",
            $"{failed.Id:N}  clip-a.mp4",
            "The staged original is missing.",
            "",
            "Re-upload guard issues:",
            "  an issue",
            "  another issue"]));
    }

    [Fact]
    public async Task ConvertedVideosPointAtFinalizeAfterTheGuardSummary()
    {
        var config = await WriteConfigAsync();
        _run.Guard = Guard(trashed: [Hit("clip-a.mp4"), Hit("clip-b.mp4")], restored: [Hit("clip-c.mp4")]);
        _queue.Queue = OneJob();
        _queue.Result = RunResult(ready: 1);

        var (code, stdout, stderr) = await RunAsync(config);

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.Should().Be(Transcript(
            [.. Banner,
            "Re-upload guard: 2 converted originals were uploaded again and moved to Immich's trash; 1 converted original is out of Immich's trash, so both versions exist.",
            "Queued: 0.",
            "Converting 1 video.",
            "Ready to finalise: 1.",
            "",
            "The phone uploaded these converted originals again. They are back in Immich's trash; delete them from the phone:",
            "  clip-a.mp4  taken 2026-03-04 05:06",
            "  clip-b.mp4  taken 2026-03-04 05:06",
            "",
            "These converted originals are out of Immich's trash, so both versions exist. The guard left them alone:",
            "  clip-c.mp4  taken 2026-03-04 05:06",
            $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready --config \"{config}\"'."]));
    }

    [Fact]
    public async Task APressedStopAfterTheVideoEndsTheRunCancelledAndSaysToRunTheRest()
    {
        var config = await WriteConfigAsync();
        var last = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "clip-a.mp4"), Status = JobStatus.ReadyToFinalize, SourceKind = JobSourceKind.Immich };
        _queue.Queue = OneJob();
        _queue.Result = RunResult(ready: 1) with { StoppedAfter = last, NotStarted = 2 };

        var (code, stdout, _) = await RunAsync(config);

        code.Should().Be((int)ExitCode.Cancelled);
        stdout.Should().Be(Transcript(
            [.. Banner,
            "Queued: 0.",
            "Converting 1 video.",
            "",
            "Stopped after clip-a.mp4; 2 videos are still waiting.",
            "Ready to finalise: 1.",
            $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready --config \"{config}\"'.",
            $"To convert the rest: video-optimiser run --config \"{config}\""]));
    }

    [Fact]
    public async Task AWorkerThatCannotBeDeletedMakesAFullyConvertedRunAGeneralFailure()
    {
        var config = await WriteConfigAsync(managedWorker: true);
        var lifecycle = new FakeWorkerLifecycle { DeleteFailure = new HttpRequestException("Hetzner answered 503") };
        _queue.Queue = OneJob();
        _queue.Result = RunResult(ready: 1);

        var (code, _, stderr) = await RunAsync(config, services =>
        {
            services.AddSingleton<IRemoteWorkerLifecycle>(lifecycle);
            services.AddSingleton<IJobProcessor>(new ScriptedRunProcessor { NeedsWorker = _ => true });
        });

        code.Should().Be((int)ExitCode.GeneralFailure);
        lifecycle.DeleteCalls.Should().Be(1);
        stderr.Should().Contain("Hetzner answered 503");
    }

    [Fact]
    public async Task ACtrlCWhileConvertingStillPrintsTheGuardSummaryAndEndsCancelled()
    {
        var config = await WriteConfigAsync();
        _run.Guard = Guard(restored: [Hit("clip-b.mp4")]);
        _queue.Queue = OneJob();
        _queue.Failure = new OperationCanceledException();

        var (code, stdout, _) = await RunAsync(config);

        code.Should().Be((int)ExitCode.Cancelled);
        stdout.Should().EndWith(Transcript(
            [
            "Converting 1 video.",
            "",
            "These converted originals are out of Immich's trash, so both versions exist. The guard left them alone:",
            "  clip-b.mp4  taken 2026-03-04 05:06",
            "",
            "Stopped now.",
            $"To resume: video-optimiser run --config \"{config}\""]));
    }

    [Fact]
    public async Task WhenDiscoveryFailsAfterTheGuardReportedTheGuardsLinesAreRepeatedAfterTheError()
    {
        var config = await WriteConfigAsync();
        _run.Reports = [new ScanProgress("p", "Trashed", "clip-a.mp4"), new ScanProgress("p", "Restored", "clip-b.mp4"), new ScanProgress("p", "Issue", "Immich said no")];
        _run.Failure = new InvalidOperationException("Immich went away");

        var (code, stdout, stderr) = await RunAsync(config);

        code.Should().NotBe((int)ExitCode.Success);
        stdout.Should().Be(Transcript(
            [.. Banner,
            "Re-upload guard: trashed clip-a.mp4",
            "Re-upload guard: restored clip-b.mp4",
            "Re-upload guard issue: Immich said no",
            "",
            "Before the run ended, the re-upload guard reported:",
            "Re-upload guard: trashed clip-a.mp4",
            "Re-upload guard: restored clip-b.mp4",
            "Re-upload guard issue: Immich said no"]));
        stderr.Should().Contain("Immich went away");
    }

    [Fact]
    public async Task WhenDiscoveryFailsBeforeTheGuardReportedNothingIsRepeated()
    {
        var config = await WriteConfigAsync();
        _run.Failure = new InvalidOperationException("Immich went away");

        var (_, stdout, stderr) = await RunAsync(config);

        stdout.Should().Be(Transcript(Banner));
        stderr.Should().Contain("Immich went away");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string config, Action<IServiceCollection>? configure = null) =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton<IImmichRunService>(_run);
            services.AddSingleton<IQueueService>(_queue);
            configure?.Invoke(services);
        }, "immich", "run", "--config", config);

    private static readonly string[] ImmichConfig = ["immich:", "  baseUrl: 'http://immich.test'"];

    private Task<string> WriteConfigAsync(bool immich = true, bool managedWorker = false) =>
        RunCli.WriteConfigAsync(DatabasePath, [.. immich ? ImmichConfig : [], .. managedWorker ? ManagedHetzner : []]);

    private static ImmichReuploadHit Hit(string name) => new(Guid.NewGuid(), Guid.NewGuid().ToString("D"), name, Taken);

    private static ImmichReuploadGuardResult Guard(ImmichReuploadHit[]? trashed = null, ImmichReuploadHit[]? restored = null, string[]? issues = null) =>
        new(0, trashed ?? [], restored ?? [], [.. (issues ?? []).Select(reason => new ImmichReuploadIssue(Guid.NewGuid(), Guid.NewGuid().ToString("D"), reason))]);

    private static ImmichDiscoveryResult Discovery(int excluded = 0, int issues = 0) => new([], 0, 0, 0, 0, 0, 0, 0, excluded, 0, 0, issues, []);

    private QueuePreparationResult OneJob() => new(
        [new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "clip-a.mp4"), Status = JobStatus.Queued }],
        [], [], []);

    private static QueueRunResult RunResult(int ready, FailedQueueJob? failed = null) =>
        new(ready, failed is null ? [] : [failed], [], null, 0, ExitCode.Success);

    /// <summary>Returns what the test set, after handing the guard's progress to the command as the real service does.</summary>
    private sealed class FakeRunService : IImmichRunService
    {
        public int Calls { get; private set; }
        public ImmichReuploadGuardResult Guard { get; set; } = new(0, [], [], []);
        public ImmichDiscoveryResult Discovery { get; set; } = new([], 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
        public ScanProgress[] Reports { get; set; } = [];
        public Exception? Failure { get; set; }

        public Task<ImmichQueueAddition> AddToQueueAsync(string databasePath, AppSettings settings, int? limit, IProgress<ScanProgress>? guardProgress = null, IProgress<ImmichProgress>? discoveryProgress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            foreach (var report in Reports) guardProgress?.Report(report);
            return Failure is null ? Task.FromResult(new ImmichQueueAddition(Guard, Discovery)) : Task.FromException<ImmichQueueAddition>(Failure);
        }
    }

    /// <summary>The queue: prepares what the test set and runs it to the end the test says, without touching a job.</summary>
    private sealed class FakeQueue : IQueueService
    {
        public QueuePreparationResult Queue { get; set; } = new([], [], [], []);
        public QueueRunResult Result { get; set; } = RunResult(ready: 0);
        public Exception? Failure { get; set; }

        public Task<QueuePreparationResult> PrepareAsync(string databasePath, AppSettings settings, JobSourceKind? sourceKind, bool modeOverridden = false, CancellationToken cancellationToken = default) => Task.FromResult(Queue);

        public Task<QueueRunResult> RunAsync(string databasePath, AppSettings settings, QueuePreparationResult preparation, Func<JobRecord, CancellationToken, Task<bool>>? passOver = null, IProgress<JobProgress>? progress = null, ConversionSpans? spans = null, CancellationToken stopAfterThisVideo = default, CancellationToken cancellationToken = default)
        {
            return Failure is null ? Task.FromResult(Result) : Task.FromException<QueueRunResult>(Failure);
        }
    }
}
