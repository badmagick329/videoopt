using System.Globalization;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Jobs;

/// <summary>Where a <see cref="JobProgress"/> line sits in the console's per-job layout.</summary>
public enum JobProgressKind
{
    /// <summary><c>[2/5] name.mp4 (1.0 GiB)</c>: opens a job's block.</summary>
    Header,
    /// <summary>A step starting or ending, or the job's outcome, one level under the header.</summary>
    Step,
    /// <summary>
    /// Progress within a step, one level under the steps: a fact that stays, such as one CRF attempt and its verdict, or, with a
    /// <see cref="JobProgress.Percent"/>, a reading that the next one replaces, such as an encode's or a transfer's progress.
    /// </summary>
    Detail,
    /// <summary>
    /// A raw line from ab-av1, before <c>AbAv1OutputTranslator</c> rewrites it. Only processing sessions report it; anything
    /// that reaches the console this way is a line the translator did not recognise.
    /// </summary>
    ToolOutput
}

/// <summary>
/// How a line that ends something should read. It is data, set where the outcome is known, so the console never has to
/// guess a colour from the wording.
/// </summary>
public enum JobTone
{
    /// <summary>An ordinary line.</summary>
    None,
    /// <summary>The job, or a stage of it, is done: ready to finalise.</summary>
    Success,
    /// <summary>The job ended without being converted, and nothing is wrong: not worth converting.</summary>
    Warning,
    /// <summary>The job failed or cannot continue.</summary>
    Failure
}

/// <summary>
/// One line of a job's console output. <see cref="Text"/> is the final wording; the console adds only the indentation for
/// <see cref="Kind"/> and, when it can, the colour for <see cref="Tone"/>, so the same lines read the same in
/// <c>run</c> and <c>immich run</c>.
/// </summary>
/// <param name="TimeLeft">On an encode progress line, the estimate its text shows, so the first Ctrl+C can say how long stopping after this video takes.</param>
/// <param name="Percent">
/// How far a step with a measurable end has got, 0 to 100, on a reading that the next one replaces (an encode, a transfer, a
/// checksum); the text already shows it. Null on every line that stays, so a front end that shows only the latest reading can
/// keep the others.
/// </param>
public sealed record JobProgress(JobProgressKind Kind, string Text, JobTone Tone = JobTone.None, TimeSpan? TimeLeft = null, int? Percent = null)
{
    /// <summary>The block's first line: <c>[2/5] name.mp4 (1.0 GiB)</c>. The size is left out when the original is already gone.</summary>
    public static JobProgress Header(int number, int total, string sourcePath)
    {
        var file = new FileInfo(sourcePath);
        var size = file.Exists ? $" ({HumanReadableValues.FormatSize(file.Length)})" : string.Empty;
        return new(JobProgressKind.Header, string.Create(CultureInfo.InvariantCulture, $"[{number}/{total}] {file.Name}") + size);
    }

    public static JobProgress Step(string text) => new(JobProgressKind.Step, text);

    /// <summary>A step that ends the job, or says how it ended, with the <paramref name="tone"/> that outcome deserves.</summary>
    public static JobProgress Outcome(JobTone tone, string text) => new(JobProgressKind.Step, text, tone);
    public static JobProgress Detail(string text) => new(JobProgressKind.Detail, text);

    /// <summary>A reading of how far a step has got, which the next reading replaces.</summary>
    public static JobProgress Reading(string text, int percent, TimeSpan? timeLeft = null) => new(JobProgressKind.Detail, text, TimeLeft: timeLeft, Percent: Math.Clamp(percent, 0, 100));
    public static JobProgress Tool(string text) => new(JobProgressKind.ToolOutput, text);
}
