using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Queue;

namespace VideoOptimiser.Cli;

/// <summary>
/// Prints a file scan as it runs: nothing for a scan that ends quickly, a count at most every 10 seconds for one that does
/// not (so a long scan reads as busy rather than stuck, without a line per file), and each issue as it is found, grouped
/// under the folder it is in.
/// </summary>
internal sealed class QueueDiscoverConsole(TextWriter output, Func<DateTimeOffset>? utcNow = null) : IProgress<ScanProgress>
{
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    // Starts at the first file, so a scan is silent for its first 10 seconds.
    private DateTimeOffset? _lastLineAt;
    private string? _issueFolder;

    /// <summary>Whether any line was printed, so a report that follows knows to set itself apart with a blank line.</summary>
    public bool WroteLines { get; private set; }

    public void Report(ScanProgress update)
    {
        lock (_gate)
        {
            if (update.Stage == "Issue")
            {
                ReportIssue(update);
                WroteLines = true;
                return;
            }

            if (update.Stage != "Checked") return;
            var now = _utcNow();
            _lastLineAt ??= now;
            if (now - _lastLineAt < ProgressInterval) return;
            _lastLineAt = now;
            output.WriteLine($"Checked {update.FilesChecked} files so far ({update.Probed} probed with ffprobe)...");
            WroteLines = true;
        }
    }

    private void ReportIssue(ScanProgress update)
    {
        // A scan-level issue (an unreadable watch root) names its folder in the message and has no file.
        if (string.IsNullOrWhiteSpace(update.Path))
        {
            _issueFolder = null;
            output.WriteLine($"Issue: {update.Message}");
            return;
        }

        var folder = Path.GetDirectoryName(update.Path) ?? string.Empty;
        if (!string.Equals(folder, _issueFolder, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"In {folder}:");
            _issueFolder = folder;
        }

        output.WriteLine($"  Issue: {Path.GetFileName(update.Path)}: {update.Message}");
    }

    /// <summary>What the scan found, folder by folder, then the counts that are not zero. The caller adds the next command.</summary>
    public static void WriteSummary(TextWriter output, QueueDiscoveryResult result)
    {
        foreach (var folder in result.QueuedPaths.GroupBy(path => Path.GetDirectoryName(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"Queued in {folder.Key}:");
            foreach (var path in folder) output.WriteLine($"  {Path.GetFileName(path)}");
        }

        WriteCounts(output, result);
    }

    /// <summary>The counts that are not zero, and the notes under them. <c>queue add immich</c> prints this alone: it lists its queued videos as it downloads them.</summary>
    public static void WriteCounts(TextWriter output, QueueDiscoveryResult result)
    {
        var counts = new List<string> { $"Queued: {result.QueuedPaths.Count}." };
        void Add(int count, string label) { if (count > 0) counts.Add($"{label}: {count}."); }
        Add(result.AlreadyQueued, "Already queued");
        Add(result.Held, "Held");
        Add(result.ReadyToFinalize, "Ready to finalise");
        Add(result.AlreadyConverted, "Already converted");
        Add(result.PreviouslyFailed, "Previously failed or cancelled");
        Add(result.PreviouslySkipped, "Previously skipped");
        Add(result.CacheHits, "Cache hits");
        Add(result.RealProbes, "Real probes");
        Add(result.Issues, "Issues");
        output.WriteLine(string.Join(' ', counts));
        if (result.PreviouslyFailed > 0) output.WriteLine("Failed or cancelled originals stay out of the queue until they change. 'history' lists their jobs; 'queue retry <job-id>' queues one again.");
        if (result.PreviouslySkipped > 0) output.WriteLine("Skipped originals (AV1 would not save enough) are checked again when the file or the quality settings change; 'queue retry <job-id>' queues one now.");
    }
}
