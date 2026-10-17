using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Cost;

/// <summary>
/// The EUR per TB freed of every video, run and the lifetime, built to epic VOPT-126's *The figures*: per video from its own
/// spans, per run from the time it overlapped its worker, lifetime from what Hetzner billed. The three count different time,
/// so they do not add up to each other.
/// </summary>
public sealed class CostReportService(ICostRecords records, TimeProvider time) : ICostReport
{
    /// <summary>TB as drives and storage plans are sold.</summary>
    public const decimal BytesPerTerabyte = 1_000_000_000_000m;

    public async Task<CostReport> ReadAsync(string databasePath, CancellationToken cancellationToken = default) =>
        Build(await records.ReadAsync(databasePath, cancellationToken), time.GetUtcNow());

    public static CostReport Build(CostRecords records, DateTimeOffset now) => new Builder(records, now).Build();

    /// <summary>Hetzner bills per started hour from creation to deletion, so a worker up for a few minutes bills one hour.</summary>
    public static decimal BilledEur(decimal hourlyGross, DateTimeOffset createdUtc, DateTimeOffset endUtc)
    {
        var ticks = Math.Max(0, (endUtc - createdUtc).Ticks);
        var hours = Math.Max(1, (ticks + TimeSpan.TicksPerHour - 1) / TimeSpan.TicksPerHour);
        return hours * hourlyGross;
    }

    /// <summary>Null, shown as a dash, until something is freed: no division by zero, and no figure for a saving still under review.</summary>
    public static decimal? PerTerabyte(decimal? eur, long freedBytes) => eur is { } amount && freedBytes > 0 ? amount / (freedBytes / BytesPerTerabyte) : null;

    /// <summary>Multiplied before dividing, so whole minutes at a price in cents come out exact.</summary>
    private static decimal TimeCost(decimal hourlyGross, TimeSpan duration) => hourlyGross * duration.Ticks / TimeSpan.TicksPerHour;

    private static DateTimeOffset Earliest(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;

    private sealed class Builder(CostRecords records, DateTimeOffset now)
    {
        private readonly Dictionary<long, CostWorkerRow> _workers = records.Workers.ToDictionary(worker => worker.ServerId);
        private readonly Dictionary<Guid, CostRunRow> _runs = records.Runs.ToDictionary(run => run.Id);
        private readonly Dictionary<Guid, CostJobRow> _jobs = records.Jobs.ToDictionary(job => job.Id);
        private readonly CostRunRow[] _runsByStart = [.. records.Runs.OrderBy(run => run.StartedUtc)];

        public CostReport Build()
        {
            var spansByJob = records.Spans.GroupBy(span => span.JobId).ToDictionary(group => group.Key, group => group.OrderBy(span => span.StartedUtc).ToArray());
            // A job belongs to the run of its last span, with all its time: a video resumed in a later run is that run's.
            var videosByRun = spansByJob
                .Where(entry => _jobs.ContainsKey(entry.Key))
                .Select(entry => (Last: entry.Value[^1], Video: Video(_jobs[entry.Key], entry.Value)))
                .GroupBy(entry => entry.Last.RunId)
                .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.Last.StartedUtc).Select(entry => entry.Video).ToArray());

            var runs = records.Runs
                .Where(run => run.WorkerServerId is not null)
                .OrderByDescending(run => run.StartedUtc)
                .Select(run => Run(run, videosByRun.GetValueOrDefault(run.Id, [])))
                .ToArray();
            return new CostReport(Lifetime(spansByJob), runs);
        }

        private CostVideo Video(CostJobRow job, CostSpanRow[] spans)
        {
            var workerTime = TimeSpan.Zero;
            decimal? eur = 0m;
            foreach (var span in spans)
            {
                var length = SpanEnd(span) - span.StartedUtc;
                if (length < TimeSpan.Zero) length = TimeSpan.Zero;
                workerTime += length;
                eur = PriceOf(span.WorkerServerId) is { } price ? eur + TimeCost(price, length) : null;
            }
            var (state, saved) = Saving(job);
            return new CostVideo(job.Id, Path.GetFileName(job.SourcePath), workerTime, eur, saved, state, job.Status, PerTerabyte(eur, state == CostVideoState.Freed ? saved : 0), eur is null);
        }

