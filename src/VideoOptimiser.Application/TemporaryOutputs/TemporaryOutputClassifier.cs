using System.Text.RegularExpressions;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.TemporaryOutputs;

/// <summary>A file under a <c>.video-optimiser</c> folder that belongs to a job attempt nothing will use again.</summary>
public sealed record AbandonedTemporaryOutput(string Path, Guid JobId, int Attempt, long SizeBytes, string Reason);

/// <summary>An Immich job's downloaded original that no job will read again, because the asset's latest job has ended.</summary>
public sealed record StagedImmichOriginal(string Path, Guid JobId, long SizeBytes, string Reason);

/// <summary>
/// Decides which temporary-output files are abandoned. A file is identified only by the job ID and attempt in its name,
/// which <c>JobProcessor</c> writes as <c>{name}.{jobId:N}.{attempt}.encoding{ext}</c>, plus a manifest or download
/// suffix. The match is unanchored at the start because ab-av1 encodes to <c>.tmp.ab-av1-encoding.{that name}</c> and
/// renames it on success. Anything else in the folder, and any file whose job is not in this database, is never
/// classified as abandoned.
/// </summary>
public static partial class TemporaryOutputClassifier
{
    public const string FolderName = ".video-optimiser";

    public static bool TryParse(string fileName, out Guid jobId, out int attempt)
    {
        var match = FileNamePattern().Match(fileName);
        jobId = match.Success ? Guid.ParseExact(match.Groups["id"].Value, "N") : Guid.Empty;
        attempt = match.Success && int.TryParse(match.Groups["attempt"].Value, out var parsed) ? parsed : 0;
        return match.Success && attempt > 0;
    }

    /// <summary>
    /// Returns why the file of <paramref name="attempt"/> is abandoned, or null when the job may still read it. Outputs of a
    /// failed finalisation or an interrupted one are kept: the original may be mid-replacement and needs manual review.
    /// </summary>
    public static string? AbandonedReason(JobRecord job, int attempt)
    {
        if (job.FailureCategory == "FinalizationFailed" || job.ResumeStatus == JobStatus.Finalizing) return null;
        return job.Status switch
        {
            JobStatus.Completed => "job completed",
            JobStatus.Cancelled => "job cancelled",
            JobStatus.Failed => "job failed",
            JobStatus.Skipped => "job skipped",
            _ when attempt < job.Attempt => "replaced by a newer AV1 of the same video",
            _ when RestartsEncode(job) => "stopped encode; the next run starts it again",
            _ => null
        };
    }

    /// <summary>A local encode cannot continue a partial file, so resuming one starts a new attempt and abandons the old file.</summary>
    public static bool RestartsEncode(JobRecord job) =>
        job.Status == JobStatus.Interrupted &&
        job.ResumeStatus == JobStatus.Encoding &&
        !string.Equals(job.ExecutionMode, "remoteSsh", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\.(?<id>[0-9a-f]{32})\.(?<attempt>[0-9]{1,9})\.encoding(\.[^.]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
