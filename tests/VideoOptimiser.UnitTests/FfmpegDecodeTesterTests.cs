using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class FfmpegDecodeTesterTests
{
    [Fact]
    public async Task DecodeAsyncFailsWhenFfmpegReportsAnErrorButExitsZero()
    {
        var runner = new StaticProcessRunner(new ExternalProcessResult(0, string.Empty, "[av1 @ 0x1] Error parsing OBU data\r\nmore detail"));

        var result = await new FfmpegDecodeTester("ffmpeg", runner).DecodeAsync("out.mkv", DecodeWindow.Whole);

        result.Should().Be(new DecodeTestResult(false, "[av1 @ 0x1] Error parsing OBU data"));
    }

    [Fact]
    public async Task DecodeAsyncFailsWhenFfmpegExitsNonZeroSilently()
    {
        var runner = new StaticProcessRunner(new ExternalProcessResult(1, string.Empty, string.Empty));

        var result = await new FfmpegDecodeTester("ffmpeg", runner).DecodeAsync("out.mkv", DecodeWindow.Whole);

        result.Should().Be(new DecodeTestResult(false, "ffmpeg exited with 1."));
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
            "-i", "out.mkv", "-map", "0:v", "-map", "0:a?", "-f", "null", "-");
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
