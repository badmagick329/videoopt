using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Processing;

public enum AbAv1Stage
{
    CrfSearch,
    Encode
}

/// <summary>
/// Rewrites ab-av1's log, from a local process or a remote stage log alike, as the job's progress lines: one line per CRF
/// attempt with its verdict, and encode progress at most every <see cref="ProgressInterval"/>. Timestamps, module paths,
/// per-sample lines and the remote <c>/usr/bin/time -v</c> report are dropped. Any line it does not recognise passes through,
/// so an error or a future ab-av1 format still reaches the user. The raw log is kept elsewhere; this only shapes the console.
/// </summary>
public sealed partial class AbAv1OutputTranslator
{
    /// <summary>
    /// A little under ab-av1's own ~15 s progress cadence and the remote 10 s poll, so each poll's first line still prints
    /// while bursts of lines (a remote poll's batch) collapse to one.
    /// </summary>
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(8);

    private readonly AbAv1Stage _stage;
    private readonly double _minimumVmaf;
    private readonly int _maxEncodedPercent;
    private readonly Func<TimeSpan> _elapsed;
    private readonly Dictionary<string, Prediction> _predictions = new(StringComparer.Ordinal);
    private TimeSpan? _lastProgress;
    private bool _inTimeReport;

    public AbAv1OutputTranslator(AbAv1Stage stage, double minimumVmaf, int maxEncodedPercent)
        : this(stage, minimumVmaf, maxEncodedPercent, StartClock())
    {
    }

    internal AbAv1OutputTranslator(AbAv1Stage stage, double minimumVmaf, int maxEncodedPercent, Func<TimeSpan> elapsed)
    {
        _stage = stage;
        _minimumVmaf = minimumVmaf;
        _maxEncodedPercent = maxEncodedPercent;
        _elapsed = elapsed;
    }

    /// <summary>Wraps <paramref name="progress"/> so raw ab-av1 lines arrive translated; every other line passes unchanged.</summary>
    public IProgress<JobProgress> Wrap(IProgress<JobProgress> progress) => new TranslatingProgress(this, progress);

    public IReadOnlyList<JobProgress> Translate(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return [];
        // The time report's lines are indented; its first line names the command, so everything indented after it is the report.
        if (TimeReportStart().IsMatch(line)) { _inTimeReport = true; return []; }
        if (_inTimeReport && char.IsWhiteSpace(line[0])) return [];
        _inTimeReport = false;

        var logged = LogLine().Match(line);
        if (!logged.Success) return TranslatePlain(line.Trim());
        var level = logged.Groups["level"].Value;
        var module = logged.Groups["module"].Value;
        var message = logged.Groups["message"].Value.Trim();
        if (level != "INFO") return [JobProgress.Detail($"ab-av1 {level.ToLowerInvariant()}: {message}")];
        if (module.EndsWith("::crf_search", StringComparison.Ordinal)) return TranslateCrfSearch(message);
        if (module.EndsWith("::sample_encode", StringComparison.Ordinal))
        {
            if (PredictionLine().Match(message) is { Success: true } prediction)
                _predictions[prediction.Groups["crf"].Value] = new Prediction(double.Parse(prediction.Groups["vmaf"].Value, CultureInfo.InvariantCulture), int.Parse(prediction.Groups["percent"].Value, CultureInfo.InvariantCulture));
            return [];
        }
        if (module.EndsWith("::encode", StringComparison.Ordinal)) return TranslateEncode(message);
        if (module.EndsWith("::vmaf", StringComparison.Ordinal)) return [];
        return [JobProgress.Detail(message)];
    }

    private IReadOnlyList<JobProgress> TranslateCrfSearch(string message)
    {
        if (AttemptLine().Match(message) is { Success: true } attempt)
        {
            var vmaf = double.Parse(attempt.Groups["vmaf"].Value, CultureInfo.InvariantCulture);
            var percent = int.Parse(attempt.Groups["percent"].Value, CultureInfo.InvariantCulture);
            var verdict = vmaf < _minimumVmaf ? "below target" : percent > _maxEncodedPercent ? "passes, file too big" : "passes";
            return [JobProgress.Detail(FormatAttempt(attempt.Groups["crf"].Value, vmaf, percent, verdict))];
        }

        if (ChosenLine().Match(message) is { Success: true } chosen)
        {
            // ab-av1 logs no VMAF line for the attempt it chooses, only "crf N successful"; the sample_encode prediction just
            // before it carries the numbers.
            var crf = chosen.Groups["crf"].Value;
            return _predictions.TryGetValue(crf, out var prediction)
                ? [JobProgress.Detail(FormatAttempt(crf, prediction.Vmaf, prediction.Percent, "passes, chosen"))]
                : [JobProgress.Detail($"CRF {crf,2}   chosen")];
        }

        return [JobProgress.Detail(message)];
    }

