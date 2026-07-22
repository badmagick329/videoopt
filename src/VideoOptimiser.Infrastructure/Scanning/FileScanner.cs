using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.Infrastructure.Scanning;

public sealed class FileScanner(
    IFileReadinessService readinessService,
    Func<string, IMediaProbe> mediaProbeFactory) : IFileScanner
{
    public async Task<ScanReport> ScanAsync(
        IReadOnlyList<WatchRootSettings> roots,
        AppSettings settings,
        bool stopAfterFirstEligible = false,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<ScanItem>();
        var issues = new List<ScanIssue>();
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

                    var item = await EvaluateAsync(canonicalPath, settings, progress, cancellationToken);
                    items.Add(item);
                    if (stopAfterFirstEligible && item.Status == ScanItemStatus.Eligible)
                    {
                        return new ScanReport(items, issues);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new ScanIssue(root.Path, exception.Message));
            }
        }

        return new ScanReport(items, issues);
    }

    private async Task<ScanItem> EvaluateAsync(string path, AppSettings settings, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (!settings.Eligibility.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return new ScanItem(path, ScanItemStatus.Ineligible, "Extension is not allowed.");
        }

        if (settings.Eligibility.ExcludedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
            settings.Eligibility.ExcludedNamePatterns.Any(pattern => IsMatch(fileName, pattern)))
        {
            return new ScanItem(path, ScanItemStatus.Ineligible, "File name is excluded by configuration.");
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new ScanItem(path, ScanItemStatus.Unavailable, "File no longer exists.");
        }

        progress?.Report(new ScanProgress(
            path,
            "Readiness",
            "Checking the file can be read."));
        var readiness = await readinessService.CheckAsync(path, cancellationToken);
        if (!readiness.IsReady)
        {
            return new ScanItem(path, ScanItemStatus.Unavailable, readiness.Reason, info.Length);
        }

        try
        {
            progress?.Report(new ScanProgress(path, "Probing", "Running ffprobe."));
            var mediaInfo = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(path, cancellationToken);
            return EligibilityEvaluator.IsEligible(info, mediaInfo, settings.Eligibility, out var reason)
                ? new ScanItem(path, ScanItemStatus.Eligible, reason, info.Length, mediaInfo)
                : new ScanItem(path, ScanItemStatus.Ineligible, reason, info.Length, mediaInfo);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new ScanItem(path, ScanItemStatus.ProbeFailed, exception.Message, info.Length);
        }
    }

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
