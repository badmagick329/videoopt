using FluentAssertions;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class FfmpegDecodeTesterTests
{
    [Theory]
    [InlineData(0, "[av1 @ 0x1] Error parsing OBU data\r\nmore detail", "[av1 @ 0x1] Error parsing OBU data")] // an error line fails even a zero exit
    [InlineData(1, "", "ffmpeg exited with 1.")] // a non-zero exit fails even with nothing on stderr
    public async Task DecodeAsyncFailsOnAnErrorLineOrANonZeroExit(int exitCode, string stderr, string reason)
    {
        var runner = new StaticProcessRunner(new ExternalProcessResult(exitCode, string.Empty, stderr));

        var result = await new FfmpegDecodeTester("ffmpeg", runner).DecodeAsync("out.mkv", DecodeWindow.Whole);

        result.Should().Be(new DecodeTestResult(false, reason));
    }

    [Fact]
    public async Task DecodeAsyncPassesCleanDecode()
    {
        var runner = new StaticProcessRunner(new ExternalProcessResult(0, string.Empty, string.Empty));

        var result = await new FfmpegDecodeTester("ffmpeg", runner).DecodeAsync("out.mkv", new DecodeWindow(68.5946, 10));

        result.Passed.Should().BeTrue();
        runner.Request!.Arguments.Should().Equal(
            "-hide_banner", "-nostdin", "-v", "error", "-xerror",
            "-ss", "68.595", "-t", "10",
            "-i", "out.mkv", "-map", "0:v", "-map", "0:a?", "-enc_time_base:v", "demux", "-f", "null", "-");
    }

    [Fact]
    public void BuildArgumentsDecodesToTheEndWhenWindowHasNoDuration()
    {
        FfmpegDecodeTester.BuildArguments("out.mkv", new DecodeWindow(137.189, null))
            .Should().ContainInOrder("-ss", "137.189", "-i").And.NotContain("-t");
    }

    private sealed class StaticProcessRunner(ExternalProcessResult result) : IExternalProcessRunner
    {
        public ExternalProcessRequest? Request { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }
}
