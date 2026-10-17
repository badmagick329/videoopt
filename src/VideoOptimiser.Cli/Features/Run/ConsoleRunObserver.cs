using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// Prints what <see cref="RunFlow"/> reports, with the words of today's <c>run</c> and <c>immich run</c> and the Ctrl+C
/// wiring that goes with them: the stop scope that lasts while videos convert, and the one that lasts while the worker is
/// deleted. It decides nothing about the flow. One per call (<c>run</c>'s guard and its conversion each build their own), since
/// it holds the progress console and scopes that its own events open and close.
/// </summary>
/// <param name="beforeNext">Printed between the failed jobs and the line that says what to finalise.</param>
internal sealed class ConsoleRunObserver(LoadedConfiguration configuration, CliCommand command, RunStop stop, RunWorkerState state, Action? beforeNext) : IRunObserver
{
    private ImmichDiscoveryConsole? _restaging;
    private JobProgressConsole? _progress;
    private IDisposable? _convertingScope;
    private IDisposable? _deletingScope;

    public void VideoSkipped(JobRecord job) => CliConsole.WriteLine(SkippedJobMessage.Line(job, configuration, command));

    public void FinalisationInterrupted(JobRecord job) =>
        CliConsole.WriteLine($"{Path.GetFileName(job.SourcePath)} was interrupted while being finalised. Run '{command.Hint("finalize --ready")}' to finish it.");

    public void VideosHeld(int count) =>
        CliConsole.WriteLine(count == 1 ? "1 video is held; runs pass over it until it's released." : $"{count} videos are held; runs pass over them until they're released.");

    public IProgress<ImmichProgress> ImmichRestaging() => _restaging = new ImmichDiscoveryConsole(CliConsole.Out, OutputStyle, CliConsole.Error, ErrorStyle);

    public void ImmichVideosStayQueued(int count, string? unusableReason)
    {
        if (unusableReason is null)
        {
            CliConsole.WriteLine(ImmichStayQueued(count, "immich.baseUrl isn't set"));
            return;
        }
        // A download that stopped answering leaves its line open.
        _restaging?.FinishLine();
        CliConsole.WriteLine(ImmichStayQueued(count, $"Immich can't be used ({unusableReason})"));
    }

    /// <summary>
    /// Each video's block prints as a converted one's does. No stop scope: nothing converts yet, so Ctrl+C stops the run now,
    /// and a video whose verdict was not saved stays failed.
    /// </summary>
    public IProgress<JobProgress> RecheckStarted(int count)
    {
        CliConsole.WriteLine(RecheckWords.Started(count));
        return new JobProgressConsole(CliConsole.Out, OutputStyle);
    }

    /// <summary>Set apart from the last video's block, as each block is from the one before.</summary>
    public void Rechecked(int ready, int toCheck, int notWorthConverting, int stillFailed)
    {
        CliConsole.WriteLine();
        CliConsole.WriteLine(RecheckWords.Tally(ready, toCheck, notWorthConverting, stillFailed, OutputStyle.Tone));
    }

    public void WorkerProgress(string line) => CliConsole.WriteLine(line);

    /// <summary>
    /// While a video converts, the first Ctrl+C stops after it (<see cref="RunStop"/>): the line says so, with the encode's
    /// time left when it has reported one, and with a managed worker up, the worker question follows. Ending the scope ends
    /// the question's time during the run and prints any held lines.
    /// </summary>
    public IProgress<JobProgress> ConversionStarted(int count, bool workerUp)
    {
        CliConsole.WriteLine($"Converting {count} {(count == 1 ? "video" : "videos")}.");
        var progress = _progress = new JobProgressConsole(CliConsole.Out, OutputStyle);
        _convertingScope = stop.WhileConverting(() =>
        {
            progress.WriteNote(StoppedRunMessage.StoppingAfterThisVideo(progress.EncodeTimeLeft));
            if (workerUp && state.Question.CanAsk) state.Question.Ask(progress);
        });
        return progress;
    }

    public void ConversionEnded()
    {
        _convertingScope?.Dispose();
        _ = state.Question.TakeOver();
    }

    public void JoiningVideoSkipped(JobRecord job) => _progress!.WriteNote(SkippedJobMessage.Line(job, configuration, command));

    public void JoiningVideoWaitsForWorker(JobRecord job) =>
        _progress!.WriteNote($"{Path.GetFileName(job.SourcePath)} waits for the next run: it needs a worker, and this run has none.");

    public void StoppedByUnreachableWorker(string workerName, int notConverted)
    {
        CliConsole.WriteLine();
        CliConsole.WriteLine(UnreachableWorkerMessage.Stopped(workerName, notConverted));
    }

    public void StoppedAfter(JobRecord last, int notStarted)
    {
        CliConsole.WriteLine();
        CliConsole.WriteLine(StoppedRunMessage.StoppedAfter(last.SourcePath, notStarted));
    }

