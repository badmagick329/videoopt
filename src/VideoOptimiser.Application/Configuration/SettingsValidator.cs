using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Configuration;

public sealed class SettingsValidator : ISettingsValidator
{
    /// <summary>Also the remote session's refusal, for a mode given for one run that skipped this check.</summary>
    public const string StageSilenceTimeoutMessage = "processing.remoteSsh.stageSilenceTimeout must be a positive duration such as 30m.";

    public IReadOnlyList<Diagnostic> Validate(AppSettings settings)
    {
        var diagnostics = new List<Diagnostic>();
        var remoteMode = settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase);
        var managedHetzner = remoteMode && settings.Processing.RemoteSsh.Lifecycle.Equals(RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase);

        AddWhen(settings.Version != 1, "UnsupportedVersion", "Configuration version must be 1.");
        AddWhen(!IsOneOf(settings.Processing.Mode, ProcessingModes.Local, ProcessingModes.RemoteSsh), "InvalidProcessingMode", "processing.mode must be local or remoteSsh.");
        AddWhen(!remoteMode && string.IsNullOrWhiteSpace(settings.Tools.AbAv1Path), "AbAv1PathRequired", "tools.abAv1Path is required in local mode.");
        AddWhen(string.IsNullOrWhiteSpace(settings.Tools.FfmpegPath), "FfmpegPathRequired", "tools.ffmpegPath is required.");
        AddWhen(string.IsNullOrWhiteSpace(settings.Tools.FfprobePath), "FfprobePathRequired", "tools.ffprobePath is required.");
        if (remoteMode)
        {
            AddWhen(string.IsNullOrWhiteSpace(settings.Tools.SshPath), "SshPathRequired", "tools.sshPath is required in remoteSsh mode.");
            AddWhen(string.IsNullOrWhiteSpace(settings.Tools.SftpPath), "SftpPathRequired", "tools.sftpPath is required in remoteSsh mode.");
            AddWhen(!IsOneOf(settings.Processing.RemoteSsh.Lifecycle, RemoteLifecycleModes.Manual, RemoteLifecycleModes.Hetzner), "InvalidRemoteLifecycle", "processing.remoteSsh.lifecycle must be manual or hetzner.");
            AddWhen(!managedHetzner && string.IsNullOrWhiteSpace(settings.Processing.RemoteSsh.Host), "RemoteHostRequired", "processing.remoteSsh.host is required with manual lifecycle.");
            AddWhen(!managedHetzner && !IsValidRemoteHost(settings.Processing.RemoteSsh.Host), "InvalidRemoteHost", "processing.remoteSsh.host must be a single SSH host or config alias and cannot begin with '-'.");
            AddWhen(!IsAbsolutePosixPath(settings.Processing.RemoteSsh.WorkingDirectory), "InvalidRemoteWorkingDirectory", "processing.remoteSsh.workingDirectory must be an absolute POSIX path without line breaks.");
            AddWhen(settings.Processing.RemoteSsh.MinimumCpuCount < 1, "InvalidRemoteMinimumCpuCount", "processing.remoteSsh.minimumCpuCount must be at least 1.");
            AddWhen(!HumanReadableValues.TryParseSize(settings.Processing.RemoteSsh.MinimumAvailableMemory, out var minimumMemory) || minimumMemory < 1, "InvalidRemoteMinimumMemory", "processing.remoteSsh.minimumAvailableMemory must be a positive size such as 14GiB.");
            AddWhen(!double.IsFinite(settings.Processing.RemoteSsh.MinimumFreeDiskMultiplier) || settings.Processing.RemoteSsh.MinimumFreeDiskMultiplier <= 0, "InvalidRemoteDiskMultiplier", "processing.remoteSsh.minimumFreeDiskMultiplier must be greater than zero.");
            AddWhen(!HumanReadableValues.TryParseDuration(settings.Processing.RemoteSsh.StageSilenceTimeout, out var stageSilence) || stageSilence <= TimeSpan.Zero, "InvalidRemoteStageSilenceTimeout", StageSilenceTimeoutMessage);
            if (managedHetzner)
            {
                var hetzner = settings.Processing.RemoteSsh.Hetzner;
                AddWhen(string.IsNullOrWhiteSpace(hetzner.ApiTokenEnvironmentVariable) || hetzner.ApiTokenEnvironmentVariable.Any(char.IsWhiteSpace), "InvalidHetznerTokenVariable", "processing.remoteSsh.hetzner.apiTokenEnvironmentVariable must name one environment variable.");
                AddWhen(!string.IsNullOrWhiteSpace(hetzner.ApiTokenFile) && Directory.Exists(hetzner.ApiTokenFile), "InvalidHetznerTokenFile", "processing.remoteSsh.hetzner.apiTokenFile must be a file path, not a directory.");
                AddWhen(hetzner.ServerTypes is null || hetzner.ServerTypes.Count == 0, "HetznerServerTypesRequired", "processing.remoteSsh.hetzner.serverTypes must list at least one server type, such as [\"cx43\", \"cx53\", \"cpx42\", \"ccx33\"].");
                AddWhen(hetzner.ServerTypes is not null && hetzner.ServerTypes.Any(type => string.IsNullOrWhiteSpace(type) || type.Any(char.IsWhiteSpace)), "InvalidHetznerServerTypes", "processing.remoteSsh.hetzner.serverTypes must contain non-empty server type names without whitespace.");
                AddWhen(hetzner.MaxHourlyPrice <= 0, "InvalidHetznerMaxHourlyPrice", "processing.remoteSsh.hetzner.maxHourlyPrice must be greater than zero, in EUR per hour including VAT. Set it to null for no cap.");
                AddWhen(string.IsNullOrWhiteSpace(hetzner.Image), "HetznerImageRequired", "processing.remoteSsh.hetzner.image is required.");
                AddWhen(hetzner.Locations is null || hetzner.Locations.Count == 0, "HetznerLocationsRequired", "processing.remoteSsh.hetzner.locations must contain at least one location.");
                if (hetzner.Locations is not null)
                {
                    AddWhen(hetzner.Locations.Any(location => string.IsNullOrWhiteSpace(location) || location.Any(char.IsWhiteSpace)), "InvalidHetznerLocations", "processing.remoteSsh.hetzner.locations must contain non-empty location names without whitespace.");
                }
                AddWhen(string.IsNullOrWhiteSpace(hetzner.SshKeyName), "HetznerSshKeyRequired", "processing.remoteSsh.hetzner.sshKeyName is required.");
                AddWhen(string.IsNullOrWhiteSpace(settings.Processing.RemoteSsh.IdentityFile), "RemoteIdentityFileRequired", "processing.remoteSsh.identityFile is required with Hetzner lifecycle.");
                AddWhen(!string.IsNullOrWhiteSpace(settings.Processing.RemoteSsh.IdentityFile) && !File.Exists(settings.Processing.RemoteSsh.IdentityFile), "RemoteIdentityFileMissing", "processing.remoteSsh.identityFile does not exist.");
                AddWhen(string.IsNullOrWhiteSpace(hetzner.BootstrapScriptPath) || !File.Exists(hetzner.BootstrapScriptPath), "HetznerBootstrapMissing", "processing.remoteSsh.hetzner.bootstrapScriptPath does not exist.");
                AddWhen(!HumanReadableValues.TryParseDuration(hetzner.BootstrapTimeout, out var bootstrapTimeout) || bootstrapTimeout <= TimeSpan.Zero, "InvalidHetznerBootstrapTimeout", "processing.remoteSsh.hetzner.bootstrapTimeout must be a positive duration such as 90m.");
            }
        }
        AddWhen(string.IsNullOrWhiteSpace(settings.Database.Path), "DatabasePathRequired", "database.path is required.");

