using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// The questions of a run with no terminal: it asks none. The worker was chosen before launch, and its fate is a standing
/// answer in the requests row that anyone can change while the run goes, so a stop, a failure or the end of the queue acts on
/// the latest choice and nothing bills while waiting for a reply.
/// </summary>
/// <param name="startFate">The fate chosen at launch: the answer when the requests row can't be read as the run ends.</param>
/// <param name="mayCreateWorker">
/// False when the launcher chose the worker that is up (VOPT-154): the lifecycle asks only when no worker exists, so that
/// worker was deleted after the choice, and creating the first server that fits would bill for one nobody chose.
/// </param>
public sealed class BackgroundRunQuestions(IRunRequestStore requests, string databasePath, WorkerFateChoice startFate, bool mayCreateWorker) : IRunQuestions
{
    /// <summary>Never: with nobody to ask, <see cref="WorkerEndPolicy"/> turns the fate into the answer given, never <see cref="WorkerFate.Ask"/>.</summary>
    public bool CanAsk => false;

    /// <summary>The launch already answered: a plain <c>background start</c> lets the run create its worker, the TUI's choice of a worker doesn't.</summary>
    public Task<bool> ConfirmWorkerCreationAsync(RemoteWorkerCreationQuote quote, CancellationToken cancellationToken) => Task.FromResult(mayCreateWorker);

    /// <remarks>
    /// Read on a token of its own: the run's is already cancelled after a stop, and the fate of a stopped run is the one that
    /// matters most. A fate that was never set (null) leaves the settings to decide, as a foreground run does. A failed read
    /// (a busy or locked database) falls back to the fate chosen at launch: throwing here would leave the worker's fate
    /// undecided, and with nobody watching, the worker would bill on unnoticed.
    /// </remarks>
    public async Task<RunWorkerAnswer> AnswerGivenDuringRunAsync()
    {
        WorkerFateChoice? fate;
        try
        {
            fate = (await requests.ReadAsync(databasePath, CancellationToken.None)).WorkerFate;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            fate = startFate;
        }
        return new RunWorkerAnswer(fate switch
        {
            WorkerFateChoice.Delete => true,
            WorkerFateChoice.Keep => false,
            _ => null
        }, Pending: false);
    }

    /// <summary>Unreachable: <see cref="CanAsk"/> is false and no answer is ever pending, so the flow never asks.</summary>
    public Task<bool> AskToDeleteWorkerAsync(RunEnd runEnd, JobStatus? remoteStage) =>
        throw new InvalidOperationException("A background run never asks about the worker.");
}