    public void Summary(int ready, int skipped, IReadOnlyList<FailedQueueJob> failures, int readyTotal, bool stoppedAfter, int toCheck)
    {
        if (OutcomeSummary.Run(ready, skipped, failures.Count, OutputStyle, othersReady: readyTotal > 0, toCheck) is { } summary) CliConsole.WriteLine(summary);
        PrintFailedJobs(failures);
        beforeNext?.Invoke();
        // finalize --ready finalises every source, so one line serves a queue of folder and Immich videos.
        if (readyTotal > 0) CliConsole.WriteLine(NextSteps.ReadyToFinaliseLine(readyTotal, command));
        if (stoppedAfter) CliConsole.WriteLine(StoppedRunMessage.ConvertTheRest(command));
    }

    /// <summary>
    /// A manual host hears what is still running there, and otherwise, on this PC or before any worker existed, the video
    /// continues from its last finished step.
    /// </summary>
    public void StoppedWithoutManagedWorker(JobInterruptedException? interrupted, string? manualHost)
    {
        IReadOnlyList<string> lines = manualHost is null ? [] : StoppedRunMessage.Lines(interrupted, manualHost, managed: false, command);
        if (lines.Count == 0) lines = StoppedRunMessage.StoppedNow(interrupted, command);
        // Sets the lines apart from the job's block, and ends a creation prompt that Ctrl+C left open.
        CliConsole.WriteLine();
        PrintLines(lines);
    }

    public void StoppedNow(JobInterruptedException? interrupted, string worker, WorkerFate fate, bool settingUpWorker)
    {
        CliConsole.WriteLine();
        PrintLines(fate switch
        {
            // Nothing to lose, and the user said y: the video resumes on a new worker.
            WorkerFate.Delete => StoppedRunMessage.StoppedNow(interrupted, command),
            WorkerFate.Keep => StoppedRunMessage.Lines(interrupted, worker, managed: true, command, settingUpWorker),
            _ => StoppedRunMessage.LinesBeforeQuestion(interrupted, worker, command, settingUpWorker)
        });
    }

    public void WorkerUnreachableAtEnd(JobRecord job, bool questionFollows)
    {
        CliConsole.WriteLine();
        var lines = UnreachableWorkerMessage.Lines(job, managed: true, command);
        PrintLines(questionFollows ? lines.SkipLast(1) : lines);
    }

    public void WorkerKept(string worker, RunEnd runEnd, JobStatus? remoteStage, bool answeredNo)
    {
        if (runEnd is RunEnd.StoppedNow or RunEnd.WorkerUnreachable)
        {
            // A stop's lines and U3 already say the worker stays and how to delete it; only the answer N adds the later command.
            if (answeredNo) CliConsole.WriteLine(StoppedRunMessage.DeleteLater(remoteStage, command));
            return;
        }
        PrintLines(WorkerKeepLines.For(worker, command));
    }

    public void WorkerDeleting() => _deletingScope = stop.WhileDeleting(() => CliConsole.WriteLine(WorkerEndMessage.DeletingPress));

    public void WorkerDeleteFailed(string worker, string message)
    {
        foreach (var line in WorkerEndMessage.DeleteFailed(worker, message, command)) WriteError(line);
    }

    public void WorkerDeleteEnded() => _deletingScope?.Dispose();

    /// <summary>U3m (VOPT-69): a manual host that stopped answering is neither billed nor deleted by this tool, so the user hears only how to resume.</summary>
    public void ManualWorkerUnreachable(JobRecord job)
    {
        CliConsole.WriteLine();
        PrintLines(UnreachableWorkerMessage.Lines(job, managed: false, command));
    }

    public void ManualWorkerReminder() =>
        CliConsole.WriteLine("Remote worker lifecycle is manual: after confirming all expected outputs are ready to finalise, delete the worker; powering it off still bills.");

    public int RunFailed(Exception exception) => ReportFailure(exception);

    private static string ImmichStayQueued(int count, string reason) =>
        count == 1 ? $"1 Immich video stays queued: {reason}." : $"{count} Immich videos stay queued: {reason}.";

    private static void PrintFailedJobs(IReadOnlyList<FailedQueueJob> failedJobs)
    {
        if (failedJobs.Count == 0) return;
        CliConsole.WriteLine();
        CliConsole.WriteLine(OutputStyle.Red("Failed jobs:"));
        foreach (var job in failedJobs)
        {
            CliConsole.WriteLine($"{job.Id:N}  {Path.GetFileName(job.SourcePath)}");
            CliConsole.WriteLine(FailureWords.Describe(job.FailureCategory, job.FailureMessage, sentence: true) ?? job.FailureMessage);
        }
    }

    private static void PrintLines(IEnumerable<string> lines)
    {
        foreach (var line in lines) CliConsole.WriteLine(line);
    }
}
