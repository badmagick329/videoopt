using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Cost;

/// <summary>A managed worker's row as the bill counts it (migration 13).</summary>
/// <param name="HourlyGross">The server type's and the Primary IPv4's hourly gross, incl. VAT; null when Hetzner listed no price.</param>
/// <param name="DeletedUtc">Null while the worker is still up.</param>
/// <param name="EndEstimated">The worker was deleted outside VideoOptimiser, so <paramref name="DeletedUtc"/> is the last time it was seen up.</param>
public sealed record CostWorkerRow(long ServerId, string Name, decimal? HourlyGross, DateTimeOffset CreatedUtc, DateTimeOffset? DeletedUtc, bool EndEstimated);

/// <summary>A run, local ones included: one run holds the run lock at a time, so a later run's start bounds an earlier one a killed process left open.</summary>
public sealed record CostRunRow(Guid Id, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, long? WorkerServerId);

/// <summary>A conversion span on a managed worker; spans on this PC or a manual host cost nothing and are not read.</summary>
public sealed record CostSpanRow(Guid JobId, Guid RunId, long WorkerServerId, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc);

/// <summary>A job with a span on a managed worker, with the sizes validation recorded.</summary>
public sealed record CostJobRow(Guid Id, string SourcePath, JobStatus Status, long? SourceSizeBytes, long? OutputSizeBytes);

public sealed record CostRecords(IReadOnlyList<CostWorkerRow> Workers, IReadOnlyList<CostRunRow> Runs, IReadOnlyList<CostSpanRow> Spans, IReadOnlyList<CostJobRow> Jobs)
{
    public static CostRecords None { get; } = new([], [], [], []);
}

/// <summary>The cost report's own read of the run records and <c>jobs</c>. It writes nothing and takes no lock.</summary>
public interface ICostRecords
{
    /// <summary>Every worker and run, the spans on managed workers, and their jobs; <see cref="CostRecords.None"/> when the database does not exist.</summary>
    Task<CostRecords> ReadAsync(string databasePath, CancellationToken cancellationToken);
}

/// <summary>What a video's conversion left, as epic VOPT-126's figures count it.</summary>
public enum CostVideoState
{
    /// <summary><see cref="JobStatus.Completed"/>: the AV1 took the original's place.</summary>
    Freed,
    /// <summary><see cref="JobStatus.ReadyToFinalize"/>: the saving waits for the person's review.</summary>
    WaitingForReview,
    /// <summary>Every other status: its worker time counts, with 0 bytes.</summary>
    NothingSaved
}

/// <param name="WorkerTime">Its spans on managed workers, over every run.</param>
/// <param name="Eur">Each span's time × its worker's hourly price, not rounded; null when a worker has no recorded price (<paramref name="PriceMissing"/>).</param>
/// <param name="SavedBytes">Source minus AV1 when freed or waiting for review, otherwise 0.</param>
/// <param name="EurPerTerabyte">Null (a dash) unless something was freed and the EUR is known.</param>
public sealed record CostVideo(Guid JobId, string FileName, TimeSpan WorkerTime, decimal? Eur, long SavedBytes, CostVideoState State, JobStatus Status, decimal? EurPerTerabyte, bool PriceMissing);

/// <param name="EndUtc">The recorded end, or the end of its last span when the run never recorded one.</param>
/// <param name="WorkerTime">How long the run overlapped its worker's life, bootstrap and the gaps between videos included.</param>
/// <param name="Eur">That overlap × the hourly price, not rounded; null when the worker has no recorded price.</param>
/// <param name="Videos">The jobs whose last span on a managed worker is in this run, in the order they were converted.</param>
public sealed record CostRun(Guid Id, DateTimeOffset StartedUtc, DateTimeOffset EndUtc, long WorkerServerId, string WorkerName, decimal? HourlyGross, TimeSpan WorkerTime, decimal? Eur, long FreedBytes, long WaitingBytes, decimal? EurPerTerabyte, IReadOnlyList<CostVideo> Videos)
{
    public bool PriceMissing => HourlyGross is null;
}

/// <param name="BilledEur">What Hetzner billed every priced worker: started hours × hourly price, a worker still up counted to now.</param>
/// <param name="FreedBytes">Freed by jobs with a span on a managed worker; a job that also ran on a worker with no price is left out, as that worker's bill is.</param>
/// <param name="Workers">Every managed worker recorded, priced or not.</param>
/// <param name="EndEstimated">A worker was deleted outside VideoOptimiser, so its bill ends at the last time it was seen up.</param>
/// <param name="PriceMissing">A worker has no recorded price, so its bill and its videos are left out of these figures.</param>
public sealed record CostLifetime(decimal BilledEur, long FreedBytes, long WaitingBytes, decimal? EurPerTerabyte, int Workers, bool StillBilling, bool EndEstimated, bool PriceMissing);

/// <param name="Runs">Only runs that used a managed worker, newest first.</param>
public sealed record CostReport(CostLifetime Lifetime, IReadOnlyList<CostRun> Runs)
{
    public bool NothingRecorded => Lifetime.Workers == 0 && Runs.Count == 0;
}

/// <summary>Builds the <c>cost</c> report and the TUI's cost page. Reads only, and takes no lock.</summary>
public interface ICostReport
{
    Task<CostReport> ReadAsync(string databasePath, CancellationToken cancellationToken = default);
}
