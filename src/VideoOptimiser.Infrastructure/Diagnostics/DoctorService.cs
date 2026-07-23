using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Diagnostics;

public sealed class DoctorService(
    ISettingsValidator settingsValidator,
    IDatabaseInitializer databaseInitializer,
    IToolVerifier toolVerifier) : IDoctorService
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

        var toolChecks = new[]
        {
            toolVerifier.VerifyAsync("ab-av1", configuration.Settings.Tools.AbAv1Path, "--version", cancellationToken),
            toolVerifier.VerifyAsync("ffmpeg", configuration.Settings.Tools.FfmpegPath, "-version", cancellationToken),
            toolVerifier.VerifyAsync("ffprobe", configuration.Settings.Tools.FfprobePath, "-version", cancellationToken)
        };
        foreach (var result in await Task.WhenAll(toolChecks))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCategory.Dependency,
                result.IsAvailable ? DiagnosticStatus.Pass : DiagnosticStatus.Fail,
                result.IsAvailable ? "DependencyAvailable" : "DependencyUnavailable",
                $"{result.Name}: {result.Detail}"));
        }

        return new DoctorReport(diagnostics);
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
