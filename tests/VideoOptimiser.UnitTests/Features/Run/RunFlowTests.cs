using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// The run's flow with no terminal: a recording observer and a scripted questions port stand in for the CLI, so a front end
/// that prints nothing (a background runner) or draws its own screen (a TUI) can drive the same flow. What the CLI prints
/// from these events is covered through the real CLI (<see cref="WorkerEndRunTests"/>).
/// </summary>
public sealed class RunFlowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeWorkerLifecycle _lifecycle = new();
    private readonly RecordingRunObserver _observer = new();
    private readonly ScriptedRecheckValidator _validator = new();

    public RunFlowTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    /// <summary>
    /// A stop now while a video converts on this PC, after the user answered y during the run: nothing on the worker is lost,
    /// so the worker is deleted without asking again, and the stop still exits 9.
    /// </summary>
    [Fact]
    public async Task AStopNowAfterAYesDeletesTheWorkerWithoutAskingAgain()
    {
        var jobs = await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        using var stopNow = new CancellationTokenSource();
        var processor = new ScriptedRunProcessor(remote: false);
        processor.During[0] = _ => { stopNow.Cancel(); return Task.CompletedTask; };
        var questions = new ScriptedQuestions(new RunWorkerAnswer(true, Pending: false));

        var code = await RunAsync(processor, questions, stopNow.Token);

        code.Should().Be((int)ExitCode.Cancelled);
        _lifecycle.DeleteCalls.Should().Be(1);
        questions.Asked.Should().BeFalse();
        _observer.Events.Should().ContainInOrder("ConversionStarted:1,workerUp=True", "ConversionEnded", "StoppedNow:Delete", "WorkerDeleting", "WorkerDeleteEnded");
        _observer.WorkerLines.Should().Contain("Worker deleted; billing for it and its IPv4 address has stopped.");
        (await RunCli.StatusesAsync(_repository, DatabasePath, jobs)).Should().Equal(JobStatus.Interrupted);
    }

    /// <summary>
    /// An N given during the run keeps the worker even though a finished run that used it would delete it: the user's answer
    /// outranks the setting.
    /// </summary>
    [Fact]
    public async Task AFinishedRunAfterANoKeepsTheWorkerAndDeletesNothing()
    {
        var jobs = await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        var processor = new ScriptedRunProcessor(remote: true);
        var questions = new ScriptedQuestions(new RunWorkerAnswer(false, Pending: false));

        var code = await RunAsync(processor, questions, CancellationToken.None);

        code.Should().Be((int)ExitCode.Success);
        _lifecycle.EnsureCalls.Should().Be(1);
        _lifecycle.DeleteCalls.Should().Be(0);
        _observer.Events.Should().Contain("WorkerKept:Finished").And.NotContain("WorkerDeleting");
        _observer.Events.Should().Contain("Summary:ready=1,total=1,stoppedAfter=False");
        (await RunCli.StatusesAsync(_repository, DatabasePath, jobs)).Should().Equal(JobStatus.ReadyToFinalize);
    }

    /// <summary>The scenarios of <see cref="TheWorkerFateChosenAtStartIsAppliedOnEveryWayABackgroundRunEnds"/>: how the run ends.</summary>
    public enum BackgroundEnd
    {
        Finished,
        StoppedAfterVideo,
        StoppedNowWithWorkOnTheWorker,
        StoppedNowWithoutWork,
        WorkerUnreachable,
        Failed
    }

    /// <summary>
    /// D3's done criterion (VOPT-110): a background run asks nobody, so the fate chosen at start is the answer, and it decides at
    /// every end. Delete deletes even when a stop now throws away work only the worker has (nobody can be asked again), and
    /// Keep keeps whatever the settings say about a finished run. The unreachable-worker end deletes silently under Delete, as a
    /// y does; under Keep it leaves the worker with the unreachable lines.
    /// </summary>
    [Theory]
    [InlineData(BackgroundEnd.Finished, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.Finished, WorkerFateChoice.Keep)]
    [InlineData(BackgroundEnd.StoppedAfterVideo, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.StoppedAfterVideo, WorkerFateChoice.Keep)]
    [InlineData(BackgroundEnd.StoppedNowWithWorkOnTheWorker, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.StoppedNowWithWorkOnTheWorker, WorkerFateChoice.Keep)]
    [InlineData(BackgroundEnd.StoppedNowWithoutWork, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.StoppedNowWithoutWork, WorkerFateChoice.Keep)]
    [InlineData(BackgroundEnd.WorkerUnreachable, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.WorkerUnreachable, WorkerFateChoice.Keep)]
    [InlineData(BackgroundEnd.Failed, WorkerFateChoice.Delete)]
    [InlineData(BackgroundEnd.Failed, WorkerFateChoice.Keep)]
    public async Task TheWorkerFateChosenAtStartIsAppliedOnEveryWayABackgroundRunEnds(BackgroundEnd end, WorkerFateChoice fate)
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 2);
        using var stopNow = new CancellationTokenSource();
        using var stopAfter = new CancellationTokenSource();
        var processor = new ScriptedRunProcessor(remote: end != BackgroundEnd.StoppedNowWithoutWork);
        switch (end)
        {
            case BackgroundEnd.StoppedAfterVideo:
                processor.During[0] = _ => { stopAfter.Cancel(); return Task.CompletedTask; };
                break;
            case BackgroundEnd.StoppedNowWithWorkOnTheWorker or BackgroundEnd.StoppedNowWithoutWork:
                processor.During[0] = _ => { stopNow.Cancel(); return Task.CompletedTask; };
                break;
            case BackgroundEnd.WorkerUnreachable:
                processor.Unreachable[0] = true;
                break;
            case BackgroundEnd.Failed:
                processor.Throw[0] = new InvalidOperationException("the disk broke");
                break;
        }
        var requests = new SqliteRunRequestStore(new SqliteDatabaseInitializer());
        await requests.ResetAsync(DatabasePath, fate, CancellationToken.None);

        _ = await RunAsync(processor, new BackgroundRunQuestions(requests, DatabasePath, fate), stopNow.Token, stopAfter.Token);

        _lifecycle.DeleteCalls.Should().Be(fate == WorkerFateChoice.Delete ? 1 : 0);
        // The end it was meant to reach, and what the worker's fate looked like there.
        var (reached, kept) = end switch
        {
            BackgroundEnd.Finished => ("Summary:ready=2,total=2,stoppedAfter=False", "WorkerKept:Finished"),
            BackgroundEnd.StoppedAfterVideo => ("StoppedAfter", "WorkerKept:StoppedAfterVideo"),
            BackgroundEnd.StoppedNowWithWorkOnTheWorker => ("StoppedNow:" + fate, null),
            BackgroundEnd.StoppedNowWithoutWork => ("StoppedNow:" + fate, null),
            BackgroundEnd.WorkerUnreachable => ("StoppedByUnreachableWorker", "WorkerUnreachableAtEnd"),
            _ => ("RunFailed", "WorkerKept:Failed")
        };
        _observer.Events.Should().Contain(reached);
        if (fate == WorkerFateChoice.Delete) _observer.Events.Should().Contain("WorkerDeleting").And.NotContain("WorkerKept:" + end);
        else if (kept is not null) _observer.Events.Should().Contain(kept).And.NotContain("WorkerDeleting");
        else _observer.Events.Should().NotContain("WorkerDeleting");
    }

    /// <summary>
    /// The fate is read when the run ends, not when it starts, so a change made while a video converts decides: that is what
    /// lets a person keep the worker after starting the run to delete it, or the reverse.
    /// </summary>
    [Theory]
    [InlineData(WorkerFateChoice.Delete, WorkerFateChoice.Keep)]
    [InlineData(WorkerFateChoice.Keep, WorkerFateChoice.Delete)]
    public async Task AFateChangedWhileTheRunGoesIsTheOneAppliedAtTheEnd(WorkerFateChoice atStart, WorkerFateChoice changedTo)
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        var requests = new SqliteRunRequestStore(new SqliteDatabaseInitializer());
        await requests.ResetAsync(DatabasePath, atStart, CancellationToken.None);
        var processor = new ScriptedRunProcessor(remote: true);
        processor.During[0] = _ => requests.SetWorkerFateAsync(DatabasePath, changedTo, CancellationToken.None);

        _ = await RunAsync(processor, new BackgroundRunQuestions(requests, DatabasePath, atStart), CancellationToken.None);

        _lifecycle.DeleteCalls.Should().Be(changedTo == WorkerFateChoice.Delete ? 1 : 0);
    }

    /// <summary>
    /// A busy or locked database as the run ends must not leave the worker's fate undecided: with nobody watching a background
    /// run, an undecided worker bills on unnoticed. The fate chosen at launch decides instead.
    /// </summary>
    [Theory]
    [InlineData(WorkerFateChoice.Delete)]
    [InlineData(WorkerFateChoice.Keep)]
    public async Task AnUnreadableRequestsRowAtTheEndAppliesTheFateChosenAtLaunch(WorkerFateChoice atStart)
    {
        await RunCli.QueueJobsAsync(_repository, DatabasePath, 1);
        var processor = new ScriptedRunProcessor(remote: true);

        _ = await RunAsync(processor, new BackgroundRunQuestions(new UnreadableRequests(), DatabasePath, atStart), CancellationToken.None);

        _lifecycle.DeleteCalls.Should().Be(atStart == WorkerFateChoice.Delete ? 1 : 0);
        _observer.Events.Should().NotContain(e => e.StartsWith("RunFailed", StringComparison.Ordinal));
    }

    private sealed class UnreadableRequests : IRunRequestStore
    {
        public Task ResetAsync(string databasePath, WorkerFateChoice? fate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<RunRequests> ReadAsync(string databasePath, CancellationToken cancellationToken) => throw new IOException("database is locked");
        public Task RequestStopAsync(string databasePath, bool now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetWorkerFateAsync(string databasePath, WorkerFateChoice fate, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// VOPT-119: a video that failed validation under older rules is checked again at the start of a run, even with nothing
    /// queued, and a verdict of ready with warnings readies it. The worker is not asked about, let alone created.
    /// </summary>
    [Fact]
    public async Task AFailedVideoFromOlderRulesIsReadiedWithItsWarningsBeforeAnyWorkerIsTouched()
    {
        _lifecycle.Exists = false;
        var (job, output, _) = await SeedFailedValidationAsync(rulesVersion: 0);
        _validator.Report = new ValidationReport { Passed = true, Warnings = ["audio lasts 2 s longer"], SourceSizeBytes = 100, OutputSizeBytes = 50, PercentageSaved = 50 };

        var code = await RunAsync(new ScriptedRunProcessor(remote: true), new ScriptedQuestions(new RunWorkerAnswer(false, Pending: false)), CancellationToken.None);

        code.Should().Be((int)ExitCode.Success);
        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.ReadyToFinalize);
        stored.FailureCategory.Should().BeNull();
        stored.ValidationWarnings.Should().Equal("audio lasts 2 s longer");
        _observer.Events.Should().ContainInOrder("RecheckStarted:1", "Rechecked:1,1,0,0");
        _observer.ProgressTaken.Should().Contain(text => text.StartsWith("Checking the output", StringComparison.Ordinal));
        _lifecycle.EnsureCalls.Should().Be(0);
        _lifecycle.DeleteCalls.Should().Be(0);
        (await new OutputManifestStore().LoadAsync(output)).Validation!.RulesVersion.Should().Be(ValidationReport.CurrentRulesVersion);
    }

    public enum LeftAlone
    {
        ALaterJobForTheSameOriginal,
        Av1Missing,
        ManifestMissing,
        OriginalChangedAfterTheEncode,
        ReportAlreadyAtTheCurrentRules
    }

    /// <summary>
    /// A failed video that does not meet every condition is left exactly as it was and the run says nothing: a later job owns
    /// the original, or there is nothing to check, or the check would not be about the same original, or the current rules
    /// already failed it (else every run would check it again).
    /// </summary>
    [Theory]
    [InlineData(LeftAlone.ALaterJobForTheSameOriginal)]
    [InlineData(LeftAlone.Av1Missing)]
    [InlineData(LeftAlone.ManifestMissing)]
    [InlineData(LeftAlone.OriginalChangedAfterTheEncode)]
    [InlineData(LeftAlone.ReportAlreadyAtTheCurrentRules)]
    public async Task AFailedVideoThatDoesNotQualifyIsLeftAloneAndNothingIsRaised(LeftAlone reason)
    {
        _lifecycle.Exists = false;
        var (job, output, original) = await SeedFailedValidationAsync(reason == LeftAlone.ReportAlreadyAtTheCurrentRules ? ValidationReport.CurrentRulesVersion : 0);
        switch (reason)
        {
            case LeftAlone.ALaterJobForTheSameOriginal:
                await Task.Delay(5);
                _ = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = original, Status = JobStatus.Cancelled });
                break;
            case LeftAlone.Av1Missing:
                File.Delete(output);
                break;
            case LeftAlone.ManifestMissing:
                File.Delete(new OutputManifestStore().GetPath(output));
                break;
            case LeftAlone.OriginalChangedAfterTheEncode:
                await File.WriteAllTextAsync(original, "edited after the encode");
                break;
        }

        _ = await RunAsync(new ScriptedRunProcessor(), new ScriptedQuestions(new RunWorkerAnswer(false, Pending: false)), CancellationToken.None);

        (await _repository.GetAsync(DatabasePath, job.Id))!.Status.Should().Be(JobStatus.Failed);
        _observer.Events.Should().NotContain(entry => entry.StartsWith("Recheck", StringComparison.Ordinal));
        _validator.Calls.Should().Be(0);
    }

    /// <summary>The AV1 saves too little to keep under the savings settings: the job ends skipped, as in a run, and the AV1 and its record go.</summary>
    [Fact]
    public async Task ARecheckedVideoThatIsNotWorthConvertingEndsSkippedAndItsAv1IsDeleted()
    {
        _lifecycle.Exists = false;
        var (job, output, _) = await SeedFailedValidationAsync(rulesVersion: 0);
        _validator.Report = new ValidationReport { Passed = false, NotWorthConverting = "It saves 3%, under the 10% minimum.", SourceSizeBytes = 100, OutputSizeBytes = 97, PercentageSaved = 3 };

        _ = await RunAsync(new ScriptedRunProcessor(), new ScriptedQuestions(new RunWorkerAnswer(false, Pending: false)), CancellationToken.None);

        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.Skipped);
        stored.FailureCategory.Should().Be(FailureCategories.NotWorthConverting);
        File.Exists(output).Should().BeFalse();
        File.Exists(new OutputManifestStore().GetPath(output)).Should().BeFalse();
        _observer.Events.Should().ContainInOrder("RecheckStarted:1", "Rechecked:0,0,1,0");
    }

    /// <summary>
    /// A video that fails the current rules too stays failed with the new message, and the record now carries the current
    /// rules version, so the next run does not check it again.
    /// </summary>
    [Fact]
    public async Task AVideoThatFailsTheCurrentRulesStaysFailedAndIsNotCheckedAgainByTheNextRun()
    {
        _lifecycle.Exists = false;
        var (job, _, _) = await SeedFailedValidationAsync(rulesVersion: 0);
        _validator.Report = new ValidationReport { Passed = false, Failures = ["The output has no video stream."] };
        var questions = new ScriptedQuestions(new RunWorkerAnswer(false, Pending: false));

        _ = await RunAsync(new ScriptedRunProcessor(), questions, CancellationToken.None);

        var stored = (await _repository.GetAsync(DatabasePath, job.Id))!;
        stored.Status.Should().Be(JobStatus.Failed);
        stored.FailureMessage.Should().Be("The output has no video stream.");
        _observer.Events.Should().ContainInOrder("RecheckStarted:1", "Rechecked:0,0,0,1");

        _observer.Events.Clear();
        _ = await RunAsync(new ScriptedRunProcessor(), questions, CancellationToken.None);

        _observer.Events.Should().NotContain(entry => entry.StartsWith("Recheck", StringComparison.Ordinal));
        _validator.Calls.Should().Be(1);
    }

    /// <summary>A failed validation job with its original, AV1 and record on disk, the record's report written under <paramref name="rulesVersion"/>.</summary>
    private async Task<(JobRecord Job, string Output, string Original)> SeedFailedValidationAsync(int rulesVersion)
    {
        var original = Path.Combine(_directory, "clip.mp4");
        var output = Path.Combine(_directory, "clip.av1.mp4");
        await File.WriteAllTextAsync(original, "original");
        await File.WriteAllTextAsync(output, "av1");
        var store = new OutputManifestStore();
        await store.SaveAsync(new OutputManifest
        {
            SourcePath = original,
            SourceFingerprint = await new FileFingerprintService().CreateAsync(original),
            OutputPath = output,
            Validation = new ValidationReport { Passed = false, Failures = ["old rule"], RulesVersion = rulesVersion }
        });
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = original,
            OutputPath = output,
            Status = JobStatus.Failed,
            FailureCategory = FailureCategories.ValidationFailed,
            FailureMessage = "old rule"
        });
        return (job, output, original);
    }

    private sealed class ScriptedRecheckValidator : IOutputValidationService
    {
        public ValidationReport Report { get; set; } = new() { Passed = true };
        public int Calls { get; private set; }

        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Report);
        }
    }

    private async Task<int> RunAsync(ScriptedRunProcessor processor, IRunQuestions questions, CancellationToken stopNow, CancellationToken stopAfterThisVideo = default)
    {
        var settings = Settings();
        var locks = new DatabaseLocks();
        var flow = new RunFlow(new QueueService(_repository, processor, locks, new CommandHints(null)), new QueueDiscovery(null!, _repository, new FileFingerprintService(), locks), processor, _lifecycle, new NoImmichRestaging(), new FailedValidationRecheck(_repository, new OutputManifestStore(), new FileFingerprintService(), _validator), new RecordingRunRecords(), TimeProvider.System);
        flow.MarkStarted(Guid.NewGuid());
        // The same shape as the CLI's: the guard decides the worker's fate for an end the conversion does not decide itself.
        return await flow.GuardAsync(settings, _observer, questions, async () => (int)await flow.ConvertQueueAsync(settings, modeOverridden: false, _observer, questions, stopAfterThisVideo, stopNow));
    }

    private AppSettings Settings() => new()
    {
        Database = new DatabaseSettings { Path = DatabasePath },
        Processing = new ProcessingSettings { Mode = ProcessingModes.RemoteSsh, RemoteSsh = new RemoteSshSettings { Lifecycle = RemoteLifecycleModes.Hetzner } }
    };

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ScriptedQuestions(RunWorkerAnswer answer) : IRunQuestions
    {
        public bool Asked { get; private set; }
        public bool CanAsk => false;
        public Task<bool> ConfirmWorkerCreationAsync(RemoteWorkerCreationQuote quote, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<RunWorkerAnswer> AnswerGivenDuringRunAsync() => Task.FromResult(answer);

        public Task<bool> AskToDeleteWorkerAsync(RunEnd runEnd, JobStatus? remoteStage)
        {
            Asked = true;
            return Task.FromResult(false);
        }
    }
}
