using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Immich;

namespace VideoOptimiser.Application.Features.Calibrate;

/// <summary>
/// A video the eligibility rules are applied to: a probed file in a watch root, or an Immich asset. <see cref="Codec"/> is
/// null for an Immich asset no job or skip has probed; its <see cref="Bitrate"/> is then the whole file's (size × 8 ÷
/// duration), audio included, because Immich reports no stream bitrate. <see cref="AlreadyConverted"/> marks an Immich
/// asset a completed job uploaded or replaced, whose codec Immich does not report either.
/// </summary>
public sealed record CalibrationVideo(
    string Name,
    string? Path,
    string? ImmichAssetId,
    string? Codec,
    int? Width,
    int? Height,
    long? Bitrate,
    long SizeBytes,
    bool AlreadyConverted = false);

/// <summary>
/// The report, how many scan issues (an unreadable watch root, a failed probe) left videos out of it, and the failure that
/// kept the Immich half out, when Immich could not be searched. Each source stands on its own: a scan of minutes should
/// not be thrown away because the other source failed, so the report then holds the folder videos alone.
/// </summary>
public sealed record CalibrationRun(CalibrationReport Report, int ScanIssues, ImmichApiException? ImmichFailure = null);

/// <summary>Builds the <c>calibrate</c> report. Reads only: it downloads, encodes, queues and stores nothing.</summary>
public interface ICalibrationService
{
    /// <summary>
    /// <paramref name="immichProgress"/> receives the running count of Immich videos searched. Throws
    /// <see cref="Jobs.DatabaseOutdatedException"/> when the database needs a migration, since running one would write.
    /// </summary>
    Task<CalibrationRun> RunAsync(AppSettings settings, IProgress<ScanProgress>? scanProgress, IProgress<int>? immichProgress, CancellationToken cancellationToken = default);
}
