using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// Prints a job's <see cref="JobProgress"/> lines as one indented block, so <c>run</c> and <c>immich run</c>
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
    private List<JobProgress>? _held;

    /// <summary>
    /// The latest encode estimate, while the encode is the step in progress: any header or step line clears it, so the
    /// download after an encode never shows the encode's last estimate.
    /// </summary>
    public TimeSpan? EncodeTimeLeft
    {
        get { lock (_gate) return _encodeTimeLeft; }
    }

    /// <summary>A line of the run's own, such as Ctrl+C's, at column 0 between the job's lines. Printed even while lines are held.</summary>
    public void WriteNote(string line)
    {
        lock (_gate) writer.WriteLine(line);
    }

    /// <summary>A question waiting for its answer on the same line. Printed even while lines are held.</summary>
    public void WritePrompt(string prompt)
    {
        lock (_gate) writer.Write(prompt);
    }

    /// <summary>
    /// Holds the job's lines while a question waits for its answer, so they do not run into the typing. The video keeps
    /// converting meanwhile.
    /// </summary>
    public void Hold()
    {
        lock (_gate) _held ??= [];
    }

    /// <summary>
    /// Prints the held lines. Of a run of readings (lines with a <see cref="JobProgress.Percent"/>) only the last prints: the
    /// earlier ones are out of date, and a long wait for an answer would otherwise flood the screen. A CRF attempt is a fact, not
    /// a reading, and always prints. Safe to call when nothing is held.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_held is not { } held) return;
            _held = null;
            for (var index = 0; index < held.Count; index++)
            {
                var stale = held[index].Percent is not null && index + 1 < held.Count && held[index + 1].Percent is not null;
                if (!stale) Write(held[index]);
            }
        }
    }

    public void Report(JobProgress value)
    {
        // Both are for a front end that keeps one progress line (JobProgressKind says why).
        if (value.Kind is JobProgressKind.Status or JobProgressKind.WorkerAnswered) return;
        lock (_gate)
        {
            if (value.TimeLeft is { } left) _encodeTimeLeft = left;
            else if (value.Kind is JobProgressKind.Header or JobProgressKind.Step) _encodeTimeLeft = null;
            if (_held is not null) _held.Add(value);
            else Write(value);
        }
    }

    private void Write(JobProgress value)
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
                writer.WriteLine(DetailIndent + (value.Tone == JobTone.None ? style.Dim(value.Text) : style.Tone(value.Tone, value.Text)));
                break;
        }
    }
}
