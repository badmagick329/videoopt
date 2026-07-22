using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Jobs;

public sealed class QueueService(IFileScanner scanner, IJobRepository jobs, IFileFingerprintService fingerprints, IJobProcessor processor) : IQueueService
{
    public async Task<QueueDiscoveryResult> DiscoverAsync(string databasePath, AppSettings settings, bool first, CancellationToken cancellationToken = default)
    {
        var report = await scanner.ScanAsync(settings.Watch.Roots, settings, stopAfterFirstEligible: first, cancellationToken: cancellationToken);
        var queued = new List<string>();
        var existing = 0;
        var issues = report.Issues.Count;
        foreach (var item in report.Items.Where(item => item.Status == ScanItemStatus.Eligible))
        {
            try
            {
                if (await jobs.FindOpenBySourceAsync(databasePath, item.Path, cancellationToken) is not null)
                {
                    existing++;
                    continue;
                }

                await jobs.CreateAsync(databasePath, new JobRecord
                {
                    Id = Guid.NewGuid(),
                    SourcePath = Path.GetFullPath(item.Path),
                    SourceFingerprint = await fingerprints.CreateAsync(item.Path, cancellationToken),
                    Status = JobStatus.Queued
                }, cancellationToken);
                queued.Add(item.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                issues++;
            }
        }

        return new QueueDiscoveryResult(queued, existing, issues);
    }

    public async Task<QueueRunResult> RunAsync(string databasePath, AppSettings settings, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        await jobs.MarkActiveJobsInterruptedAsync(databasePath, cancellationToken);
        var candidates = (await jobs.ListAsync(databasePath, terminal: false, cancellationToken))
            .Where(job => job.Status is JobStatus.Queued or JobStatus.Interrupted)
            .OrderBy(job => job.CreatedUtc)
            .ToArray();
        var ready = 0;
        var failed = 0;
        foreach (var job in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await processor.ProcessAsync(databasePath, job.SourcePath, settings, force: false, progress, cancellationToken);
            if (result.Job.Status == JobStatus.ReadyToFinalize) ready++;
            else if (result.ExitCode != ExitCode.Success) failed++;
        }

        return new QueueRunResult(ready, failed, failed == 0 ? ExitCode.Success : ready > 0 ? ExitCode.PartialSuccess : ExitCode.ProcessingFailure);
    }
}
