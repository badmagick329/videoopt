using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Diagnostics;

public sealed class DoctorService(
    ISettingsValidator settingsValidator,
    IDatabaseInitializer databaseInitializer,
    IToolVerifier toolVerifier,
    IRemoteEnvironmentVerifier remoteEnvironmentVerifier,
    IRemoteWorkerLifecycle remoteWorkerLifecycle,
    IImmichClientFactory immichClients) : IDoctorService
{
    public async Task<DoctorReport> RunAsync(LoadedConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<Diagnostic>(settingsValidator.Validate(configuration.Settings));
        if (!diagnostics.Any(diagnostic => diagnostic.Category == DiagnosticCategory.Configuration && diagnostic.Status == DiagnosticStatus.Fail))
        {
            diagnostics.Add(new Diagnostic(DiagnosticCategory.Configuration, DiagnosticStatus.Pass, "ConfigurationValid", $"Configuration is valid: {configuration.Path}"));
        }

        diagnostics.Add(await DescribeEncodingLocationAsync(configuration.Settings, cancellationToken));


        foreach (var root in configuration.Settings.Watch.Roots.Where(root => !string.IsNullOrWhiteSpace(root.Path)))
        {
            CheckExistingDirectory(root.Path, "WatchRoot", diagnostics);
        }

        await CheckDatabaseAsync(configuration.Settings.Database.Path, diagnostics, cancellationToken);
        AddFreeSpaceDiagnostics(configuration.Settings, diagnostics);

        if (configuration.Settings.Immich.IsConfigured && !diagnostics.Any(diagnostic => diagnostic.Code.StartsWith("InvalidImmich", StringComparison.Ordinal)))
        {
            await CheckImmichAsync(configuration.Settings.Immich, diagnostics, cancellationToken);
        }

        var isRemote = configuration.Settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase);
        var toolChecks = new List<Task<ToolVerificationResult>>
        {
            toolVerifier.VerifyAsync("ffmpeg", configuration.Settings.Tools.FfmpegPath, "-version", cancellationToken),
            toolVerifier.VerifyAsync("ffprobe", configuration.Settings.Tools.FfprobePath, "-version", cancellationToken)
        };
        if (isRemote)
        {
            toolChecks.Add(toolVerifier.VerifyAsync("ssh", configuration.Settings.Tools.SshPath, "-V", cancellationToken));
            toolChecks.Add(toolVerifier.VerifyPresenceAsync("sftp", configuration.Settings.Tools.SftpPath, "-h", cancellationToken));
        }
        else
        {
            toolChecks.Add(toolVerifier.VerifyAsync("ab-av1", configuration.Settings.Tools.AbAv1Path, "--version", cancellationToken));
        }

        var toolResults = await Task.WhenAll(toolChecks);
        foreach (var result in toolResults)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCategory.Dependency,
                result.IsAvailable ? DiagnosticStatus.Pass : DiagnosticStatus.Fail,
                result.IsAvailable ? "DependencyAvailable" : "DependencyUnavailable",
                $"{result.Name}: {result.Detail}"));
        }

        if (!isRemote)
        {
            AddAbAv1VersionDiagnostic(toolResults, diagnostics);
        }

        if (toolResults.First(result => result.Name == "ffmpeg").IsAvailable)
        {
            await AddFfmpegFeatureDiagnosticsAsync(configuration.Settings, isRemote, diagnostics, cancellationToken);
        }

        var canCheckRemoteHost = isRemote && toolResults.First(result => result.Name == "ssh").IsAvailable && toolResults.First(result => result.Name == "sftp").IsAvailable;
        if (remoteWorkerLifecycle.IsManaged(configuration.Settings))
        {
            canCheckRemoteHost &= await CheckManagedWorkerAsync(configuration.Settings, diagnostics, cancellationToken);
        }

        if (canCheckRemoteHost)
        {
            diagnostics.AddRange(await remoteEnvironmentVerifier.VerifyAsync(
                configuration.Settings.Tools.SshPath,
                configuration.Settings.Processing.RemoteSsh,
                cancellationToken));
        }

        return new DoctorReport(diagnostics);
    }

    /// <summary>
    /// Starting is not enough: ab-av1 older than <see cref="AbAv1VersionPolicy.Minimum"/> passes <c>--version</c> yet is not the
    /// code CI tests, so local mode applies the same floor as the remote worker.
    /// </summary>
    private static void AddAbAv1VersionDiagnostic(ToolVerificationResult[] toolResults, List<Diagnostic> diagnostics)
    {
        var abAv1 = toolResults.First(result => result.Name == "ab-av1");
        if (!abAv1.IsAvailable)
        {
            return;
        }

        var minimum = AbAv1VersionPolicy.Minimum;
        if (!AbAv1VersionPolicy.TryParse(abAv1.Detail, out var version))
        {
            diagnostics.Add(DependencyFailure("AbAv1VersionUnavailable", $"ab-av1 version could not be determined from '{abAv1.Detail}'; minimum required version is {minimum}."));
            return;
        }

        diagnostics.Add(version >= minimum
            ? new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Pass, "AbAv1VersionSufficient", $"ab-av1 version {version} meets the minimum required version {minimum}.")
            : DependencyFailure(
                "AbAv1VersionInsufficient",
                $"ab-av1 version {version} is too old; minimum required version is {minimum}. Update it with 'cargo install ab-av1 --version {minimum} --locked' or download it from https://github.com/alexheretic/ab-av1/releases."));
    }

    /// <summary>
    /// A local run encodes with ab-av1, which drives FFmpeg's libsvtav1 encoder and libvmaf filter on 10-bit input, so a build
    /// without them fails mid-job. Both modes decode the AV1 output in validation, and FFmpeg's native AV1 decoder needs hardware
    /// acceleration, so libdav1d is required unless the decode test is off. Queries run concurrently and only for the listings
    /// that a required feature needs.
    /// </summary>
    private async Task AddFfmpegFeatureDiagnosticsAsync(AppSettings settings, bool isRemote, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var features = new List<FfmpegFeature>();
        if (!isRemote)
        {
            features.AddRange(LocalEncodeFeatures);
        }

        var requiresDecoder = !settings.Validation.DecodeTest.Equals(DecodeTestModes.None, StringComparison.OrdinalIgnoreCase);
        if (requiresDecoder)
        {
            features.Add(Libdav1dDecoder);
        }

        var listings = features.Select(feature => feature.Listing).Distinct().ToList();
        var outputs = await Task.WhenAll(listings.Select(listing =>
            toolVerifier.ReadOutputAsync(settings.Tools.FfmpegPath, ["-hide_banner", $"-{listing.Argument}"], cancellationToken)));

        foreach (var feature in features)
        {
            var output = outputs[listings.IndexOf(feature.Listing)];
            if (output is null)
            {
                diagnostics.Add(DependencyFailure(feature.UnavailableCode, $"The FFmpeg {feature.Listing.Name} listing could not be read, so {feature.Description} cannot be verified."));
                continue;
            }

            diagnostics.Add(ListsToken(output, feature.Token)
                ? new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Pass, feature.AvailableCode, $"FFmpeg exposes {feature.Description}.")
                : DependencyFailure(feature.UnavailableCode, $"FFmpeg does not expose {feature.Description}; {feature.Consequence}"));
        }
    }

    // Matches a whole whitespace-delimited token, as the remote probe's grep does, so 'libsvtav1' never matches 'libsvtav1x'.
    private static bool ListsToken(string listing, string token) =>
        listing.Split('\n')
            .Any(line => line.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal));

    private static readonly char[] TokenSeparators = [' ', '\t', '\r'];

    private static Diagnostic DependencyFailure(string code, string message) => new(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, code, message);

    private sealed record FfmpegListing(string Argument, string Name);

    private sealed record FfmpegFeature(FfmpegListing Listing, string Token, string Description, string Consequence, string AvailableCode, string UnavailableCode);

    private static readonly FfmpegListing Encoders = new("encoders", "encoder");
    private static readonly FfmpegListing Filters = new("filters", "filter");
    private static readonly FfmpegListing PixelFormats = new("pix_fmts", "pixel format");
    private static readonly FfmpegListing Decoders = new("decoders", "decoder");

    private static readonly FfmpegFeature[] LocalEncodeFeatures =
    [
        new(Encoders, "libsvtav1", "the libsvtav1 encoder", "ab-av1 needs it to encode AV1.", "FfmpegFeatureAvailable", "FfmpegFeatureUnavailable"),
        new(Filters, "libvmaf", "the libvmaf filter", "ab-av1 needs it to score quality.", "FfmpegFeatureAvailable", "FfmpegFeatureUnavailable"),
        new(PixelFormats, "yuv420p10le", "the yuv420p10le pixel format", "ab-av1 needs it for 10-bit AV1 output.", "FfmpegFeatureAvailable", "FfmpegFeatureUnavailable")
    ];

    private static readonly FfmpegFeature Libdav1dDecoder = new(
        Decoders,
        "libdav1d",
        "the libdav1d software AV1 decoder",
        "validation's decode test cannot decode AV1 outputs without it. Install a full FFmpeg build such as gyan.dev's \"full\" build, or set validation.decodeTest to none.",
        "Av1DecoderAvailable",
        "Av1DecoderUnavailable");

    /// <summary>
    /// The same line runs print first, so doctor and a run say the same thing about where encoding happens. An unreadable
    /// worker state file is reported by the Hetzner check, so here it only drops the retained-worker detail.
    /// </summary>
    private async Task<Diagnostic> DescribeEncodingLocationAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        RemoteWorkerInfo? worker = null;
        if (remoteWorkerLifecycle.IsManaged(settings))
        {
            try
            {
                worker = await remoteWorkerLifecycle.ReadRecordedAsync(settings.Database.Path, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
            }
        }

        return new Diagnostic(DiagnosticCategory.Processing, DiagnosticStatus.Pass, "EncodingLocation", RunBanner.Describe(settings, worker));
    }

    /// <summary>
    /// A managed worker exists only between its creation by a run or <c>worker create</c> and its deletion, so having none is
    /// the normal state, not a failure. Without one, only the Hetzner token and API access can be checked; with a running one,
    /// the lookup points the settings at it so the remote host checks run against it. Returns whether such a worker is running.
    /// </summary>
    private async Task<bool> CheckManagedWorkerAsync(AppSettings settings, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        RemoteWorkerInfo? worker;
        try
        {
            worker = await remoteWorkerLifecycle.GetAsync(settings, settings.Database.Path, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            diagnostics.Add(new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, "HetznerApiUnavailable", $"Hetzner API check failed: {exception.Message}"));
            return false;
        }

        diagnostics.Add(new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Pass, "HetznerApiAvailable", "Hetzner API accepted the token."));
        if (worker?.Status == "running") return true;

        var state = worker is null ? "No managed Hetzner worker exists" : $"Managed Hetzner worker {worker.ServerId} is {worker.Status}";
        diagnostics.Add(new Diagnostic(
            DiagnosticCategory.Dependency,
            DiagnosticStatus.Warning,
            "RemoteHostChecksSkipped",
            $"{state}, so remote host checks were skipped. Run 'worker create', then doctor, to check one."));
        return false;
    }

    /// <summary>
    /// The version check needs no key, so a wrong URL and a bad key are reported separately. Immich 3 is required because the
    /// search filters and endpoints were checked against v3.2.2.
    /// </summary>
    private async Task CheckImmichAsync(ImmichSettings immich, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        await CheckDirectoryAsync(immich.StagingDirectory, "ImmichStagingDirectory", createIfMissing: true, diagnostics, cancellationToken);
        var client = immichClients.Create(immich);
        try
        {
            var version = await client.GetServerVersionAsync(cancellationToken);
            if (version.Major != 3)
            {
                diagnostics.Add(new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, "ImmichVersionUnsupported", $"Immich {version} at {immich.BaseUrl} is not supported; VideoOptimiser needs Immich 3."));
                return;
            }

            diagnostics.Add(new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Pass, "ImmichServerAvailable", $"Immich {version} is reachable at {immich.BaseUrl}."));
            var user = await client.GetCurrentUserAsync(cancellationToken);
            diagnostics.Add(new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Pass, "ImmichApiKeyAccepted", $"Immich accepted the API key for {user.Name}."));
            var trash = await client.GetTrashSettingsAsync(cancellationToken);
            diagnostics.Add(
                !trash.IsOn ? new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, ImmichTrash.DisabledCode, ImmichTrash.DisabledMessage)
                : trash.Days < ImmichTrash.ShortDays ? new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Warning, "ImmichTrashShort", $"Immich keeps trashed videos for {trash.Days} {(trash.Days == 1 ? "day" : "days")}, so a converted original can be restored from its trash only that long after finalize. Raise it under Administration > Settings > Trash Settings.")
                : new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Pass, "ImmichTrashOn", $"Immich keeps trashed videos for {trash.Days} days before deleting them."));
        }
        catch (ImmichApiException exception)
        {
            diagnostics.Add(new Diagnostic(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, exception.Code, exception.Message));
        }
    }

    private static async Task CheckDirectoryAsync(string path, string code, bool createIfMissing, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            diagnostics.Add(new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Warning, $"{code}NotConfigured", $"{code} is not configured."));
            return;
        }

        try
        {
            if (createIfMissing)
            {
                Directory.CreateDirectory(path);
            }

            var probe = Path.Combine(path, $".video-optimiser-write-probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "probe", cancellationToken);
            File.Delete(probe);
            diagnostics.Add(new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Pass, $"{code}Writable", $"{code} is writable: {path}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Fail, $"{code}Unavailable", $"{code} is not writable: {path}. {exception.Message}"));
        }
    }

    private static void CheckExistingDirectory(string path, string code, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Directory.Exists(path)
            ? new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Pass, $"{code}Exists", $"{code} exists: {path}")
            : new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Fail, $"{code}Missing", $"{code} does not exist: {path}"));
    }

    private async Task CheckDatabaseAsync(string path, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            diagnostics.Add(new Diagnostic(DiagnosticCategory.Database, DiagnosticStatus.Fail, "DatabasePathNotConfigured", "database.path is not configured."));
            return;
        }

        try
        {
            await databaseInitializer.InitializeAsync(path, cancellationToken);
            diagnostics.Add(new Diagnostic(DiagnosticCategory.Database, DiagnosticStatus.Pass, "DatabaseAvailable", $"SQLite database is ready: {path}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            diagnostics.Add(new Diagnostic(DiagnosticCategory.Database, DiagnosticStatus.Fail, "DatabaseUnavailable", $"SQLite database cannot be initialised: {exception.Message}"));
        }
    }

    private static void AddFreeSpaceDiagnostics(AppSettings settings, List<Diagnostic> diagnostics)
    {
        var paths = settings.Watch.Roots.Select(root => root.Path)
            .Append(settings.Database.Path)
            .Append(settings.Immich.IsConfigured ? settings.Immich.StagingDirectory : string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrWhiteSpace(root) || root.StartsWith("\\\\", StringComparison.Ordinal))
                {
                    diagnostics.Add(new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Warning, "FreeSpaceUnavailable", $"Free space was not determined for '{path}'."));
                    continue;
                }

                var drive = new DriveInfo(root);
                diagnostics.Add(new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Pass, "FreeSpaceAvailable", $"{drive.Name} has {drive.AvailableFreeSpace} bytes free."));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Warning, "FreeSpaceUnavailable", $"Free space was not determined for '{path}': {exception.Message}"));
            }
        }
    }
}