        private CostRun Run(CostRunRow run, CostVideo[] videos)
        {
            var serverId = run.WorkerServerId!.Value;
            var worker = _workers.GetValueOrDefault(serverId);
            var end = RunEnd(run);
            var overlap = worker is null
                ? end - run.StartedUtc
                : Earliest(end, WorkerEnd(worker)) - (run.StartedUtc > worker.CreatedUtc ? run.StartedUtc : worker.CreatedUtc);
            if (overlap < TimeSpan.Zero) overlap = TimeSpan.Zero;
            var price = worker?.HourlyGross;
            decimal? eur = price is { } hourly ? TimeCost(hourly, overlap) : null;
            var freed = videos.Where(video => video.State == CostVideoState.Freed).Sum(video => video.SavedBytes);
            var waiting = videos.Where(video => video.State == CostVideoState.WaitingForReview).Sum(video => video.SavedBytes);
            // A worker row the lifecycle never wrote still names the run's server.
            return new CostRun(run.Id, run.StartedUtc, end, serverId, worker?.Name ?? $"server {serverId}", price, overlap, eur, freed, waiting, PerTerabyte(eur, freed), videos);
        }

        private CostLifetime Lifetime(Dictionary<Guid, CostSpanRow[]> spansByJob)
        {
            var billed = records.Workers.Where(worker => worker.HourlyGross is not null).Sum(worker => BilledEur(worker.HourlyGross!.Value, worker.CreatedUtc, WorkerEnd(worker)));
            var freed = 0L;
            var waiting = 0L;
            var unpricedJob = false;
            foreach (var (jobId, spans) in spansByJob)
            {
                if (!_jobs.TryGetValue(jobId, out var job)) continue;
                // Left out with its worker's bill, so the lifetime figure never sets bytes against a cost it did not count.
                if (spans.Any(span => PriceOf(span.WorkerServerId) is null))
                {
                    unpricedJob = true;
                    continue;
                }
                var (state, saved) = Saving(job);
                if (state == CostVideoState.Freed) freed += saved;
                else if (state == CostVideoState.WaitingForReview) waiting += saved;
            }
            return new CostLifetime(
                billed, freed, waiting, PerTerabyte(billed, freed), records.Workers.Count,
                StillBilling: records.Workers.Any(worker => worker.DeletedUtc is null),
                EndEstimated: records.Workers.Any(worker => worker.EndEstimated),
                PriceMissing: unpricedJob || records.Workers.Any(worker => worker.HourlyGross is null));
        }

        private static (CostVideoState State, long Saved) Saving(CostJobRow job)
        {
            var state = job.Status switch
            {
                JobStatus.Completed => CostVideoState.Freed,
                JobStatus.ReadyToFinalize => CostVideoState.WaitingForReview,
                _ => CostVideoState.NothingSaved
            };
            return state == CostVideoState.NothingSaved || job.SourceSizeBytes is not { } source || job.OutputSizeBytes is not { } output
                ? (state, 0)
                : (state, source - output);
        }

        private decimal? PriceOf(long serverId) => _workers.GetValueOrDefault(serverId)?.HourlyGross;

        /// <summary>A worker still up bills until now.</summary>
        private DateTimeOffset WorkerEnd(CostWorkerRow worker) => worker.DeletedUtc ?? now;

        /// <summary>The recorded end, else the end of its last span (VOPT-126), else as far as an open run can have gone.</summary>
        private DateTimeOffset RunEnd(CostRunRow run)
        {
            if (run.EndedUtc is { } ended) return ended;
            var spans = records.Spans.Where(span => span.RunId == run.Id).ToArray();
            var end = spans.Length > 0 ? spans.Max(SpanEnd) : OpenBound(run, run.WorkerServerId);
            return end < run.StartedUtc ? run.StartedUtc : end;
        }

        /// <summary>
        /// A span with no end is converting now, or its process was killed. Either way it cannot outlast its run, the next run
        /// (which needed the run lock), or its worker; with none of those ended, it counts to now.
        /// </summary>
        private DateTimeOffset SpanEnd(CostSpanRow span)
        {
            if (span.EndedUtc is { } ended) return ended;
            var run = _runs.GetValueOrDefault(span.RunId);
            var bound = run?.EndedUtc ?? (run is null ? now : OpenBound(run, span.WorkerServerId));
            return _workers.GetValueOrDefault(span.WorkerServerId) is { } worker ? Earliest(bound, WorkerEnd(worker)) : bound;
        }

        private DateTimeOffset OpenBound(CostRunRow run, long? serverId)
        {
            var next = _runsByStart.FirstOrDefault(other => other.StartedUtc > run.StartedUtc)?.StartedUtc ?? now;
            return serverId is { } id && _workers.GetValueOrDefault(id) is { } worker ? Earliest(next, WorkerEnd(worker)) : next;
        }
    }
}
