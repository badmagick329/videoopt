using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// What <see cref="RunFlow"/> asks the person at the front end. The console asks as the run goes; a runner with no terminal
/// asks nothing mid-run and answers from the choices made before it started (D3: the worker's fate chosen at start).
/// </summary>
public interface IRunQuestions
{
    /// <summary>Whether anyone can answer. Redirected input never does: a piped "y" is for the creation prompt.</summary>
    bool CanAsk { get; }

    /// <summary>The price quote before a server is created and billed; false declines. Matches what <see cref="IRemoteWorkerLifecycle.EnsureReadyAsync"/> takes.</summary>
    Task<bool> ConfirmWorkerCreationAsync(RemoteWorkerCreationQuote quote, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the time in which the worker question is answered during the run and says what it got. The first call decides;
    /// later calls return the same answer. Async because a runner with no terminal reads the answer from the database at the
    /// end, so a fate changed mid-run counts.
    /// </summary>
    Task<RunWorkerAnswer> AnswerGivenDuringRunAsync();

    /// <summary>
    /// "Delete the worker now?" as the run ends. Takes the answer still pending from the first stop request when there is
    /// one, else asks anew.
    /// </summary>
    /// <param name="remoteStage">The stage the stopped video was in on the worker, or null: what deleting would lose.</param>
    Task<bool> AskToDeleteWorkerAsync(RunEnd runEnd, JobStatus? remoteStage);
}

/// <summary>The worker question's state when the run ended.</summary>
/// <param name="Answer">True for y, false for N; null when not asked or not answered.</param>
/// <param name="Pending">A read is still waiting, and answers the question the run asks at its end.</param>
public sealed record RunWorkerAnswer(bool? Answer, bool Pending);
