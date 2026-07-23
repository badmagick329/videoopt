using VideoOptimiser.Application.Configuration;
using YamlDotNet.Serialization;

namespace VideoOptimiser.Infrastructure.Configuration;

public sealed class YamlConfigurationLoader : IConfigurationLoader
{
    private readonly IDeserializer _deserializer = new StaticDeserializerBuilder(new VideoOptimiserYamlContext())
        .WithCaseInsensitivePropertyMatching()
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<LoadedConfiguration> LoadAsync(string? explicitPath, CancellationToken cancellationToken = default)
    {
        var path = ResolveConfigurationPath(explicitPath);
        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        var settings = _deserializer.Deserialize<AppSettings>(yaml) ?? throw new InvalidDataException("The configuration file is empty.");

        NormalizeDefaults(settings);
        ResolveRelativePaths(settings, Path.GetDirectoryName(path)!);
        return new LoadedConfiguration(settings, path, yaml);
    }

    public string GetDefaultConfigurationPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VideoOptimiser",
        "config.yaml");

    private string ResolveConfigurationPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return RequireExisting(explicitPath, "--config");
        }

        var environmentPath = Environment.GetEnvironmentVariable("VIDEO_OPTIMISER_CONFIG");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return RequireExisting(environmentPath, "VIDEO_OPTIMISER_CONFIG");
        }

        var platformDefault = GetDefaultConfigurationPath();
        if (File.Exists(platformDefault))
        {
            return Path.GetFullPath(platformDefault);
        }

        var workingDirectoryFallback = Path.Combine(Environment.CurrentDirectory, "video-optimiser.yaml");
        if (File.Exists(workingDirectoryFallback))
        {
            return Path.GetFullPath(workingDirectoryFallback);
        }

        throw new FileNotFoundException(
            $"No configuration file was found. Supply --config, set VIDEO_OPTIMISER_CONFIG, or create '{platformDefault}' or '{workingDirectoryFallback}'.");
    }

    private static string RequireExisting(string path, string source)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The configuration file selected by {source} does not exist: '{fullPath}'.", fullPath);
        }

        return fullPath;
    }

    private static void ResolveRelativePaths(AppSettings settings, string configurationDirectory)
    {
        settings.Database.Path = ResolvePath(settings.Database.Path, configurationDirectory);
        settings.Tools.AbAv1Path = ResolveExecutablePath(settings.Tools.AbAv1Path, configurationDirectory);
        settings.Tools.FfmpegPath = ResolveExecutablePath(settings.Tools.FfmpegPath, configurationDirectory);
        settings.Tools.FfprobePath = ResolveExecutablePath(settings.Tools.FfprobePath, configurationDirectory);
        foreach (var root in settings.Watch.Roots)
        {
            root.Path = ResolvePath(root.Path, configurationDirectory);
        }
    }

    private static void NormalizeDefaults(AppSettings settings)
    {
        settings.Tools ??= new ToolSettings();
        settings.Database ??= new DatabaseSettings();
        settings.Watch ??= new WatchSettings();
        settings.Eligibility ??= new EligibilitySettings();
        settings.Quality ??= new QualitySettings();
        settings.Savings ??= new SavingsSettings();
        settings.Original ??= new OriginalSettings();
        settings.Validation ??= new ValidationSettings();
        settings.Watch.Roots ??= [];
        settings.Eligibility.Extensions ??= [];
        settings.Eligibility.Rules ??= [];
        settings.Eligibility.ExcludedExtensions ??= [];
        settings.Eligibility.ExcludedNamePatterns ??= [];
        settings.Eligibility.ExcludedDirectories ??= [];
        settings.Quality.CrfSearch ??= new CrfSearchSettings();
    }

    private static string ResolvePath(string path, string configurationDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(configurationDirectory, path));
    }

    private static string ResolveExecutablePath(string path, string configurationDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || (!path.Contains(Path.DirectorySeparatorChar) && !path.Contains(Path.AltDirectorySeparatorChar)))
        {
            return path;
        }

        return ResolvePath(path, configurationDirectory);
    }
}
