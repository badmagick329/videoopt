namespace VideoOptimiser.Cli;

/// <summary>
/// Lines that tell the user what to run next and that more than one command ends with. They live at the Cli root because
/// the queue commands, the run commands and the status card each print them. Changed wording goes through the humanizer gate
/// (docs index).
/// </summary>
internal static class NextSteps
{
    public static string ReadyToFinaliseLine(int count, CliCommand command) => count == 1
        ? $"1 video is ready to finalise: review it, then run '{command.Hint("finalize --ready")}'."
        : $"{count} videos are ready to finalise: review them, then run '{command.Hint("finalize --ready")}'.";
}
