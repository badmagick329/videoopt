using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Processing;
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
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
