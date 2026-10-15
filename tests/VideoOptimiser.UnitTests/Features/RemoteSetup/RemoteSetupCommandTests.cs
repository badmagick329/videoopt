using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.RemoteSetup;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests.Features.RemoteSetup;

/// <summary>
/// What <c>remote setup</c> prints and returns. A fake service stands in for Hetzner, so the test pins the CLI's part: how the
/// confirmation prompt is asked, how an answer or its absence reads, and that the service's lines and exit code pass through.
/// </summary>
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

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        setup.Answer.Should().BeTrue();
        stdout.ReplaceLineEndings("\n").Should().Be("Upload this key? [y/N] Setup step finished.\n");
    }

    [Fact]
    public async Task NoInputEndsThePromptLineAndDeclines()
    {
        var setup = new FakeSetup();

        var (code, stdout, stderr) = await RunAsync(setup, null);

        code.Should().Be((int)ExitCode.GeneralFailure);
        stderr.Should().BeEmpty();
        setup.Answer.Should().BeFalse();
        // The prompt has no newline, so the console adds one when input ends.
        stdout.ReplaceLineEndings("\n").Should().Be("Upload this key? [y/N] \nSetup step finished.\n");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(FakeSetup setup, string? input)
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, $"database:\n  path: '{Path.Combine(_directory, "jobs.db")}'\n");
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
            return Answer.Value ? ExitCode.Success : ExitCode.GeneralFailure;
        }
    }
}
