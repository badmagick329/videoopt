using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Features.Validate;

public sealed class OutputValidationServiceTests : IDisposable
{
    private static readonly MediaTimeline Timeline = new(0, 149.069, 2.251);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public OutputValidationServiceTests() => Directory.CreateDirectory(_directory);

    public enum Check
    {
        None,
        /// <summary>Both streams shifted by the same amount: no difference.</summary>
        BothStreamsShifted,
        /// <summary>No saving at all passes when neither setting asks for one.</summary>
        SameSizeAllowed,
        Missing,
        Empty,
        Unreadable,
        NotAv1,
        NotSmaller,
        LargerThanMinimum,
        SavesTooLittle,
        Shorter,
        AudioStreams,
        SubtitleStreams,
        AudioTiming,
        AudioStartUnreadable,
        TimelineUnreadable,
        DecodeFails,
        DecodeCannotRun
    }

    /// <summary>One row per check (VOPT-117): only a file with nothing to review fails; saving too little is a skip; every other doubt warns.</summary>
    [Theory]
    [InlineData(Check.None, ValidationVerdict.Passed, null)]
    [InlineData(Check.BothStreamsShifted, ValidationVerdict.Passed, null)]
    [InlineData(Check.SameSizeAllowed, ValidationVerdict.Passed, null)]
    [InlineData(Check.Missing, ValidationVerdict.Failed, "Original or temporary output is missing.")]
    [InlineData(Check.Empty, ValidationVerdict.Failed, "Output is empty.")]
    [InlineData(Check.Unreadable, ValidationVerdict.Failed, "Output 'output.mkv' could not be read by ffprobe: moov atom not found")]
    [InlineData(Check.NotAv1, ValidationVerdict.Failed, "Output video is not AV1.")]
    [InlineData(Check.NotSmaller, ValidationVerdict.NotWorthConverting, "the AV1 is 100 B, no smaller than the original's 100 B (savings.requireSmallerOutput).")]
    [InlineData(Check.LargerThanMinimum, ValidationVerdict.NotWorthConverting, "the AV1 is 120 B, no smaller than the original's 100 B, so it can't save 1B (savings.minimumBytesSaved).")]
    [InlineData(Check.SavesTooLittle, ValidationVerdict.NotWorthConverting, "the AV1 saves only 50 B, less than 51B (savings.minimumBytesSaved).")]
    [InlineData(Check.Shorter, ValidationVerdict.Warned, "The AV1's video is 4.069 s shorter than the original's.")]
    [InlineData(Check.AudioStreams, ValidationVerdict.Warned, "The AV1 has 0 audio streams; the original has 1.")]
    [InlineData(Check.SubtitleStreams, ValidationVerdict.Warned, "The AV1 has 1 subtitle stream; the original has 0.")]
    [InlineData(Check.AudioTiming, ValidationVerdict.Warned, "The AV1's audio starts 0.749 s later relative to the video than in the original.")]
    [InlineData(Check.AudioStartUnreadable, ValidationVerdict.Warned, "The audio's start time could not be read, so audio sync was not checked.")]
    [InlineData(Check.TimelineUnreadable, ValidationVerdict.Warned, "The length and audio sync could not be checked: Malformed packet timing.")]
    [InlineData(Check.DecodeFails, ValidationVerdict.Warned, "The AV1 failed the decode test: Error submitting packet to decoder: Invalid data found")]
    [InlineData(Check.DecodeCannotRun, ValidationVerdict.Warned, "The decode test could not run: ffmpeg is gone")]
    public async Task EachCheckGivesItsVerdict(Check check, ValidationVerdict expected, string? message)
    {
        var scenario = new Scenario();
        switch (check)
        {
            case Check.BothStreamsShifted: scenario.Source = new MediaTimeline(1.881, 149.069, 4.132); scenario.Output = new MediaTimeline(0, 147.188, 2.251); break;
            case Check.SameSizeAllowed: scenario.OutputBytes = 100; scenario.RequireSmallerOutput = false; break;
            case Check.Missing: scenario.DeleteOutput = true; break;
            case Check.Empty: scenario.OutputBytes = 0; break;
            case Check.Unreadable: scenario.OutputProbeError = new MediaProbeException("moov atom not found"); break;
            case Check.NotAv1: scenario.OutputCodec = "hevc"; break;
            case Check.NotSmaller: scenario.OutputBytes = 100; break;
            case Check.LargerThanMinimum: scenario.OutputBytes = 120; scenario.RequireSmallerOutput = false; scenario.MinimumBytesSaved = "1B"; break;
            case Check.SavesTooLittle: scenario.MinimumBytesSaved = "51B"; break;
            case Check.Shorter: scenario.Output = new MediaTimeline(0, 145, 2.251); break;
            case Check.AudioStreams: scenario.OutputAudioStreams = 0; break;
            case Check.SubtitleStreams: scenario.OutputSubtitleStreams = 1; break;
            case Check.AudioTiming: scenario.Source = new MediaTimeline(1.881, 149.069, 4.132); scenario.Output = new MediaTimeline(0, 147.188, 3); break;
            case Check.AudioStartUnreadable: scenario.Output = new MediaTimeline(0, 149.069, null); break;
            case Check.TimelineUnreadable: scenario.Output = null; break;
            case Check.DecodeFails: scenario.Decode = new DecodeTestResult(false, "Error submitting packet to decoder: Invalid data found"); break;
            case Check.DecodeCannotRun: scenario.DecodeError = new IOException("ffmpeg is gone"); break;
        }

        var report = await ValidateAsync(scenario);

        report.Verdict.Should().Be(expected);
        report.Passed.Should().Be(expected is ValidationVerdict.Passed or ValidationVerdict.Warned);
        report.Failures.Should().Equal(expected == ValidationVerdict.Failed ? [message!] : []);
        report.NotWorthConverting.Should().Be(expected == ValidationVerdict.NotWorthConverting ? message : null);
        report.Warnings.Should().Equal(expected == ValidationVerdict.Warned ? [message!] : []);
    }

