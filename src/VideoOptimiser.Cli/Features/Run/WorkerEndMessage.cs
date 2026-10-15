namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// What a run says about the managed worker when it ends (VOPT-54 step 3; <c>WorkerEndPolicy</c> decides). Changed wording goes
/// through the humanizer gate (docs index).
/// </summary>
internal static class WorkerEndMessage
{
    public const string QuestionAtFirstPress = "Delete the worker when the run stops? [y/N] ";
    public const string WillBeDeleted = "The worker will be deleted when the run stops. Keep this window open until it says it's deleted.";
    public const string WillStayUp = "The worker will stay up when the run stops.";
    public const string QuestionAtEnd = "Delete the worker now? [y/N] ";

    /// <summary>A press while the worker is deleted stops nothing; closing the window would cut the delete off.</summary>
    public const string DeletingPress = "Deleting the worker; closing this window now leaves it billing.";

    public static IReadOnlyList<string> DeleteFailed(string worker, string reason, CliCommand command) =>
    [
        $"Couldn't delete the worker {worker}: {reason}",
        $"It's still billing hourly. To delete it: {StoppedRunMessage.DeleteCommand(command)}"
    ];
}
