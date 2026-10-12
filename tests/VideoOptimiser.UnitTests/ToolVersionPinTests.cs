using System.Text.RegularExpressions;
using FluentAssertions;
using VideoOptimiser.Infrastructure.Diagnostics;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// CI runs the end-to-end test with the tools <c>install-media-tools.ps1</c> pins, while paid jobs run on a worker the
/// bootstrap script builds. The pins must agree, or CI tests ab-av1 code the worker never runs.
/// </summary>
public sealed partial class ToolVersionPinTests
{
    [Fact]
    public void TheWorkerBootstrapAndTheCiToolsPinTheSameAbAv1()
    {
        var bootstrap = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "bootstrap-remote-worker.sh"));
        var mediaTools = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "install-media-tools.ps1"));

        var workerVersion = BootstrapAbAv1Version().Match(bootstrap);
        var ciVersion = MediaToolsAbAv1Version().Match(mediaTools);

        workerVersion.Success.Should().BeTrue("the bootstrap declares AB_AV1_VERSION");
        ciVersion.Success.Should().BeTrue("install-media-tools.ps1 names its ab-av1 tool ab-av1-<version>");
        workerVersion.Groups[1].Value.Should().Be(ciVersion.Groups[1].Value);
        mediaTools.Should().Contain($"/releases/download/v{ciVersion.Groups[1].Value}/", "the download URL matches the tool name");
        Version.Parse(workerVersion.Groups[1].Value).Should().Be(ProcessRemoteEnvironmentVerifier.MinimumAbAv1Version,
            "doctor rejects a remote worker older than the pinned version");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VideoOptimiser.sln"))) return directory.FullName;
        }

        throw new InvalidOperationException("The repository root was not found above the test output directory.");
    }

    [GeneratedRegex("""^readonly AB_AV1_VERSION="([0-9.]+)"$""", RegexOptions.Multiline)]
    private static partial Regex BootstrapAbAv1Version();

    [GeneratedRegex("""Name = 'ab-av1-([0-9.]+)'""")]
    private static partial Regex MediaToolsAbAv1Version();
}
