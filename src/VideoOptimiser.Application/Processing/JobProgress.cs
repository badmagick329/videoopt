using System.Globalization;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Processing;

/// <summary>Where a <see cref="JobProgress"/> line sits in the console's per-job layout.</summary>
public enum JobProgressKind
{
    /// <summary><c>[2/5] name.mp4 (1.0 GiB)</c>: opens a job's block.</summary>
    Header,
    /// <summary>A step starting or ending, or the job's outcome, one level under the header.</summary>
    Step,
    /// <summary>Progress within a step, such as one CRF attempt, one level under the steps.</summary>
    Detail,
    /// <summary>
    /// A raw line from ab-av1, before <c>AbAv1OutputTranslator</c> rewrites it. Only processing sessions report it; anything
    /// that reaches the console this way is a line the translator did not recognise.
    /// </summary>
    ToolOutput
}

/// <summary>
/// One line of a job's console output. <see cref="Text"/> is the final wording; the console adds only the indentation for
/// <see cref="Kind"/>, so the same lines read the same in <c>process</c>, <c>queue run</c> and <c>immich run</c>.
/// </summary>
public sealed record JobProgress(JobProgressKind Kind, string Text)
{
    /// <summary>The block's first line: <c>[2/5] name.mp4 (1.0 GiB)</c>. The size is left out when the original is already gone.</summary>
    public static JobProgress Header(int number, int total, string sourcePath)
    {
        var file = new FileInfo(sourcePath);
        var size = file.Exists ? $" ({HumanReadableValues.FormatSize(file.Length)})" : string.Empty;
        return new(JobProgressKind.Header, string.Create(CultureInfo.InvariantCulture, $"[{number}/{total}] {file.Name}") + size);
    }

    public static JobProgress Step(string text) => new(JobProgressKind.Step, text);
    public static JobProgress Detail(string text) => new(JobProgressKind.Detail, text);
    public static JobProgress Tool(string text) => new(JobProgressKind.ToolOutput, text);
}
