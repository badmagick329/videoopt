using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Cli;

/// <summary>
/// Prints a job's <see cref="JobProgress"/> lines as one indented block, so <c>process</c>, <c>queue run</c> and <c>immich run</c>
/// read alike: the header at column 0 after a blank line, steps under it, and detail (one CRF attempt, a progress line)
/// under the steps. A <see cref="JobProgressKind.ToolOutput"/> line reaching here is one the translator did not recognise;
/// it prints as detail rather than being dropped. On a terminal the header is bold, detail is dim so the steps and the outcome
/// stand out, and an outcome step takes its <see cref="JobProgress.Tone"/>'s colour; the words are the same either way.
/// </summary>
internal sealed class JobProgressConsole(TextWriter writer, TerminalStyle style) : IProgress<JobProgress>
{
    private const string StepIndent = "      ";
    private const string DetailIndent = "        ";

    // Remote stage lines arrive on a poll thread while the job's own steps print from the caller's.
    private readonly object _gate = new();

    public void Report(JobProgress value)
    {
        lock (_gate)
        {
            switch (value.Kind)
            {
                case JobProgressKind.Header:
                    writer.WriteLine();
                    writer.WriteLine(style.Bold(value.Text));
                    break;
                case JobProgressKind.Step:
                    writer.WriteLine(StepIndent + style.Tone(value.Tone, value.Text));
                    break;
                default:
                    writer.WriteLine(DetailIndent + style.Dim(value.Text));
                    break;
            }
        }
    }
}
