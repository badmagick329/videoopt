using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.RemoteSetup;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests.Features.RemoteSetup;

/// <summary>
/// What <c>remote setup</c> prints and returns. A fake service stands in for Hetzner, so the test pins the CLI's part: that the
/// confirmation prompt is asked and a typed answer reaches the service, that progress goes to stdout and failures to stderr,
/// and that the Next line carries <c>--config</c> only when it was typed. The prompt is the shared
/// <c>ConsolePrompts.ConfirmAsync</c>, whose end-of-input path <c>WorkerCommandsTests</c> pins.
///</summary>
public sealed class RemoteSetupCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private string _config = string.Empty;

    public RemoteSetupCommandTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ATypedYAcceptsThePromptAndTheServiceExitCodeIsReturned()
    {
        var setup = new FakeSetup();

        var (code, stdout, stderr) = await RunAsync(setup, "y\n");

        code.Should().Be((int)ExitCode.PartialSuccess);
        stderr.Should().BeEmpty();
        setup.Answer.Should().BeTrue();
        stdout.ReplaceLineEndings("\n").Should().Be("Upload this key? [y/N] Setup step finished.\n");
    }

    [Fact]
    public async Task ACompletedSetupSuggestsRunWithTheConfigItWasTypedWith()
    {
        var setup = new FakeSetup(new RemoteSetupResult(ExitCode.Success, NextVerb: "run"), output: ["Remote setup is complete. Nothing was created on Hetzner."]);

        var (code, stdout, stderr) = await RunAsync(setup, "y\n");

        code.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be($"Upload this key? [y/N] Remote setup is complete. Nothing was created on Hetzner.\nNext: run 'video-optimiser run --config \"{_config}\"'.\n");
    }

    [Fact]
    public async Task ACompletedSetupSuggestsTheCommandWithNoConfigWhenNoneWasTyped()
    {
        var setup = new FakeSetup(new RemoteSetupResult(ExitCode.Success, NextVerb: "immich run --first"), output: ["Remote setup is complete. Nothing was created on Hetzner."]);

        var (code, stdout, stderr) = await RunWithConfigFromEnvironmentAsync(setup, "y\n");

        code.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be("Upload this key? [y/N] Remote setup is complete. Nothing was created on Hetzner.\nNext: run 'video-optimiser immich run --first'.\n");
    }

    [Fact]
    public async Task AFailureIsWrittenToStderrAndNothingIsSuggested()
    {
        var setup = new FakeSetup(new RemoteSetupResult(ExitCode.InvalidConfiguration), output: [], errors: ["No Hetzner API token was found."]);

        var (code, stdout, stderr) = await RunAsync(setup, "y\n");

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.ReplaceLineEndings("\n").Should().Be("Upload this key? [y/N] ");
        stderr.ReplaceLineEndings("\n").Should().Be("No Hetzner API token was found.\n");
    }

    [Fact]
    public async Task AConfigThatIsStillInvalidListsItsProblemsOnStderrAndSaysHowToCheckThem()
    {
        var setup = new FakeSetup(
            new RemoteSetupResult(ExitCode.InvalidConfiguration, ConfigStillInvalid: true),
            output: ["Updated config.yaml; the previous version is in config.yaml.bak."],
            errors: ["[WatchRootsMissing] watch.roots needs at least one folder."]);

        var (code, stdout, stderr) = await RunAsync(setup, "y\n");

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.ReplaceLineEndings("\n").Should().Be("Upload this key? [y/N] Updated config.yaml; the previous version is in config.yaml.bak.\n");
        stderr.ReplaceLineEndings("\n").Should().Be($"[WatchRootsMissing] watch.roots needs at least one folder.\nThe configuration still has the problems above. Fix them, then run 'video-optimiser config validate --config \"{_config}\"'.\n");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(FakeSetup setup, string? input)
    {
        _config = await RunCli.WriteConfigAsync(Path.Combine(_directory, "jobs.db"));
        return await ConsoleCapture.RunCliWithInputAsync(input, services => Configure(services, setup), "remote", "setup", "--config", _config);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunWithConfigFromEnvironmentAsync(FakeSetup setup, string? input)
    {
        _config = await RunCli.WriteConfigAsync(Path.Combine(_directory, "jobs.db"));
        var environment = new FixedEnvironment(("VIDEO_OPTIMISER_CONFIG", _config));
        return await ConsoleCapture.RunCliWithInputAsync(input, services =>
        {
            Configure(services, setup);
            services.AddSingleton<IEnvironmentVariables>(environment);
        }, "remote", "setup");
    }

    private static void Configure(IServiceCollection services, FakeSetup setup)
    {
        CliFakes.AcceptAnySettings(services);
        services.AddSingleton<IRemoteSetupService>(setup);
    }

    private sealed class FakeSetup(RemoteSetupResult? result = null, string[]? output = null, string[]? errors = null) : IRemoteSetupService
    {
        public bool? Answer { get; private set; }

        public async Task<RemoteSetupResult> RunAsync(LoadedConfiguration configuration, Func<string, CancellationToken, Task<bool>> confirm, IProgress<string> progress, IProgress<string> errorSink, CancellationToken cancellationToken)
        {
            Answer = await confirm("Upload this key? [y/N] ", cancellationToken);
            foreach (var line in output ?? ["Setup step finished."]) progress.Report(line);
            foreach (var line in errors ?? []) errorSink.Report(line);
            return result ?? new RemoteSetupResult(ExitCode.PartialSuccess);
        }
    }
}
