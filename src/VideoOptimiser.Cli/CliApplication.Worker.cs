using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain.Jobs;
using static CliOutput;

/// <summary>
/// What happens to the managed worker when a run ends (VOPT-54 step 3). Every end decides (<see cref="WorkerEndPolicy"/>), so
/// none leaves a worker billing without a line that says so. The question and the delete run on their own token: the run's
/// is already cancelled after a stop, and a cancelled delete would leave the worker billing silently.
/// </summary>
internal static partial class CliApplication
{
    /// <summary>
    /// While a video converts, the first Ctrl+C stops after it (<see cref="RunStop"/>): the line says so, with the encode's
    /// time left when it has reported one, and with a managed worker up, the worker question follows. Disposing it ends the
    /// question's time during the run and prints any held lines.
    /// </summary>
    private static ConversionScope StopAfterThisVideoAnnounced(IServiceProvider services, JobProgressConsole progress)
    {
        var state = services.GetRequiredService<RunWorkerState>();
        var stopScope = services.GetRequiredService<RunStop>().WhileConverting(() =>
        {
            progress.WriteNote(StoppedRunMessage.StoppingAfterThisVideo(progress.EncodeTimeLeft));
            if (state.AskAtFirstPress && state.Question.CanAsk) state.Question.Ask(progress);
        });
        return new ConversionScope(stopScope, state.Question);
    }

    /// <summary>Asks the worker question at the first Ctrl+C only when a managed worker is up; read from the local state file.</summary>
    private static async Task PrepareWorkerQuestionAsync(IServiceProvider services, LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        services.GetRequiredService<RunWorkerState>().AskAtFirstPress = lifecycle.IsManaged(configuration.Settings) &&
            await lifecycle.ReadRecordedAsync(configuration.Settings.Database.Path, cancellationToken) is not null;
    }

