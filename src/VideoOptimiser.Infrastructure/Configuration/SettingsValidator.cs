using VideoOptimiser.Application.Configuration;
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

        ValidateSize(settings.Processing.MinimumFileSize, "processing.minimumFileSize", diagnostics);
        ValidateSize(settings.Savings.MinimumBytesSaved, "savings.minimumBytesSaved", diagnostics);
        ValidateDuration(settings.Watch.ReconciliationInterval, "watch.reconciliationInterval", diagnostics);
        ValidateDuration(settings.Watch.Stability.PollInterval, "watch.stability.pollInterval", diagnostics);
        ValidateDuration(settings.Watch.Stability.MinimumAge, "watch.stability.minimumAge", diagnostics);
        ValidateDuration(settings.Watch.Stability.Timeout, "watch.stability.timeout", diagnostics);
        ValidateDuration(settings.Processing.RetryDelay, "processing.retryDelay", diagnostics);

        AddWhen(settings.Processing.MaximumConcurrentJobs < 1, "InvalidConcurrency", "processing.maximumConcurrentJobs must be at least 1.");
        AddWhen(settings.Watch.Stability.RequiredStableChecks < 1, "InvalidStableChecks", "watch.stability.requiredStableChecks must be at least 1.");
        AddWhen(settings.Processing.RetryCount < 0, "InvalidRetryCount", "processing.retryCount cannot be negative.");
        AddWhen(settings.Quality.MinimumVmaf is < 0 or > 100, "InvalidVmaf", "quality.minimumVmaf must be between 0 and 100.");
        AddWhen(settings.Quality.Preset < 0, "InvalidPreset", "quality.preset cannot be negative.");
        AddWhen(string.IsNullOrWhiteSpace(settings.Quality.Encoder), "EncoderRequired", "quality.encoder is required.");
        AddWhen(!IsOneOf(settings.Output.Container, "preserve", "mkv", "mp4"), "InvalidContainer", "output.container must be preserve, mkv, or mp4.");
        AddWhen(string.IsNullOrWhiteSpace(settings.Output.TemporarySuffix), "TemporarySuffixRequired", "output.temporarySuffix is required.");
        AddWhen(!IsOneOf(settings.Original.Action, "keep", "archive", "delete"), "InvalidOriginalAction", "original.action must be keep, archive, or delete.");
        AddWhen(settings.Original.Action.Equals("archive", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(settings.Original.ArchiveDirectory), "ArchiveDirectoryRequired", "original.archiveDirectory is required when original.action is archive.");
        AddWhen(!IsOneOf(settings.Original.CollisionStrategy, "fail", "overwrite", "timestamp", "increment", "hash"), "InvalidCollisionStrategy", "original.collisionStrategy must be fail, overwrite, timestamp, increment, or hash.");
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