    private IReadOnlyList<JobProgress> TranslateEncode(string message)
    {
        if (message.StartsWith("encoding ", StringComparison.Ordinal)) return [];
        if (ProgressLine().Match(message) is not { Success: true } progress) return [JobProgress.Detail(message)];
        var now = _elapsed();
        if (_lastProgress is { } last && now - last < ProgressInterval) return [];
        _lastProgress = now;
        var eta = ParseEta(progress.Groups["eta"].Value) is { } left ? $", about {HumanReadableValues.FormatDuration(left)} left" : string.Empty;
        return [JobProgress.Detail($"{progress.Groups["percent"].Value}%, {progress.Groups["fps"].Value} fps{eta}")];
    }

    private IReadOnlyList<JobProgress> TranslatePlain(string line)
    {
        if (EncodedLine().Match(line) is { Success: true } encoded)
            return [JobProgress.Step($"Encoded {encoded.Groups["size"].Value} ({encoded.Groups["percent"].Value}% of the original) in {HumanReadableValues.FormatDuration(_elapsed())}.")];
        // The crf-search result line repeats the chosen attempt, which is already on screen.
        if (_stage == AbAv1Stage.CrfSearch && PredictionLine().IsMatch(line)) return [];
        // The job's outcome explains this one: the video is not worth converting at the configured limits.
        if (line == "Error: Failed to find a suitable crf") return [];
        if (NonZeroExit().IsMatch(line)) return [];
        return [JobProgress.Detail(line)];
    }

    private static string FormatAttempt(string crf, double vmaf, int percent, string verdict) =>
        string.Create(CultureInfo.InvariantCulture, $"CRF {crf,2}   VMAF {vmaf,5:0.00}   size {percent,3}%   {verdict}");

    /// <summary>ab-av1 writes ETAs as "66 seconds", "3 minutes" or "1 hour".</summary>
    internal static TimeSpan? ParseEta(string value)
    {
        var match = EtaPattern().Match(value);
        if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        return match.Groups["unit"].Value switch
        {
            "second" => TimeSpan.FromSeconds(number),
            "minute" => TimeSpan.FromMinutes(number),
            "hour" => TimeSpan.FromHours(number),
            "day" => TimeSpan.FromDays(number),
            _ => null
        };
    }

    private sealed record Prediction(double Vmaf, int Percent);

    private static Func<TimeSpan> StartClock()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }

    private sealed class TranslatingProgress(AbAv1OutputTranslator translator, IProgress<JobProgress> inner) : IProgress<JobProgress>
    {
        // A local ab-av1's stdout and stderr are read on separate tasks, and the translator's state (time-report mode,
        // progress throttle) is not safe to touch from both at once.
        private readonly object _gate = new();

        public void Report(JobProgress value)
        {
            if (value.Kind != JobProgressKind.ToolOutput) { inner.Report(value); return; }
            lock (_gate)
            {
                foreach (var line in translator.Translate(value.Text)) inner.Report(line);
            }
        }
    }

    [GeneratedRegex(@"^\[\d{4}-\d\d-\d\dT[^\]\s]*\s+(?<level>[A-Z]+)\s+(?<module>[\w:]+)\]\s?(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LogLine();

    // A repeated search reuses earlier samples and marks those lines "(cache)".
    [GeneratedRegex(@"^crf (?<crf>\d+(?:\.\d+)?) VMAF (?<vmaf>\d+(?:\.\d+)?) \((?<percent>\d+)%\)(?: \(cache\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex AttemptLine();

    [GeneratedRegex(@"^crf (?<crf>\d+(?:\.\d+)?) successful$", RegexOptions.CultureInvariant)]
    private static partial Regex ChosenLine();

    [GeneratedRegex(@"^crf (?<crf>\d+(?:\.\d+)?) VMAF (?<vmaf>\d+(?:\.\d+)?) predicted video stream size (?<size>\d+(?:\.\d+)? \w+) \((?<percent>\d+)%\) taking (?<taking>.+?)(?: \(cache\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex PredictionLine();

    [GeneratedRegex(@"^(?<percent>\d+)%, (?<fps>\d+(?:\.\d+)?) fps, eta (?<eta>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();

    [GeneratedRegex(@"^Encoded (?<size>\d+(?:\.\d+)? \w+) \((?<percent>\d+)%\)$", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedLine();

    [GeneratedRegex(@"^\s+Command being timed:", RegexOptions.CultureInvariant)]
    private static partial Regex TimeReportStart();

    [GeneratedRegex(@"^Command exited with non-zero status \d+$", RegexOptions.CultureInvariant)]
    private static partial Regex NonZeroExit();

    [GeneratedRegex(@"^(?<value>\d+(?:\.\d+)?) (?<unit>second|minute|hour|day)s?$", RegexOptions.CultureInvariant)]
    private static partial Regex EtaPattern();
}