    /// <summary>Decides the managed worker's fate as the run ends and prints what happened to it.</summary>
    /// <param name="interrupted">For <see cref="RunEnd.StoppedNow"/>, the video it stopped, or null when none was converting.</param>
    /// <param name="unreachableJob">For <see cref="RunEnd.WorkerUnreachable"/>, the video the worker left interrupted.</param>
    /// <returns>False when deleting the worker failed.</returns>
    private static async Task<bool> ConcludeWorkerAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, RunEnd end, JobInterruptedException? interrupted = null, JobRecord? unreachableJob = null)
    {
        var state = services.GetRequiredService<RunWorkerState>();
        state.Concluded = true;
        var answer = state.Question.TakeOver();
        var settings = configuration.Settings;
        var lifecycle = services.GetRequiredService<IRemoteWorkerLifecycle>();
        var immichJobsLeft = await ImmichJobsLeftAsync(services, configuration, command);
        var recorded = lifecycle.IsManaged(settings) ? await lifecycle.ReadRecordedAsync(settings.Database.Path, CancellationToken.None) : null;
        if (recorded is null)
        {
            if (end == RunEnd.StoppedNow) await ReportStopWithoutManagedWorkerAsync(services, configuration, command, interrupted, immichJobsLeft);
            return true;
        }

        var worker = recorded.Name;
        JobStatus? remoteStage = end switch
        {
            RunEnd.StoppedNow => interrupted is { Remote: true } ? interrupted.Stage : null,
            RunEnd.WorkerUnreachable => unreachableJob?.ResumeStatus,
            _ => null
        };
        var deleteLosesWork = StoppedRunMessage.LostSuffix(remoteStage).Length > 0;
        var fate = answer.Pending is not null
            ? WorkerFate.Ask
            : WorkerEndPolicy.Decide(end, answer.Answer, settings.Processing.RemoteSsh.Hetzner.DeleteAfterRun, state.Used, deleteLosesWork, state.Question.CanAsk);
        var stopLike = end is RunEnd.StoppedNow or RunEnd.WorkerUnreachable;

        if (end == RunEnd.StoppedNow)
        {
            Console.WriteLine();
            PrintLines(fate switch
            {
                // Nothing to lose, and the user said y: the video resumes on a new worker.
                WorkerFate.Delete => StoppedRunMessage.StoppedNow(interrupted, command, immichJobsLeft),
                WorkerFate.Keep => StoppedRunMessage.Lines(interrupted, worker, managed: true, command, immichJobsLeft),
                _ => StoppedRunMessage.LinesBeforeQuestion(interrupted, worker, command, immichJobsLeft)
            });
        }
        else if (end == RunEnd.WorkerUnreachable && fate != WorkerFate.Delete)
        {
            Console.WriteLine();
            var lines = UnreachableWorkerMessage.Lines(unreachableJob!, managed: true, command, immichJobsLeft);
            PrintLines(fate == WorkerFate.Ask ? lines.SkipLast(1) : lines);
        }

        if (fate == WorkerFate.Ask)
        {
            Console.Write(stopLike ? StoppedRunMessage.DeleteNowQuestion(remoteStage) : WorkerEndMessage.QuestionAtEnd);
            // A read left from the first press may have been ended by the second press rather than by the user.
            var response = answer.Pending is { } pending ? await pending ?? await state.Question.ReadAsync() : await state.Question.ReadAsync();
            if (response is null) Console.WriteLine();
            if (!WorkerEndPolicy.IsAffirmative(response))
            {
                if (stopLike) Console.WriteLine(StoppedRunMessage.DeleteLater(remoteStage, command));
                else PrintLines(WorkerEndMessage.Keep(worker, command));
                return true;
            }
            fate = WorkerFate.Delete;
        }

        if (fate == WorkerFate.Keep)
        {
            // A stop's lines and U3 already say the worker stays and how to delete it.
            if (!stopLike) PrintLines(WorkerEndMessage.Keep(worker, command));
            return true;
        }
        return await DeleteWorkerAsync(services, configuration, command, worker);
    }

    /// <summary>
    /// A stop now with no managed worker up: a manual host hears what is still running there, and otherwise, on this PC or
    /// before any worker existed, the video continues from its last finished step.
    /// </summary>
    private static async Task ReportStopWithoutManagedWorkerAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, JobInterruptedException? interrupted, int? immichJobsLeft)
    {
        var settings = configuration.Settings;
        IReadOnlyList<string> lines = [];
        if (settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) && !services.GetRequiredService<IRemoteWorkerLifecycle>().IsManaged(settings))
        {
            lines = StoppedRunMessage.Lines(interrupted, settings.Processing.RemoteSsh.Host, managed: false, command, immichJobsLeft);
        }
        if (lines.Count == 0) lines = StoppedRunMessage.StoppedNow(interrupted, command, immichJobsLeft);
        // Sets the lines apart from the job's block, and ends a creation prompt that Ctrl+C left open.
        Console.WriteLine();
        PrintLines(lines);
    }

    /// <returns>False when the delete failed; the lines then say the worker still bills and how to delete it.</returns>
    private static async Task<bool> DeleteWorkerAsync(IServiceProvider services, LoadedConfiguration configuration, CliCommand command, string worker)
    {
        using var deleting = services.GetRequiredService<RunStop>().WhileDeleting(() => Console.WriteLine(WorkerEndMessage.DeletingPress));
        try
        {
            await services.GetRequiredService<IRemoteWorkerLifecycle>().DeleteAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(Console.WriteLine), CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            foreach (var line in WorkerEndMessage.DeleteFailed(worker, exception.GetBaseException().Message, command)) WriteError(line);
            return false;
        }
    }

    private static void PrintLines(IEnumerable<string> lines)
    {
        foreach (var line in lines) Console.WriteLine(line);
    }

    private sealed class ConversionScope(IDisposable stopScope, WorkerQuestion question) : IDisposable
    {
        public void Dispose()
        {
            stopScope.Dispose();
            _ = question.TakeOver();
        }
    }
}

/// <summary>
/// The worker's side of one run command, shared by the code that converts and the code that ends the run. One per command,
/// registered as a service.
/// </summary>
internal sealed class RunWorkerState(IConsoleInput input)
{
    /// <summary>Set once the run holds its lock: from then on every end decides the worker's fate.</summary>
    public bool Started { get; set; }

    /// <summary>Whether this run used the managed worker; <c>deleteAfterRun</c> applies only then.</summary>
    public bool Used { get; set; }

    /// <summary>Whether a managed worker was up when the conversion began, so the first Ctrl+C asks about it.</summary>
    public bool AskAtFirstPress { get; set; }

    /// <summary>Set once the run has decided; an error after that is not a second end.</summary>
    public bool Concluded { get; set; }

    public WorkerQuestion Question { get; } = new(input);
}
