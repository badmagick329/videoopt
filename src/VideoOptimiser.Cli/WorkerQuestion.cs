using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Cli;

/// <summary>
/// "Delete the worker when the run stops? [y/N]", asked at the first Ctrl+C rather than when the run stops (VOPT-54 step 3):
/// the video can take 30 minutes or more to finish, and the worker bills while a question waits. The user answers and walks
/// away. While it waits the job's lines are held and the video keeps converting.
/// </summary>
/// <remarks>
/// The read runs on its own thread, since Console.ReadLine cannot be cancelled. When the conversion ends first, the run takes
/// the question over (<see cref="TakeOver"/>): it prints its end lines and asks "Delete the worker now?", and the read still
/// waiting answers that instead.
/// </remarks>
internal sealed class WorkerQuestion(IConsoleInput input)
{
    private readonly object _gate = new();
    private Task<string?>? _read;
    private JobProgressConsole? _progress;
    private WorkerAnswer? _takenOver;
    private Task? _acknowledged;

    public bool CanAsk => input.IsInteractive;

    /// <summary>Done once an answer given during the run has been acknowledged, or the question was taken over first.</summary>
    public Task Acknowledged { get { lock (_gate) return _acknowledged ?? Task.CompletedTask; } }

    public bool IsTakenOver { get { lock (_gate) return _takenOver is not null; } }

    public void Ask(JobProgressConsole progress)
    {
        lock (_gate)
        {
            if (_read is not null || _takenOver is not null) return;
            _progress = progress;
            progress.Hold();
            progress.WritePrompt(WorkerEndMessage.QuestionAtFirstPress);
            _read = Task.Run(input.ReadLine);
            _acknowledged = _read.ContinueWith(Acknowledge, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Ends the question's time during the run and prints any held lines. The first call decides; later calls return the
    /// same answer.
    /// </summary>
    public WorkerAnswer TakeOver()
    {
        lock (_gate)
        {
            if (_takenOver is { } decided) return decided;
            // A prompt still waiting ends its line, so the held lines start on their own.
            if (_read is { IsCompleted: false }) _progress!.WriteNote(string.Empty);
            _progress?.Release();
            _takenOver = _read switch
            {
                null => WorkerAnswer.None,
                { IsCompleted: false } pending => WorkerAnswer.Waiting(pending),
                // A failed read is no answer, like ended input.
                { IsCompletedSuccessfully: true, Result: { } response } => WorkerAnswer.Given(WorkerEndPolicy.IsAffirmative(response)),
                _ => WorkerAnswer.None
            };
            return _takenOver;
        }
    }

    /// <summary>Another line, for the question the run asks when it ends; null is no answer.</summary>
    public Task<string?> ReadAsync() => Task.Run(input.ReadLine);

    private void Acknowledge(Task<string?> read)
    {
        lock (_gate)
        {
            // Taken over: the run's end lines say what happens to the worker.
            if (_takenOver is not null) return;
            var response = read.IsCompletedSuccessfully ? read.Result : null;
            // No answer (Ctrl+C or ended input) ends the prompt's line; the run asks again when it stops.
            if (response is null) _progress!.WriteNote(string.Empty);
            else _progress!.WriteNote(WorkerEndPolicy.IsAffirmative(response) ? WorkerEndMessage.WillBeDeleted : WorkerEndMessage.WillStayUp);
            _progress.Release();
        }
    }
}

/// <summary>The worker question's state when the run ended.</summary>
/// <param name="Answer">True for y, false for N; null when not asked or not answered.</param>
/// <param name="Pending">The read still waiting, which answers the run's end question.</param>
internal sealed record WorkerAnswer(bool? Answer, Task<string?>? Pending)
{
    public static readonly WorkerAnswer None = new(null, null);
    public static WorkerAnswer Given(bool answer) => new(answer, null);
    public static WorkerAnswer Waiting(Task<string?> read) => new(null, read);
}
