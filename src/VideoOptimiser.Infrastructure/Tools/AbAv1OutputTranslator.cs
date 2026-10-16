using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Tools;

public sealed class AbAv1ProgressTranslator : IAbAv1ProgressTranslator
{
    public IReattachableProgress Wrap(IProgress<JobProgress> progress, AbAv1Stage stage, QualitySettings quality) =>
        new AbAv1OutputTranslator(stage, quality.MinimumVmaf, quality.MaxEncodedPercent).Wrap(progress);
}

/// <summary>
/// Rewrites ab-av1's log, from a local process or a remote stage log alike, as the job's progress lines: one line per CRF
/// attempt with its verdict, and encode progress at most every <see cref="ProgressInterval"/>. Each test clip's encode and
/// VMAF become a <see cref="JobProgressKind.Status"/>, since one attempt can take minutes with nothing else to show (a 4K clip
/// on a CPU worker). Timestamps, module paths, per-clip scores and the remote <c>/usr/bin/time -v</c> report are dropped. Any line it does not recognise passes through,
/// so an error or a future ab-av1 format still reaches the user. The raw log is kept elsewhere; this only shapes the console.
/// </summary>
public sealed partial class AbAv1OutputTranslator
{
    /// <summary>
    /// A little under the remote 10 s poll, so each poll's first line still prints while bursts of lines (a remote poll's
    /// batch) collapse to one. ab-av1 itself, with its output redirected, prints progress 15 s apart at first and less often
    /// as the encode goes on (measured 2026-10-16: 16 s, 16 s, 32 s, then 63 s to the end), so locally nothing is dropped.
    /// </summary>
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(8);

    // An estimate from too little progress swings wildly (the first percent or two said "2 minutes" three times for a
    // three-minute encode), so it is withheld until both the progress and the time it is measured over are worth trusting.
    internal const int MinimumProgressForEta = 5;
    internal static readonly TimeSpan MinimumTimeForEta = TimeSpan.FromSeconds(30);

    private readonly AbAv1Stage _stage;
    private readonly double _minimumVmaf;
    private readonly int _maxEncodedPercent;
    private readonly Func<TimeSpan> _elapsed;
    private readonly Dictionary<string, Prediction> _predictions = new(StringComparer.Ordinal);
    private TimeSpan? _lastProgress;
    private (int Percent, TimeSpan At)? _firstProgress;
    private bool _inTimeReport;
    private bool _reattached;
    // The test clip ab-av1 is on, from its last "encoding sample" line: its VMAF line names files, not the clip or the CRF.
    private string? _sample;
    private string? _sampleCrf;

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
    public IReattachableProgress Wrap(IProgress<JobProgress> progress) => new TranslatingProgress(this, progress);

