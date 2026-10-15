using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.RemoteSetup;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests.Features.RemoteSetup;

/// <summary>
/// What <c>remote setup</c> prints and returns. A fake service stands in for Hetzner, so the test pins the CLI's part: that the
/// confirmation prompt is asked and a typed answer reaches the service, and that its lines and exit code pass through. The
/// prompt is the shared <c>ConsolePrompts.ConfirmAsync</c>, whose end-of-input path <c>WorkerCommandsTests</c> pins.
///</summary>
[Collection(ConsoleCapture.Collection)]
public sealed class RemoteSetupCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

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

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(FakeSetup setup, string? input)
    {
        var config = await RunCli.WriteConfigAsync(Path.Combine(_directory, "jobs.db"));
        return await ConsoleCapture.RunCliWithInputAsync(input, services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton<IRemoteSetupService>(setup);
        }, "remote", "setup", "--config", config);
    }

    private sealed class FakeSetup : IRemoteSetupService
    {
        public bool? Answer { get; private set; }

        public async Task<ExitCode> RunAsync(LoadedConfiguration configuration, Func<string, CancellationToken, Task<bool>> confirm, IProgress<string> output, CancellationToken cancellationToken)
        {
            Answer = await confirm("Upload this key? [y/N] ", cancellationToken);
            output.Report("Setup step finished.");
            return ExitCode.PartialSuccess;
        }
    }
}
