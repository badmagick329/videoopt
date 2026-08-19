using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.Infrastructure.Scanning;

public sealed class FileScanner(
    IFileReadinessService readinessService,
    Func<string, IMediaProbe> mediaProbeFactory,
    IMediaProbeCache mediaProbeCache) : IFileScanner
{
    public async Task<ScanReport> ScanAsync(
        IReadOnlyList<WatchRootSettings> roots,
        AppSettings settings,
        bool stopAfterFirstEligible = false,
        IReadOnlySet<string>? openSourcePaths = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<ScanItem>();
        var issues = new List<ScanIssue>();
        var cacheHits = 0;
        var realProbes = 0;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attributesToSkip = FileAttributes.ReparsePoint;
        if (settings.Eligibility.IgnoreHiddenFiles)
        {
            attributesToSkip |= FileAttributes.Hidden;
        }

        if (settings.Eligibility.IgnoreSystemFiles)
        {
            attributesToSkip |= FileAttributes.System;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = attributesToSkip
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path))
            {
                issues.Add(new ScanIssue(root.Path, "Watch root does not exist."));
                continue;
            }

            options.RecurseSubdirectories = root.Recursive;
            try
            {
                foreach (var path in Directory.EnumerateFiles(root.Path, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var canonicalPath = Path.GetFullPath(path);
                    if (!seenPaths.Add(canonicalPath))
                    {
                        continue;
                    }

                    if (IsWithinExcludedDirectory(canonicalPath, settings.Eligibility.ExcludedDirectories))
                    {
                        items.Add(new ScanItem(canonicalPath, ScanItemStatus.Ineligible, "File is in an excluded directory."));
                        continue;
                    }

                    var evaluation = await EvaluateAsync(canonicalPath, settings, progress, cancellationToken);
                    items.Add(evaluation.Item);
                    cacheHits += evaluation.CacheHit ? 1 : 0;
                    realProbes += evaluation.RealProbe ? 1 : 0;
                    if (stopAfterFirstEligible &&
                        evaluation.Item.Status == ScanItemStatus.Eligible &&
                        (openSourcePaths is null || !openSourcePaths.Contains(canonicalPath)))
                    {
                        return new ScanReport(items, issues, cacheHits, realProbes);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new ScanIssue(root.Path, exception.Message));
            }
        }

        return new ScanReport(items, issues, cacheHits, realProbes);
    }

    private async Task<EvaluationResult> EvaluateAsync(string path, AppSettings settings, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (!settings.Eligibility.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return new EvaluationResult(new ScanItem(path, ScanItemStatus.Ineligible, "Extension is not allowed."));
        }

        if (settings.Eligibility.ExcludedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
            settings.Eligibility.ExcludedNamePatterns.Any(pattern => IsMatch(fileName, pattern)))
        {
            return new EvaluationResult(new ScanItem(path, ScanItemStatus.Ineligible, "File name is excluded by configuration."));
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new EvaluationResult(new ScanItem(path, ScanItemStatus.Unavailable, "File no longer exists."));
        }

        progress?.Report(new ScanProgress(
            path,
            "Readiness",
            "Checking the file can be read."));
        var readiness = await readinessService.CheckAsync(path, cancellationToken);
        if (!readiness.IsReady)
        {
            return new EvaluationResult(new ScanItem(path, ScanItemStatus.Unavailable, readiness.Reason, info.Length));
        }

        try
        {
            info.Refresh();
            if (!info.Exists)
            {
                return new EvaluationResult(new ScanItem(path, ScanItemStatus.Unavailable, "File no longer exists."));
            }

            var sourceSizeBytes = info.Length;
            var sourceLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            var mediaInfo = await mediaProbeCache.GetAsync(
                settings.Database.Path,
                path,
                sourceSizeBytes,
                sourceLastWriteUtcTicks,
                cancellationToken);
            var cacheHit = mediaInfo is not null;
            if (cacheHit)
            {
                progress?.Report(new ScanProgress(path, "Cache", "Reusing cached ffprobe metadata."));
            }
            else
            {
                progress?.Report(new ScanProgress(path, "Probing", "Running ffprobe."));
                mediaInfo = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(path, cancellationToken);
                info.Refresh();
                if (!info.Exists || info.Length != sourceSizeBytes || info.LastWriteTimeUtc.Ticks != sourceLastWriteUtcTicks)
                {
                    return new EvaluationResult(new ScanItem(path, ScanItemStatus.Unavailable, "Source changed while probing."), RealProbe: true);
                }

                await mediaProbeCache.StoreAsync(
                    settings.Database.Path,
                    new MediaProbeCacheEntry(path, sourceSizeBytes, sourceLastWriteUtcTicks, mediaInfo),
                    cancellationToken);
            }

            return EligibilityEvaluator.IsEligible(info, mediaInfo!, settings.Eligibility, out var reason)
                ? new EvaluationResult(new ScanItem(path, ScanItemStatus.Eligible, reason, sourceSizeBytes, mediaInfo), cacheHit, !cacheHit)
                : new EvaluationResult(new ScanItem(path, ScanItemStatus.Ineligible, reason, sourceSizeBytes, mediaInfo), cacheHit, !cacheHit);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new EvaluationResult(new ScanItem(path, ScanItemStatus.ProbeFailed, exception.Message, info.Length));
        }
    }

    private sealed record EvaluationResult(ScanItem Item, bool CacheHit = false, bool RealProbe = false);

    private static bool IsMatch(string fileName, string globPattern) => Regex.IsMatch(fileName, "^" + Regex.Escape(globPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsWithinExcludedDirectory(string path, IReadOnlyCollection<string> excludedDirectories)
    {
        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (excludedDirectories.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }
}
