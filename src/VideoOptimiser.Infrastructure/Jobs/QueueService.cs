using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Jobs;

public sealed class QueueService(IFileScanner scanner, IJobRepository jobs, IFileFingerprintService fingerprints, IJobProcessor processor) : IQueueService
{
    public async Task<QueueDiscoveryResult> DiscoverAsync(string databasePath, AppSettings settings, bool first, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var openSourcePaths = first
            ? new HashSet<string>(
                (await jobs.ListAsync(databasePath, terminal: false, cancellationToken)).Select(job => Path.GetFullPath(job.SourcePath)),
                StringComparer.OrdinalIgnoreCase)
            : null;
        var report = await scanner.ScanAsync(
            settings.Watch.Roots,
            settings,
            stopAfterFirstEligible: first,
            openSourcePaths: openSourcePaths,
            progress: progress,
            cancellationToken: cancellationToken);
        var queued = new List<string>();
        var existing = report.Items.Count(item =>
            item.Status == ScanItemStatus.Eligible &&
            openSourcePaths?.Contains(Path.GetFullPath(item.Path)) == true);
        var issues = report.Issues.Count;
        foreach (var item in report.Items.Where(item => item.Status == ScanItemStatus.Eligible))
        {
            if (first && queued.Count > 0)
            {
                break;
            }

            try
            {
                if (await jobs.FindOpenBySourceAsync(databasePath, item.Path, cancellationToken) is not null)
                {
                    if (openSourcePaths is null || !openSourcePaths.Contains(Path.GetFullPath(item.Path)))
                    {
                        existing++;
                    }
                    continue;
                }

                var id = Guid.NewGuid();
                var remote = string.Equals(settings.Processing.Mode, "remoteSsh", StringComparison.OrdinalIgnoreCase);
                await jobs.CreateAsync(databasePath, new JobRecord
                {
                    Id = id,
                    SourcePath = Path.GetFullPath(item.Path),
                    SourceFingerprint = await fingerprints.CreateAsync(item.Path, cancellationToken),
                    Status = JobStatus.Queued,
                    ExecutionMode = remote ? "remoteSsh" : "local",
                    RemoteHost = remote ? RemoteExecutionIdentity.Host(settings.Processing.RemoteSsh) : null,
                    RemoteWorkspace = remote ? $"{settings.Processing.RemoteSsh.WorkingDirectory.TrimEnd('/')}/{id:N}" : null
                }, cancellationToken);
                queued.Add(item.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                issues++;
            }
        }

        return new QueueDiscoveryResult(queued, existing, issues, report.CacheHits, report.RealProbes);
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
