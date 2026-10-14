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

    // Remote stage lines arrive on a poll thread while the job's own steps print from the caller's, and Ctrl+C writes from
    // the console's signal thread.
    private readonly object _gate = new();
    private TimeSpan? _encodeTimeLeft;

    /// <summary>
    /// The latest encode estimate, while the encode is the step in progress: any header or step line clears it, so the
    /// download after an encode never shows the encode's last estimate.
    /// </summary>
    public TimeSpan? EncodeTimeLeft
    {
        get { lock (_gate) return _encodeTimeLeft; }
    }

    /// <summary>A line of the run's own, such as Ctrl+C's, at column 0 between the job's lines.</summary>
    public void WriteNote(string line)
    {
        lock (_gate) writer.WriteLine(line);
    }

    public void Report(JobProgress value)
    {
        lock (_gate)
        {
            if (value.TimeLeft is { } left) _encodeTimeLeft = left;
            else if (value.Kind is JobProgressKind.Header or JobProgressKind.Step) _encodeTimeLeft = null;
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