        // An Immich-only setup needs no folders; queue add checks for roots itself.
        AddWhen(settings.Watch.Roots.Count == 0 && !settings.Immich.IsConfigured, "WatchRootsRequired", "At least one watch.roots entry is required, or set immich.baseUrl to convert only Immich videos.");
        foreach (var root in settings.Watch.Roots.Where(root => string.IsNullOrWhiteSpace(root.Path)))
        {
            _ = root;
            Add("WatchRootPathRequired", "Every watch.roots entry must include a path.");
        }

        var immich = settings.Immich;
        if (immich.IsConfigured)
        {
            AddWhen(!IsHttpUrl(immich.BaseUrl), "InvalidImmichBaseUrl", "immich.baseUrl must be an absolute http or https URL, such as http://IMMICH_HOST:2283.");
            AddWhen(string.IsNullOrWhiteSpace(immich.ApiKeyEnvironmentVariable) || immich.ApiKeyEnvironmentVariable.Any(char.IsWhiteSpace), "InvalidImmichApiKeyVariable", "immich.apiKeyEnvironmentVariable must name one environment variable.");
            AddWhen(!string.IsNullOrWhiteSpace(immich.ApiKeyFile) && Directory.Exists(immich.ApiKeyFile), "InvalidImmichApiKeyFile", "immich.apiKeyFile must be a file path, not a directory.");
            AddWhen(immich.MinimumAgeDays < 1, "InvalidImmichMinimumAge", "immich.minimumAgeDays must be at least 1.");
            AddWhen(string.IsNullOrWhiteSpace(immich.StagingDirectory) || File.Exists(immich.StagingDirectory), "InvalidImmichStagingDirectory", "immich.stagingDirectory must be a folder path.");
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
        AddWhen(settings.Quality.MaxEncodedPercent is < 1 or > 100, "InvalidMaxEncodedPercent", "quality.maxEncodedPercent must be a whole number between 1 and 100.");
        AddWhen(settings.Quality.Preset < 0, "InvalidPreset", "quality.preset cannot be negative.");
        AddWhen(settings.Quality.CrfSearch.MinCrf > settings.Quality.CrfSearch.MaxCrf, "InvalidCrfRange", "quality.crfSearch.minCrf cannot exceed maxCrf.");
        AddWhen(settings.Quality.CrfSearch.ConfiguredSampleCount is < 1, "InvalidSampleCount", "quality.crfSearch.sampleCount must be at least 1.");
        ValidateDuration(settings.Quality.CrfSearch.SampleDuration, "quality.crfSearch.sampleDuration", diagnostics);
        AddWhen(string.IsNullOrWhiteSpace(settings.Quality.Encoder), "EncoderRequired", "quality.encoder is required.");
        AddWhen(!IsOneOf(settings.Quality.UnsupportedAudio, UnsupportedAudioActions.Drop, UnsupportedAudioActions.Skip), "InvalidUnsupportedAudio", "quality.unsupportedAudio must be drop or skip.");
        AddWhen(!IsOneOf(settings.Original.Action, OriginalActions.Delete, OriginalActions.Archive, OriginalActions.Keep), "InvalidOriginalAction", "original.action must be delete, archive, or keep.");
        AddWhen(settings.Original.Action.Equals(OriginalActions.Archive, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(settings.Original.ArchiveDirectory), "ArchiveDirectoryRequired", "original.archiveDirectory is required when original.action is archive.");
        AddWhen(!string.IsNullOrWhiteSpace(settings.Original.ArchiveDirectory) && !IsFolderNameOrAbsolutePath(settings.Original.ArchiveDirectory), "InvalidArchiveDirectory", "original.archiveDirectory must be a folder name, such as Archive, or an absolute path.");
        AddWhen(!IsOneOf(settings.Validation.DecodeTest, DecodeTestModes.None, DecodeTestModes.Sampled, DecodeTestModes.Full), "InvalidDecodeTest", "validation.decodeTest must be none, sampled, or full.");
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

    // A relative path with separators would be ambiguous between the config folder and the original's folder, so only a bare name is relative.
    private static bool IsFolderNameOrAbsolutePath(string value) =>
        Path.IsPathFullyQualified(value) ||
        (value is not "." and not ".." && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && value.Trim() == value);

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(uri.Query);

    private static bool IsOneOf(string? value, params string[] permitted) =>
        !string.IsNullOrWhiteSpace(value) && permitted.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool IsValidRemoteHost(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith('-') &&
        !value.Any(char.IsWhiteSpace) &&
        !value.Contains('\0');

    private static bool IsAbsolutePosixPath(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith('/') &&
        !value.Contains('\0') &&
        !value.Contains('\r') &&
        !value.Contains('\n') &&
        value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 0 &&
        !value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..");
}