    [Fact]
    public async Task AHardFailureBeatsWarningsAndSkipsTheDecodeTest()
    {
        var scenario = new Scenario { OutputCodec = "hevc", Output = new MediaTimeline(0, 145, 2.251) };

        var report = await ValidateAsync(scenario);

        report.Verdict.Should().Be(ValidationVerdict.Failed);
        report.Warnings.Should().BeEmpty();
        scenario.DecodeWindows.Should().BeEmpty();
    }

    [Fact]
    public async Task NotWorthConvertingBeatsWarningsAndSkipsTheDecodeTest()
    {
        var scenario = new Scenario { MinimumBytesSaved = "51B", Output = new MediaTimeline(0, 145, 2.251) };

        var report = await ValidateAsync(scenario);

        report.Verdict.Should().Be(ValidationVerdict.NotWorthConverting);
        report.Warnings.Should().BeEmpty();
        scenario.DecodeWindows.Should().BeEmpty();
    }

    [Fact]
    public async Task AWarnedAv1IsStillDecodedSoADecodeProblemIsListedToo()
    {
        var scenario = new Scenario { Output = new MediaTimeline(0, 145, 2.251), Decode = new DecodeTestResult(false, "corrupt frame") };

        var report = await ValidateAsync(scenario);

        report.Warnings.Should().Equal("The AV1's video is 4.069 s shorter than the original's.", "The AV1 failed the decode test: corrupt frame");
        scenario.DecodeWindows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValidateAsyncDecodesStartMiddleAndTailOfLongOutput()
    {
        var scenario = new Scenario();

        var report = await ValidateAsync(scenario);

        report.Verdict.Should().Be(ValidationVerdict.Passed);
        scenario.DecodeWindows.Should().Equal(
            new DecodeWindow(0, 10),
            new DecodeWindow((147.189 - 10) / 2, 10),
            new DecodeWindow(147.189 - 10, null));
    }

    /// <summary>A record written before VOPT-117 has no Warnings and no NotWorthConverting; it must read as it was judged then.</summary>
    [Theory]
    [InlineData("""{"Passed":true,"Failures":[]}""", ValidationVerdict.Passed)]
    [InlineData("""{"Passed":false,"Failures":["Output duration differs from the original."]}""", ValidationVerdict.Failed)]
    public async Task ARecordFromBeforeWarningsReadsAsItWasJudged(string validation, ValidationVerdict expected)
    {
        var output = Path.Combine(_directory, "old.mkv");
        await File.WriteAllTextAsync(output + ".manifest.json", $$"""{"SourcePath":"a.mkv","OutputPath":"{{output.Replace("\\", "\\\\")}}","Validation":{{validation}}}""");

        var manifest = await new OutputManifestStore().LoadAsync(output);

        manifest.Validation!.Warnings.Should().BeEmpty();
        manifest.Validation.Verdict.Should().Be(expected);
    }

    [Theory]
    [InlineData("none", 600d, 0)]
    [InlineData("full", 600d, 1)]
    [InlineData("sampled", 30d, 1)]
    [InlineData("sampled", null, 1)]
    public void SelectDecodeWindowsDecodesWholeOutputUnlessSamplingApplies(string mode, double? duration, int expectedWindows)
    {
        var windows = OutputValidationService.SelectDecodeWindows(mode, duration);

        windows.Should().Equal(Enumerable.Repeat(DecodeWindow.Whole, expectedWindows));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class Scenario
    {
        public MediaTimeline Source { get; set; } = Timeline;
        public MediaTimeline? Output { get; set; } = Timeline;
        public int OutputBytes { get; set; } = 50;
        public bool DeleteOutput { get; set; }
        public string MinimumBytesSaved { get; set; } = "0B";
        public bool RequireSmallerOutput { get; set; } = true;
        public Exception? OutputProbeError { get; set; }
        public string OutputCodec { get; set; } = "av1";
        public int OutputAudioStreams { get; set; } = 1;
        public int OutputSubtitleStreams { get; set; }
        public DecodeTestResult Decode { get; set; } = new(true, null);
        public Exception? DecodeError { get; set; }
        public List<DecodeWindow> DecodeWindows { get; } = [];
    }

    private async Task<ValidationReport> ValidateAsync(Scenario scenario)
    {
        var source = Path.Combine(_directory, "source.mkv");
        var output = Path.Combine(_directory, "output.mkv");
        await File.WriteAllBytesAsync(source, new byte[100]);
        await File.WriteAllBytesAsync(output, new byte[scenario.OutputBytes]);
        if (scenario.DeleteOutput) File.Delete(output);
        var manifest = new OutputManifest { SourcePath = source, OutputPath = output };
        var service = new OutputValidationService(
            _ => new StaticMediaProbe(output, scenario),
            _ => new StaticTimelineProbe(source, scenario),
            _ => new RecordingDecodeTester(scenario));
        return await service.ValidateAsync(manifest, new AppSettings
        {
            Savings = new SavingsSettings { RequireSmallerOutput = scenario.RequireSmallerOutput, MinimumBytesSaved = scenario.MinimumBytesSaved },
            Validation = new ValidationSettings { DurationToleranceSeconds = 0.1 }
        });
    }

    private sealed class RecordingDecodeTester(Scenario scenario) : IMediaDecodeTester
    {
        public Task<DecodeTestResult> DecodeAsync(string path, DecodeWindow window, CancellationToken cancellationToken = default)
        {
            scenario.DecodeWindows.Add(window);
            return scenario.DecodeError is { } error ? Task.FromException<DecodeTestResult>(error) : Task.FromResult(scenario.Decode);
        }
    }

    private sealed class StaticMediaProbe(string outputPath, Scenario scenario) : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            var isOutput = path == outputPath;
            if (isOutput && scenario.OutputProbeError is { } error) return Task.FromException<MediaInfo>(error);
            return Task.FromResult(new MediaInfo(
                isOutput ? scenario.OutputCodec : "h264",
                1,
                isOutput ? scenario.OutputAudioStreams : 1,
                isOutput ? scenario.OutputSubtitleStreams : 0,
                0,
                isOutput ? 147.189 : 149.069,
                null,
                1920,
                1080,
                10_000_000));
        }
    }

    private sealed class StaticTimelineProbe(string sourcePath, Scenario scenario) : IMediaTimelineProbe
    {
        public Task<MediaTimeline> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            path == sourcePath
                ? Task.FromResult(scenario.Source)
                : scenario.Output is { } timeline
                    ? Task.FromResult(timeline)
                    : throw new MediaProbeException("Malformed packet timing.");
    }
}
