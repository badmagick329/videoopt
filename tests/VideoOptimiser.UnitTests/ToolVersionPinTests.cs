using System.Text.RegularExpressions;
using FluentAssertions;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// CI runs the end-to-end test with the tools <c>install-media-tools.ps1</c> pins, while paid jobs run on a worker the
/// bootstrap script builds. The pins must agree, or CI tests ab-av1 code the worker never runs. The worker's own downloads
/// are pinned too (VOPT-152): each version in the bootstrap has a commit or SHA-256 beside it, which the script checks.
/// </summary>
public sealed partial class ToolVersionPinTests
{
    [Fact]
    public void TheWorkerBootstrapAndTheCiToolsPinTheSameAbAv1()
    {
        var bootstrap = ReadScript("bootstrap-remote-worker.sh");
        var mediaTools = ReadScript("install-media-tools.ps1");

        var workerVersion = BootstrapAbAv1Version().Match(bootstrap);
        var ciVersion = MediaToolsAbAv1Version().Match(mediaTools);

        workerVersion.Success.Should().BeTrue("the bootstrap declares AB_AV1_VERSION");
        ciVersion.Success.Should().BeTrue("install-media-tools.ps1 names its ab-av1 tool ab-av1-<version>");
        workerVersion.Groups[1].Value.Should().Be(ciVersion.Groups[1].Value);
        mediaTools.Should().Contain($"/releases/download/v{ciVersion.Groups[1].Value}/", "the download URL matches the tool name");
        Version.Parse(workerVersion.Groups[1].Value).Should().Be(AbAv1VersionPolicy.Minimum,
            "doctor rejects an ab-av1 older than the pinned version");
    }

    /// <summary>
    /// A version with no pin on the next line, or a pin the script never checks, would build whatever upstream serves. A
    /// download is checked on the line straight after it, before anything unpacks it.
    /// </summary>
    [Fact]
    public void EveryBootstrapVersionHasAPinBesideItThatTheScriptChecks()
    {
        var bootstrap = ReadScript("bootstrap-remote-worker.sh");

        var versions = BootstrapVersionAndPin().Matches(bootstrap);

        versions.Should().HaveCountGreaterThanOrEqualTo(4, "libvmaf, SVT-AV1, FFmpeg and ab-av1 each have a version");
        foreach (Match version in versions)
        {
            var name = version.Groups["name"].Value;
            version.Groups["kind"].Success.Should().BeTrue($"{name}_VERSION needs a {name}_COMMIT or {name}_SHA256 on the line after it");
            var pin = version.Groups["pin"].Value;
            if (version.Groups["kind"].Value == "COMMIT")
            {
                pin.Should().MatchRegex("^[0-9a-f]{40}$", $"{name}_COMMIT is a full commit id");
                bootstrap.Should().MatchRegex($$"""(?m)^\s*fetch_commit \S+ "\$\{{{name}}_COMMIT\}" \S""",$"the script fetches {name} by its pinned commit");
            }
            else
            {
                pin.Should().MatchRegex("^[0-9a-f]{64}$", $"{name}_SHA256 is a SHA-256 in lowercase hex");
                bootstrap.Should().MatchRegex($$"""--output "(?<file>[^"]+)"\n\s*verify_sha256 "\k<file>" "\$\{{{name}}_SHA256\}"\n""",
                    $"the script checks the {name} download against its pin before using it");
            }
        }
    }

    /// <summary>
    /// The pins as they were taken for each version. A version bumped without its pin, or a pin edited without its version,
    /// fails here. To bump: take the new pin (the tag's commit from <c>git ls-remote --tags</c>, using the peeled <c>^{}</c>
    /// commit for an annotated tag, or <c>sha256sum</c> of the file at the script's URL), set it beside the version in the
    /// script, and update the row.
    /// </summary>
    [Theory]
    [InlineData("VMAF", "3.0.0", "17a67b238ce0539bdeafdc95961abac64fa16ea8")]
    [InlineData("SVT_AV1", "3.1.0", "cc569c1eb16832b7f1a649cac0b3bd1a8e4a814d")]
    [InlineData("FFMPEG", "8.0", "b2751fccb6cc4c77708113cd78b561059b6fa904b24162fa0be2d60273d27b8e")]
    [InlineData("AB_AV1", "0.11.7", "40c035df608162fcb1c991d1474bc9bc8817e0354ac7aef5be5314d3efd64069")]
    public void EachBootstrapPinIsTheOneTakenForItsVersion(string name, string version, string pin)
    {
        var bootstrap = ReadScript("bootstrap-remote-worker.sh");

        var declared = BootstrapVersionAndPin().Matches(bootstrap).Single(match => match.Groups["name"].Value == name);

        (declared.Groups["version"].Value, declared.Groups["pin"].Value).Should().Be((version, pin),
            "a version and its pin change together; see this test's summary for how to take a new pin");
    }

    // The test project's working copy may have CRLF; the patterns expect LF, as the worker gets.
    private static string ReadScript(string name) => File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", name)).ReplaceLineEndings("\n");

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

    [GeneratedRegex("""^readonly (?<name>[A-Z0-9_]+)_VERSION="(?<version>[^"]+)"\n(?:readonly \k<name>_(?<kind>COMMIT|SHA256)="(?<pin>[^"]*)"$)?""", RegexOptions.Multiline)]
    private static partial Regex BootstrapVersionAndPin();
}