    /// <summary>
    /// The stage was already running when this command reached it, and only the lines after that point arrive. The time left is
    /// then measured over progress seen since, which it already is, and the time <c>Encoded</c> reports would be only the
    /// time since, so that line leaves it out.
    /// </summary>
    public void StageWasAlreadyRunning() => _reattached = true;

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
            if (SampleStartLine().Match(message) is { Success: true } sample)
            {
                _sample = $"test clip {sample.Groups["number"].Value} of {sample.Groups["count"].Value}";
                _sampleCrf = sample.Groups["crf"].Value;
                return [JobProgress.Status($"CRF {_sampleCrf}: encoding {_sample}.")];
            }
            return [];
        }
        if (module.EndsWith("::encode", StringComparison.Ordinal)) return TranslateEncode(message);
        if (module.EndsWith("::vmaf", StringComparison.Ordinal))
            return _sample is null ? [] : [JobProgress.Status($"CRF {_sampleCrf}: measuring the VMAF of {_sample}.")];
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
        var percent = int.Parse(progress.Groups["percent"].Value, CultureInfo.InvariantCulture);
        _firstProgress ??= (percent, now);
        if (_lastProgress is { } last && now - last < ProgressInterval) return [];
        _lastProgress = now;
        var left = EstimateTimeLeft(percent, now);
        var eta = left is { } value ? $", about {HumanReadableValues.FormatDuration(value)} left" : string.Empty;
        return [JobProgress.Reading($"{percent}%, {progress.Groups["fps"].Value} fps{eta}", percent, left)];
    }

    /// <summary>
    /// Time left at the pace seen since the first progress line, measured on this translator's clock. ab-av1's own estimate is
    /// rounded to whole minutes and early on reflects the first seconds only. A remote log arrives in polled batches, so the
    /// clock here is when the lines were read, which averages out over the minimum span.
    /// </summary>
    private TimeSpan? EstimateTimeLeft(int percent, TimeSpan now)
    {
        var (startPercent, startedAt) = _firstProgress!.Value;
        if (percent - startPercent < MinimumProgressForEta || now - startedAt < MinimumTimeForEta) return null;
        return (now - startedAt) * ((100d - percent) / (percent - startPercent));
    }

    private IReadOnlyList<JobProgress> TranslatePlain(string line)
    {
        if (EncodedLine().Match(line) is { Success: true } encoded)
        {
            var size = $"Encoded {HumanReadableValues.FormatSizeText(encoded.Groups["size"].Value)} ({encoded.Groups["percent"].Value}% of the original)";
            return [JobProgress.Step(_reattached ? $"{size}." : $"{size} in {HumanReadableValues.FormatDuration(_elapsed())}.")];
        }
        // The crf-search result line repeats the chosen attempt, which is already on screen.
        if (_stage == AbAv1Stage.CrfSearch && PredictionLine().IsMatch(line)) return [];
        // The job's outcome explains this one: the video is not worth converting at the configured limits.
        if (line == "Error: Failed to find a suitable crf") return [];
        if (NonZeroExit().IsMatch(line)) return [];
        return [JobProgress.Detail(line)];
    }

    private static string FormatAttempt(string crf, double vmaf, int percent, string verdict) =>
        string.Create(CultureInfo.InvariantCulture, $"CRF {crf,2}   VMAF {vmaf,5:0.00}   size {percent,3}%   {verdict}");

    private sealed record Prediction(double Vmaf, int Percent);

    private static Func<TimeSpan> StartClock()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }

    private sealed class TranslatingProgress(AbAv1OutputTranslator translator, IProgress<JobProgress> inner) : IReattachableProgress
    {
        // A local ab-av1's stdout and stderr are read on separate tasks, and the translator's state (time-report mode,
        // progress throttle) is not safe to touch from both at once.
        private readonly object _gate = new();

        public void StageWasAlreadyRunning()
        {
            lock (_gate) translator.StageWasAlreadyRunning();
        }

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

    [GeneratedRegex(@"^encoding sample (?<number>\d+)/(?<count>\d+) crf (?<crf>\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex SampleStartLine();

    [GeneratedRegex(@"^crf (?<crf>\d+(?:\.\d+)?) successful$", RegexOptions.CultureInvariant)]
    private static partial Regex ChosenLine();

    [GeneratedRegex(@"^crf (?<crf>\d+(?:\.\d+)?) VMAF (?<vmaf>\d+(?:\.\d+)?) predicted video stream size (?<size>\d+(?:\.\d+)? \w+) \((?<percent>\d+)%\) taking (?<taking>.+?)(?: \(cache\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex PredictionLine();

    [GeneratedRegex(@"^(?<percent>\d+)%, (?<fps>\d+(?:\.\d+)?) fps, eta .+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();

    [GeneratedRegex(@"^Encoded (?<size>\d+(?:\.\d+)? \w+) \((?<percent>\d+)%\)$", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedLine();

    [GeneratedRegex(@"^\s+Command being timed:", RegexOptions.CultureInvariant)]
    private static partial Regex TimeReportStart();

    [GeneratedRegex(@"^Command exited with non-zero status \d+$", RegexOptions.CultureInvariant)]
    private static partial Regex NonZeroExit();
}
