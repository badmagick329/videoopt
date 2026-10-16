using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// Asks the questions <see cref="RunFlow"/> asks, at the console. The worker question that waits during the run is
/// <see cref="WorkerQuestion"/> on <see cref="RunWorkerState"/>, which the observer starts at the first Ctrl+C; this adapter
/// takes it over when the run ends and asks the end question beside it.
/// </summary>
internal sealed class ConsoleRunQuestions(RunWorkerState state) : IRunQuestions
{
    public bool CanAsk => state.Question.CanAsk;

    public Task<bool> ConfirmWorkerCreationAsync(RemoteWorkerCreationQuote quote, CancellationToken cancellationToken) =>
        ConsolePrompts.ConfirmWorkerCreationAsync(quote, cancellationToken);

    public Task<RunWorkerAnswer> AnswerGivenDuringRunAsync()
    {
        var answer = state.Question.TakeOver();
        return Task.FromResult(new RunWorkerAnswer(answer.Answer, answer.Pending is not null));
    }

    public async Task<bool> AskToDeleteWorkerAsync(RunEnd runEnd, JobStatus? remoteStage)
    {
        var stopLike = runEnd is RunEnd.StoppedNow or RunEnd.WorkerUnreachable;
        Console.Write(stopLike ? StoppedRunMessage.DeleteNowQuestion(remoteStage) : WorkerEndMessage.QuestionAtEnd);
        // A read left from the first press may have been ended by the second press rather than by the user.
        var response = state.Question.TakeOver().Pending is { } pending ? await pending ?? await state.Question.ReadAsync() : await state.Question.ReadAsync();
        if (response is null) Console.WriteLine();
        return WorkerEndPolicy.IsAffirmative(response);
    }
}
