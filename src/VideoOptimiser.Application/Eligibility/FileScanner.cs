using System.IO.Enumeration;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Application.Eligibility;

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
        var filesChecked = 0;
        // After every file so a caller can throttle by time; the scan cannot know how often the user wants to hear.
        void ReportChecked(string path) => progress?.Report(new ScanProgress(path, "Checked", string.Empty, ++filesChecked, realProbes));
        var cacheByPath = new Dictionary<string, MediaProbeCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await mediaProbeCache.LoadAllAsync(settings.Database.Path, cancellationToken))
        {
            cacheByPath[Path.GetFullPath(entry.SourcePath)] = entry;
        }
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
                progress?.Report(new ScanProgress(string.Empty, "Issue", $"Watch root does not exist: {root.Path}"));
                continue;
            }

            options.RecurseSubdirectories = root.Recursive;
            try
            {
                foreach (var path in EnumerateFiles(root.Path, options, settings))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var canonicalPath = Path.GetFullPath(path);
                    // The walk never enters a tool folder; this catches a watch root that is itself inside one.
                    if (!seenPaths.Add(canonicalPath) || ToolFiles.IsMadeByTool(canonicalPath, settings))
                    {
                        continue;
                    }

                    if (IsWithinExcludedDirectory(canonicalPath, settings.Eligibility.ExcludedDirectories))
                    {
                        items.Add(new ScanItem(canonicalPath, ScanItemStatus.Ineligible, "File is in an excluded directory."));
                        ReportChecked(canonicalPath);
                        continue;
                    }

                    if (IsWithinArchiveDirectory(canonicalPath, settings.Original.ArchiveDirectory))
                    {
                        items.Add(new ScanItem(canonicalPath, ScanItemStatus.Ineligible, "File is in the archive directory."));
                        ReportChecked(canonicalPath);
                        continue;
                    }

                    var evaluation = await EvaluateAsync(canonicalPath, settings, cacheByPath, progress, force: false, cancellationToken);
                    items.Add(evaluation.Item);
                    // VOPT-73: every Issue line counts, so a file the scan could not judge is never silently left out.
                    if (evaluation.Issue) issues.Add(new ScanIssue(canonicalPath, evaluation.Item.Reason));
                    cacheHits += evaluation.CacheHit ? 1 : 0;
                    realProbes += evaluation.RealProbe ? 1 : 0;
                    ReportChecked(canonicalPath);
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
                progress?.Report(new ScanProgress(string.Empty, "Issue", $"Could not scan {root.Path}: {exception.Message}"));
            }
        }

        return new ScanReport(items, issues, cacheHits, realProbes);
    }

    /// <summary>
    /// <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/> that does not descend into the tool's
    /// folders (<see cref="ToolFiles"/>), whatever the exclude lists say, so their files are never even counted as checked.
    /// </summary>
    private static FileSystemEnumerable<string> EnumerateFiles(string root, EnumerationOptions options, AppSettings settings) =>
        new(root, (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !ToolFiles.IsToolFolder(entry.ToFullPath(), settings)
        };

    public async Task<FileVerdict> JudgeAsync(string path, AppSettings settings, bool force, CancellationToken cancellationToken = default)
    {
        var canonicalPath = Path.GetFullPath(path);
        if (!force && IsWithinExcludedDirectory(canonicalPath, settings.Eligibility.ExcludedDirectories))
        {
            return new FileVerdict(new ScanItem(canonicalPath, ScanItemStatus.Ineligible, "File is in an excluded directory."));
        }

        var cacheByPath = new Dictionary<string, MediaProbeCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await mediaProbeCache.LoadAllAsync(settings.Database.Path, cancellationToken))
        {
            if (string.Equals(Path.GetFullPath(entry.SourcePath), canonicalPath, StringComparison.OrdinalIgnoreCase)) cacheByPath[canonicalPath] = entry;
        }
        var item = (await EvaluateAsync(canonicalPath, settings, cacheByPath, progress: null, force, cancellationToken)).Item;
        // Said even when the rules also leave it out: --force would not lift it, so the --force command would be no help.
        return item is { Status: ScanItemStatus.Ineligible, MediaInfo: { } media, SizeBytes: { } size } && !OutputCodec.IsOutputCodec(media.PrimaryVideoCodec) && SizeCap.For(media, size, settings.Quality, settings.Savings) is { RulesOutEverything: true } cap
            ? new FileVerdict(item with { Reason = TooSmallReason(cap) }, TooSmall: true)
            : new FileVerdict(item);
    }

    private static string TooSmallReason(SizeCap cap) => $"Too small to save {cap.MinimumText} (savings.minimumBytesSaved).";

    /// <param name="force">Skips the extension, name and rule checks, for <c>queue add &lt;file&gt; --force</c>.</param>
    private async Task<EvaluationResult> EvaluateAsync(string path, AppSettings settings, Dictionary<string, MediaProbeCacheEntry> cacheByPath, IProgress<ScanProgress>? progress, bool force, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (!force && !settings.Eligibility.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return new EvaluationResult(new ScanItem(path, ScanItemStatus.Ineligible, "Extension is not allowed."));
        }

        if (!force && (settings.Eligibility.ExcludedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
            settings.Eligibility.ExcludedNamePatterns.Any(pattern => IsMatch(fileName, pattern))))
        {
            return new EvaluationResult(new ScanItem(path, ScanItemStatus.Ineligible, "File name is excluded by configuration."));
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new EvaluationResult(new ScanItem(path, ScanItemStatus.Unavailable, "File no longer exists."));
        }

        var cachedSizeBytes = info.Length;
        var cachedLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
        if (cacheByPath.TryGetValue(path, out var cached) &&
            cached.SourceSizeBytes == cachedSizeBytes &&
            cached.SourceLastWriteUtcTicks == cachedLastWriteUtcTicks)
        {
            return new EvaluationResult(Judge(path, info, cached.MediaInfo, cachedSizeBytes, settings, force), CacheHit: true);
        }

        var readiness = await readinessService.CheckAsync(path, cancellationToken);
        if (!readiness.IsReady)
        {
            progress?.Report(new ScanProgress(path, "Issue", readiness.Reason));
            return new EvaluationResult(new ScanItem(path, ScanItemStatus.Unavailable, readiness.Reason, info.Length), Issue: true);
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
            var mediaInfo = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(path, cancellationToken);
            info.Refresh();
            if (!info.Exists || info.Length != sourceSizeBytes || info.LastWriteTimeUtc.Ticks != sourceLastWriteUtcTicks)
            {
                return new EvaluationResult(new ScanItem(path, ScanItemStatus.Unavailable, "Original changed while probing."), RealProbe: true);
            }

            await mediaProbeCache.StoreAsync(
                settings.Database.Path,
                new MediaProbeCacheEntry(path, sourceSizeBytes, sourceLastWriteUtcTicks, mediaInfo),
                cancellationToken);

            return new EvaluationResult(Judge(path, info, mediaInfo, sourceSizeBytes, settings, force), RealProbe: true);
        }
        catch (Exception exception) when (exception is IOException or MediaProbeException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            progress?.Report(new ScanProgress(path, "Issue", exception.Message));
            return new EvaluationResult(new ScanItem(path, ScanItemStatus.ProbeFailed, exception.Message, info.Length), Issue: true);
        }
    }

    /// <summary>
    /// The eligibility rules, then the size cap: a video too small to save <c>savings.minimumBytesSaved</c> is never queued,
    /// since its job would only skip it. <paramref name="force"/> skips the rules, never the cap, and never the refusal of a
    /// video already in the output codec.
    /// </summary>
    private static ScanItem Judge(string path, FileInfo info, MediaInfo media, long sizeBytes, AppSettings settings, bool force)
    {
        if (OutputCodec.IsOutputCodec(media.PrimaryVideoCodec)) return new ScanItem(path, ScanItemStatus.Ineligible, OutputCodec.RefusalReason, sizeBytes, media);
        var reason = "Queued with --force.";
        if (!force && !EligibilityEvaluator.IsEligible(info, media, settings.Eligibility, out reason))
            return new ScanItem(path, ScanItemStatus.Ineligible, reason, sizeBytes, media);
        var cap = SizeCap.For(media, sizeBytes, settings.Quality, settings.Savings);
        return cap.RulesOutEverything
            ? new ScanItem(path, ScanItemStatus.Ineligible, TooSmallReason(cap), sizeBytes, media)
            : new ScanItem(path, ScanItemStatus.Eligible, reason, sizeBytes, media);
    }

    /// <param name="Issue">The file could not be judged (unreadable, or ffprobe failed), and an Issue line said so.</param>
    private sealed record EvaluationResult(ScanItem Item, bool CacheHit = false, bool RealProbe = false, bool Issue = false);

    private static bool IsMatch(string fileName, string globPattern) => Regex.IsMatch(fileName, "^" + Regex.Escape(globPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Archived originals are still eligible H.264, so discovery must never queue them again, whatever excludedDirectories says.
    public static bool IsWithinArchiveDirectory(string path, string archiveDirectory)
    {
        if (string.IsNullOrWhiteSpace(archiveDirectory)) return false;
        if (!Path.IsPathFullyQualified(archiveDirectory)) return IsWithinExcludedDirectory(path, [archiveDirectory]);
        return path.StartsWith(Path.TrimEndingDirectorySeparator(archiveDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

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
