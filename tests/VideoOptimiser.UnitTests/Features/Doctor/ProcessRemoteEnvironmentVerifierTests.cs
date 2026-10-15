using System.Text.RegularExpressions;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Features.Doctor;

namespace VideoOptimiser.UnitTests.Features.Doctor;

public sealed class ProcessRemoteEnvironmentVerifierTests
{
    private static readonly RemoteSshSettings Requirements = new() { Host = "video-worker", MinimumCpuCount = 8, MinimumAvailableMemory = "16GiB" };

    [Fact]
    public void BuildArgumentsUsesBatchModeWithoutDisablingHostKeyVerification()
    {
        var settings = new RemoteSshSettings { Host = "video-worker" };

        var arguments = ProcessRemoteEnvironmentVerifier.BuildArguments(settings);

        arguments.Should().ContainInOrder("-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "--", "video-worker");
        arguments.Should().Contain(argument => argument.Contains("ab-av1 --version", StringComparison.Ordinal));
        arguments.Should().NotContain(argument => argument.Contains("StrictHostKeyChecking", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseProbeOutputReportsMissingFeaturesAndInsufficientResources()
    {
        var output = HealthyProbeOutput()
            .Replace("video-optimiser-doctor:ffmpeg_libvmaf=1", "video-optimiser-doctor:ffmpeg_libvmaf=0", StringComparison.Ordinal)
            .Replace("video-optimiser-doctor:cpu_count=16", "video-optimiser-doctor:cpu_count=4", StringComparison.Ordinal)
            .Replace("video-optimiser-doctor:available_memory_bytes=34359738368", "video-optimiser-doctor:available_memory_bytes=1073741824", StringComparison.Ordinal);

        var diagnostics = ProcessRemoteEnvironmentVerifier.ParseProbeOutput(output, Requirements, 16L * 1024 * 1024 * 1024);

        diagnostics.Should().Contain(diagnostic => diagnostic.Code == "RemoteFfmpegFeatureUnavailable" && diagnostic.Status == DiagnosticStatus.Fail && diagnostic.Message.Contains("libvmaf", StringComparison.Ordinal));
        diagnostics.Should().Contain(diagnostic => diagnostic.Code == "RemoteCpuInsufficient" && diagnostic.Status == DiagnosticStatus.Fail);
        diagnostics.Should().Contain(diagnostic => diagnostic.Code == "RemoteMemoryInsufficient" && diagnostic.Status == DiagnosticStatus.Fail);
    }

    [Fact]
    public void ParseProbeOutputReportsHealthyRemoteEnvironment()
    {
        var diagnostics = ProcessRemoteEnvironmentVerifier.ParseProbeOutput(HealthyProbeOutput(), Requirements, 16L * 1024 * 1024 * 1024);

        diagnostics.Should().OnlyContain(diagnostic => diagnostic.Status == DiagnosticStatus.Pass);
        diagnostics.Should().Contain(diagnostic => diagnostic.Code == "RemoteWorkspaceWritable");
        diagnostics.Should().Contain(diagnostic => diagnostic.Code == "RemoteDiskSpaceAvailable" && diagnostic.Message.Contains("2.5x", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("video-optimiser-doctor:ffmpeg_libdav1d=0", "RemoteAv1DecoderUnavailable", "Remote FFmpeg does not expose the required libdav1d software AV1 decoder; AV1 decoding of originals and CRF search cannot run remotely.")]
    [InlineData("video-optimiser-doctor:ab_av1_version=0.9.3", "RemoteAbAv1VersionInsufficient", "Remote ab-av1 version 0.9.3 is too old; minimum required version is 0.11.7.")]
    [InlineData("video-optimiser-doctor:ab_av1_version=", "RemoteAbAv1VersionUnavailable", "Remote ab-av1 version could not be determined; minimum required version is 0.11.7.")]
    public void ParseProbeOutputFailsWhenTheRemoteDecoderOrAbAv1VersionIsUnusable(string probeLine, string expectedCode, string expectedMessage)
    {
        var key = probeLine[..(probeLine.IndexOf('=', StringComparison.Ordinal) + 1)];
        var output = Regex.Replace(HealthyProbeOutput(), "^" + Regex.Escape(key) + ".*$", probeLine, RegexOptions.Multiline);

        var diagnostics = ProcessRemoteEnvironmentVerifier.ParseProbeOutput(output, Requirements, 16L * 1024 * 1024 * 1024);

        diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == expectedCode &&
            diagnostic.Status == DiagnosticStatus.Fail &&
            diagnostic.Message == expectedMessage);
    }

    private static string HealthyProbeOutput() => """
        video-optimiser-doctor:tool_bash=1
        video-optimiser-doctor:tool_nohup=1
        video-optimiser-doctor:tool_setsid=1
        video-optimiser-doctor:tool_kill=1
        video-optimiser-doctor:tool_sha256sum=1
        video-optimiser-doctor:tool_time=1
        video-optimiser-doctor:tool_ab_av1=1
        video-optimiser-doctor:ab_av1_version=0.11.7
        video-optimiser-doctor:tool_ffmpeg=1
        video-optimiser-doctor:tool_ffprobe=1
        video-optimiser-doctor:ffmpeg_libsvtav1=1
        video-optimiser-doctor:ffmpeg_libvmaf=1
        video-optimiser-doctor:ffmpeg_yuv420p10le=1
        video-optimiser-doctor:ffmpeg_libdav1d=1
        video-optimiser-doctor:cpu_count=16
        video-optimiser-doctor:available_memory_bytes=34359738368
        video-optimiser-doctor:workspace_writable=1
        video-optimiser-doctor:workspace_free_bytes=300000000000
        """;
}
