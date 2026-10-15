using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Calibrate;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.Infrastructure.Features.Calibrate;

public static class ReadOnlyCalibration
{
    /// <summary>
    /// The service as the CLI runs it: a read-only store that migrates nothing, and a probe cache that never saves. Built
    /// here rather than in the CLI so tests check the same wiring the command uses.
    /// </summary>
    public static CalibrationService Create(IFileReadinessService readiness, Func<string, IMediaProbe> mediaProbes, IImmichClientFactory immichClients, Func<DateTimeOffset>? utcNow = null)
    {
        var schemaCheck = new SqliteSchemaCheck();
        var scanner = new FileScanner(readiness, mediaProbes, new UnsavedProbeCache(new SqliteMediaProbeCache(schemaCheck, Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly)));
        return new CalibrationService(scanner, new SqliteJobRepository(schemaCheck, Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly), immichClients, utcNow);
    }
}

/// <summary>
/// The probe cache as <c>calibrate</c> uses it: read when the database exists, and never written, so files probed during the
/// report stay unknown to the next run. Storing them would break the command's promise to write nothing.
/// </summary>
public sealed class UnsavedProbeCache(IMediaProbeCache inner) : IMediaProbeCache
{
    public async Task<IReadOnlyList<MediaProbeCacheEntry>> LoadAllAsync(string databasePath, CancellationToken cancellationToken = default) =>
        File.Exists(databasePath) ? await inner.LoadAllAsync(databasePath, cancellationToken) : [];

    public Task StoreAsync(string databasePath, MediaProbeCacheEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
