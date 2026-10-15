using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests.Features.Config;

/// <summary>What <c>config init</c>, <c>config show</c> and <c>config validate</c> print and return.</summary>
[Collection(ConsoleCapture.Collection)]
public sealed class ConfigCommandsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public ConfigCommandsTests() => Directory.CreateDirectory(_directory);

    private string ConfigPath => Path.Combine(_directory, "config.yaml");

    [Fact]
    public async Task InitWritesTheTemplateAndSaysWhatToDoNext()
    {
        var (code, stdout, _) = await ConsoleCapture.RunCliAsync("config", "init", "--config", ConfigPath, "--quality", "compact");

        code.Should().Be((int)ExitCode.Success);
        stdout.ReplaceLineEndings("\n").Should().Be(
            $"Created editable configuration template: {ConfigPath}\n" +
            "Add watch.roots, then run 'video-optimiser config validate'.\n");
        (await File.ReadAllTextAsync(ConfigPath)).Should().Contain("minimumVmaf: 93");
    }

    [Fact]
    public async Task InitAsksTheQualityQuestionAtAConsole()
    {
        var (code, stdout, _) = await ConsoleCapture.RunCliWithInputAsync("3", services => services.AddSingleton<IConsoleInput>(new ScriptedInput(interactive: true)), "config", "init", "--config", ConfigPath);

        code.Should().Be((int)ExitCode.Success);
        stdout.Should().StartWith("Quality target?");
        (await File.ReadAllTextAsync(ConfigPath)).Should().Contain("minimumVmaf: 93");
    }

    [Fact]
    public async Task InitRefusesToOverwriteAnExistingFile()
    {
        await File.WriteAllTextAsync(ConfigPath, "mine");

        var (code, stdout, stderr) = await ConsoleCapture.RunCliAsync("config", "init", "--config", ConfigPath, "--quality", "compact");

        code.Should().Be((int)ExitCode.GeneralFailure);
        stdout.Should().BeEmpty();
        stderr.Trim().Should().Be($"Refusing to overwrite the existing configuration file '{ConfigPath}'.");
        (await File.ReadAllTextAsync(ConfigPath)).Should().Be("mine");
    }

    [Fact]
    public async Task ShowPrintsThePathThenTheFile()
    {
        const string yaml = "database:\n  path: 'jobs.db'\n";
        await File.WriteAllTextAsync(ConfigPath, yaml);

        var (code, stdout, _) = await ConsoleCapture.RunCliAsync("config", "show", "--config", ConfigPath);

        code.Should().Be((int)ExitCode.Success);
        stdout.ReplaceLineEndings("\n").Should().Be($"Configuration path: {ConfigPath}\n\n{yaml}");
    }

    [Fact]
    public async Task ValidateNamesTheFileWhenItIsValid()
    {
        await File.WriteAllTextAsync(ConfigPath, "database:\n  path: 'jobs.db'\n");

        var (code, stdout, _) = await ConsoleCapture.RunCliAsync(CliFakes.AcceptAnySettings, "config", "validate", "--config", ConfigPath);

        code.Should().Be((int)ExitCode.Success);
        stdout.Trim().Should().Be($"Valid configuration: {ConfigPath}");
    }

    [Fact]
    public async Task ValidatePrintsEachProblemWithItsCode()
    {
        await File.WriteAllTextAsync(ConfigPath, "database:\n  path: 'jobs.db'\n");

        var (code, stdout, stderr) = await ConsoleCapture.RunCliAsync(TwoProblems, "config", "validate", "--config", ConfigPath);

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("[NoRoots] Add a watch root.\n[NoTools] Set tools.ffprobe.\n");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void TwoProblems(IServiceCollection services) => services.AddSingleton<ISettingsValidator>(new FixedValidator());

    private sealed class FixedValidator : ISettingsValidator
    {
        public IReadOnlyList<Diagnostic> Validate(AppSettings settings) =>
        [
            new(DiagnosticCategory.Configuration, DiagnosticStatus.Fail, "NoRoots", "Add a watch root."),
            new(DiagnosticCategory.Configuration, DiagnosticStatus.Fail, "NoTools", "Set tools.ffprobe.")
        ];
    }
}
