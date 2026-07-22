using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Configuration;

public sealed class SettingsValidator : ISettingsValidator
{
    public IReadOnlyList<Diagnostic> Validate(AppSettings settings)
    {
        var diagnostics = new List<Diagnostic>();

        AddWhen(settings.Version != 1, "UnsupportedVersion", "Configuration version must be 1.");
        AddWhen(string.IsNullOrWhiteSpace(settings.Tools.AbAv1Path), "AbAv1PathRequired", "tools.abAv1Path is required.");
        AddWhen(string.IsNullOrWhiteSpace(settings.Tools.FfmpegPath), "FfmpegPathRequired", "tools.ffmpegPath is required.");
        AddWhen(string.IsNullOrWhiteSpace(settings.Tools.FfprobePath), "FfprobePathRequired", "tools.ffprobePath is required.");
        AddWhen(string.IsNullOrWhiteSpace(settings.Database.Path), "DatabasePathRequired", "database.path is required.");
        AddWhen(string.IsNullOrWhiteSpace(settings.Logging.Directory), "LogDirectoryRequired", "logging.directory is required.");
        AddWhen(settings.Logging.RetainDays < 1, "InvalidLogRetention", "logging.retainDays must be at least 1.");

        AddWhen(settings.Watch.Roots.Count == 0, "WatchRootsRequired", "At least one watch.roots entry is required.");
        foreach (var root in settings.Watch.Roots.Where(root => string.IsNullOrWhiteSpace(root.Path)))
        {
            _ = root;
            Add("WatchRootPathRequired", "Every watch.roots entry must include a path.");
        }

        AddWhen(settings.Eligibility.Rules.Count == 0, "EligibilityRulesRequired", "At least one eligibility.rules entry is required.");
        foreach (var rule in settings.Eligibility.Rules)
        {
            AddWhen(rule.Codecs.Count == 0, "EligibilityRuleCodecsRequired", "Every eligibility.rules entry requires codecs.");
            AddWhen(!HumanReadableValues.TryParseSize(rule.MinimumFileSize, out _), "InvalidEligibilityRuleSize", "eligibility.rules.minimumFileSize must be a size.");
            AddWhen(!HumanReadableValues.TryParseBitrate(rule.MinimumVideoBitrate, out _), "InvalidEligibilityRuleBitrate", "eligibility.rules.minimumVideoBitrate must be a positive bitrate.");
            AddWhen(!EligibilityEvaluator.IsValidResolutionBand(rule.Resolution), "InvalidEligibilityRuleResolution", "eligibility.rules.resolution must be 1080p-1440p, 1440p-4k, or 4k+.");
        }
        ValidateSize(settings.Savings.MinimumBytesSaved, "savings.minimumBytesSaved", diagnostics);
        AddWhen(settings.Quality.MinimumVmaf is < 0 or > 100, "InvalidVmaf", "quality.minimumVmaf must be between 0 and 100.");
        AddWhen(settings.Quality.Preset < 0, "InvalidPreset", "quality.preset cannot be negative.");
        AddWhen(settings.Quality.CrfSearch.MinCrf > settings.Quality.CrfSearch.MaxCrf, "InvalidCrfRange", "quality.crfSearch.minCrf cannot exceed maxCrf.");
        AddWhen(settings.Quality.CrfSearch.SampleCount < 1, "InvalidSampleCount", "quality.crfSearch.sampleCount must be at least 1.");
        ValidateDuration(settings.Quality.CrfSearch.SampleDuration, "quality.crfSearch.sampleDuration", diagnostics);
        AddWhen(string.IsNullOrWhiteSpace(settings.Quality.Encoder), "EncoderRequired", "quality.encoder is required.");
        AddWhen(!IsOneOf(settings.Original.Action, "delete"), "InvalidOriginalAction", "original.action must currently be delete.");
        AddWhen(settings.Original.Action.Equals("delete", StringComparison.OrdinalIgnoreCase) && !settings.Validation.RunFfprobe, "UnsafeDeleteValidation", "original.action delete requires validation.runFfprobe to be enabled.");
        AddWhen(!IsOneOf(settings.Validation.DecodeTestMode, "none", "sampled", "full"), "InvalidDecodeTestMode", "validation.decodeTestMode must be none, sampled, or full.");
        AddWhen(settings.Validation.DurationToleranceSeconds < 0, "InvalidDurationTolerance", "validation.durationToleranceSeconds cannot be negative.");

        return diagnostics;

        void AddWhen(bool condition, string code, string message)
        {
            if (condition)
            {
                Add(code, message);
            }
        }

        void Add(string code, string message) => diagnostics.Add(new Diagnostic(DiagnosticCategory.Configuration, DiagnosticStatus.Fail, code, message));
    }

    private static void ValidateSize(string value, string settingName, List<Diagnostic> diagnostics)
    {
        if (!HumanReadableValues.TryParseSize(value, out _))
        {
            diagnostics.Add(new Diagnostic(DiagnosticCategory.Configuration, DiagnosticStatus.Fail, "InvalidSize", $"{settingName} must use a non-negative size such as 2GiB or 100MB."));
        }
    }

    private static void ValidateDuration(string value, string settingName, List<Diagnostic> diagnostics)
    {
        if (!HumanReadableValues.TryParseDuration(value, out _))
        {
            diagnostics.Add(new Diagnostic(DiagnosticCategory.Configuration, DiagnosticStatus.Fail, "InvalidDuration", $"{settingName} must use a non-negative duration such as 10m or 500ms."));
        }
    }

    private static bool IsOneOf(string? value, params string[] permitted) =>
        !string.IsNullOrWhiteSpace(value) && permitted.Contains(value, StringComparer.OrdinalIgnoreCase);
}
