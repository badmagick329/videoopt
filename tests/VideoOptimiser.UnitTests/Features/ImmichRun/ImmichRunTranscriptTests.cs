using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.UnitTests.Features.Queue;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.ImmichRun;

/// <summary>
/// <c>immich run</c> and <c>queue add immich</c> through the real CLI and the real services (VOPT-54 step 7), with Immich in
/// memory, a probe that reads the codec from the file and a processor that converts without the media tools. One whole
/// transcript per run state: the limit counts new videos only, a rerun leaves the open ones alone, and <c>immich run</c>
/// converts the whole queue as <c>run</c> does.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class ImmichRunTranscriptTests : QueueTestBase
{
    // Older than the 90-day age gate whenever the suite runs.
    private static readonly DateTimeOffset Taken = new(2024, 3, 4, 12, 0, 0, TimeSpan.Zero);
    private const string Intro = "Each video below is downloaded and checked. If it doesn't fit your rules, the download is deleted and the video is not checked again until your rules change.";

    private readonly InMemoryImmich _immich = new(Taken);
    private readonly ScriptedRunProcessor _processor = new() { ReportsEncoding = false };

    protected override string[] DefaultYaml =>
    [
        "eligibility:",
        "  rules:",
        "    - codecs: ['hevc']",
        "      resolution: '1080p-1440p'",
        "      minimumVideoBitrate: '10Mbps'",
        "      minimumFileSize: '5B'",
        "savings:",
        "  minimumBytesSaved: '0B'"
    ];

    protected override void RegisterFakes(IServiceCollection services)
    {
        CliFakes.AcceptAnySettings(services);
        services.AddSingleton<IImmichClientFactory>(_immich);
        services.AddSingleton<Func<string, IMediaProbe>>(_ => new ContentProbe());
        services.AddSingleton<IJobProcessor>(_processor);
    }

    [Fact]
    public async Task AFreshRunWithALimitDownloadsAndQueuesThatManyNewVideosThenConvertsThem()
    {
        _immich.Add("old.avi", "hevc avi");
        _immich.Add("a.mp4", "h264 one");
        _immich.Add("b.mp4", "hevc two");
        _immich.Add("c.mp4", "hevc three");
        _immich.Add("d.mp4", "hevc four");

        var (code, output, error) = await RunWithImmichAsync("immich", "run", "--limit", "2");

        (code, output, error).Should().Be((0, Transcript(
            "Encoding on this PC.",
            "Looking in Immich for up to 2 new videos to convert.",
            Intro,
            "",
            "  1. a.mp4  2024-03-04  h264 1080p 20 Mbps  8 B  doesn't fit: no rule for h264",
            "  2. b.mp4  2024-03-04  hevc 1080p 20 Mbps  8 B  queued (1 of 2)",
            "  3. c.mp4  2024-03-04  hevc 1080p 20 Mbps  10 B  queued (2 of 2)",
            "",
            "Downloaded and checked 3 videos: 2 queued, 1 doesn't fit your rules (no rule for h264).",
            "Not downloaded: 1 too small or not a file type your rules list.",
            "Queued: 2.",
            "Converting 2 videos.",
            "",
            "[1/2] b.mp4 (8 B)",
            "      Ready to finalise.",
            "",
            "[2/2] c.mp4 (10 B)",
            "      Ready to finalise.",
            "Ready to finalise: 2.",
            $"2 videos are ready to finalise: review them, then run 'video-optimiser finalize --ready{ConfigArgument}'."), ""));
    }

    /// <summary>Open videos do not count towards the limit: this run adds one more however many are waiting, held or ready.</summary>
    [Fact]
    public async Task ARerunLeavesOpenVideosAloneAddsAsManyNewOnesAsTheLimitAndConvertsTheWaitingOnes()
    {
        await WriteConfigAsync(immichConfigured: true);
        await ImmichJobAsync("open.mp4", "hevc open");
        await HeldAsync(await ImmichJobAsync("held.mp4", "hevc held"));
        await MoveAsync(await ImmichJobAsync("ready.mp4", "hevc ready"), JobStatus.Validating, JobStatus.ReadyToFinalize);
        _immich.Add("new1.mp4", "hevc new1");
        _immich.Add("new2.mp4", "hevc new2");

        var (code, output, _) = await RunAsync("immich", "run", "--limit", "1");

        (code, output).Should().Be((0, Transcript(
            "Encoding on this PC.",
            "Looking in Immich for up to 1 new video to convert.",
            Intro,
            "",
            "  1. new1.mp4  2024-03-04  hevc 1080p 20 Mbps  9 B  queued (1 of 1)",
            "",
            "Downloaded and checked 1 video: 1 queued.",
            "Queued: 1. Already queued: 1. Held: 1. Ready to finalise: 1.",
            "1 video is held; runs pass over it until it's released.",
            "Converting 2 videos.",
            "",
            "[1/2] open.mp4 (9 B)",
            "      Ready to finalise.",
            "",
            "[2/2] new1.mp4 (9 B)",
            "      Ready to finalise.",
            "Ready to finalise: 2.",
            $"3 videos are ready to finalise: review them, then run 'video-optimiser finalize --ready{ConfigArgument}'.")));
        _processor.Processed.Should().Equal("open.mp4", "new1.mp4");
    }

    [Fact]
    public async Task WithoutALimitItLooksForEveryVideoThatFitsAndChecksAgainWhatEarlierRulesRejected()
    {
        await WriteConfigAsync(immichConfigured: true);
        var stale = _immich.Add("a.mp4", "h264 old");
        _immich.Add("b.mp4", "hevc new");
        await Repository.PutImmichSkipAsync(DatabasePath, new ImmichSkipRecord(stale.Id, stale.Checksum, "an-older-rules-fingerprint", "codec h264", Taken));

        var (code, output, _) = await RunAsync("immich", "run");

        (code, output).Should().Be((0, Transcript(
            "Encoding on this PC.",
            "Looking in Immich for every video that fits your rules.",
            "1 video that didn't fit your earlier rules is checked again under the current ones when the search reaches it.",
            Intro,
            "",
            "  1. a.mp4  2024-03-04  h264 1080p 20 Mbps  8 B  doesn't fit: no rule for h264",
            "  2. b.mp4  2024-03-04  hevc 1080p 20 Mbps  8 B  queued",
            "",
            "Downloaded and checked 2 videos: 1 queued, 1 doesn't fit your rules (no rule for h264).",
            "Queued: 1.",
            "Converting 1 video.",
            "",
            "[1/1] b.mp4 (8 B)",
            "      Ready to finalise.",
            "Ready to finalise: 1.",
            $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready{ConfigArgument}'.")));
    }

    [Fact]
    public async Task ASearchThatRunsOutBeforeTheLimitSaysSoAndConvertsNothing()
    {
        _immich.Add("a.mp4", "h264 one");

        var (code, output, _) = await RunWithImmichAsync("immich", "run", "--limit", "3");

        (code, output).Should().Be((0, Transcript(
            "Encoding on this PC.",
            "Looking in Immich for up to 3 new videos to convert.",
            Intro,
            "",
            "  1. a.mp4  2024-03-04  h264 1080p 20 Mbps  8 B  doesn't fit: no rule for h264",
            "",
            "Downloaded and checked 1 video: 1 doesn't fit your rules (no rule for h264).",
            "Immich has no more videos that fit your rules.",
            "Queued: 0.",
            "Nothing to finalise.")));
        _processor.Calls.Should().Be(0);
    }

    /// <summary>With nothing to download the explanation of downloads stays out and no blank line separates the lines.</summary>
    [Fact]
    public async Task ASearchThatDownloadsNothingPrintsNoExplanation()
    {
        var (code, output, _) = await RunWithImmichAsync("immich", "run", "--limit", "3");

        (code, output).Should().Be((0, Transcript(
            "Encoding on this PC.",
            "Looking in Immich for up to 3 new videos to convert.",
            "Immich has no more videos that fit your rules.",
            "Queued: 0.",
            "Nothing to finalise.")));
    }

    /// <summary><c>immich run</c> is <c>queue add immich</c> plus <c>run</c>: it converts a waiting folder video too, in run order.</summary>
    [Fact]
    public async Task ImmichRunConvertsAWaitingFolderVideoToo()
    {
        await WriteConfigAsync(immichConfigured: true);
        await QueuedAsync("folder.mp4");
        _immich.Add("a.mp4", "hevc one");

        var (code, output, _) = await RunAsync("immich", "run", "--first");

        (code, output).Should().Be((0, Transcript(
            "Encoding on this PC.",
            "Looking in Immich for up to 1 new video to convert.",
            Intro,
            "",
            "  1. a.mp4  2024-03-04  hevc 1080p 20 Mbps  8 B  queued (1 of 1)",
            "",
            "Downloaded and checked 1 video: 1 queued.",
            "Queued: 1.",
            "Converting 2 videos.",
            "",
            "[1/2] folder.mp4 (20 B)",
            "      Ready to finalise.",
            "",
            "[2/2] a.mp4 (8 B)",
            "      Ready to finalise.",
            "Ready to finalise: 2.",
            $"2 videos are ready to finalise: review them, then run 'video-optimiser finalize --ready{ConfigArgument}'.")));
        _processor.RunLockHolders.Should().AllBe("video-optimiser immich run");
    }

    [Fact]
    public async Task QueueAddImmichQueuesNewVideosAndEndsWithTheCountsAndWhatToRunWithoutConverting()
    {
        await WriteConfigAsync(immichConfigured: true);
        await MoveAsync(await ImmichJobAsync("ready.mp4", "hevc ready"), JobStatus.Validating, JobStatus.ReadyToFinalize);
        await ImmichJobAsync("waiting.mp4", "hevc waiting");
        _immich.Add("a.mp4", "hevc one");
        _immich.Add("b.mp4", "hevc two");
        _immich.Add("c.mp4", "hevc three");

        var (code, output, error) = await RunAsync("queue", "add", "immich", "--limit", "2");

        (code, output, error).Should().Be((0, Transcript(
            "Looking in Immich for up to 2 new videos to convert.",
            Intro,
            "",
            "  1. a.mp4  2024-03-04  hevc 1080p 20 Mbps  8 B  queued (1 of 2)",
            "  2. b.mp4  2024-03-04  hevc 1080p 20 Mbps  8 B  queued (2 of 2)",
            "",
            "Downloaded and checked 2 videos: 2 queued.",
            "Queued: 2. Already queued: 1. Ready to finalise: 1.",
            $"Next: run 'video-optimiser run{ConfigArgument}'.",
            $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready{ConfigArgument}'."), ""));
        _processor.Calls.Should().Be(0);
        (await Repository.ListAsync(DatabasePath, terminal: false)).Should().HaveCount(4);
    }

    [Fact]
    public async Task QueueAddImmichNamesWhatItLeftAloneAndExitsWithIssues()
    {
        await WriteConfigAsync(immichConfigured: true);
        var picture = _immich.Add("pic.mp4", "hevc picture");
        _immich.Replace(picture with { Type = "IMAGE" });
        var unsafeName = _immich.Add("unsafe.mp4", "hevc unsafe");
        _immich.Replace(unsafeName with { OriginalFileName = "nested/clip.mp4" });
        _immich.Add("corrupt.mp4", "corrupt bytes");
        _immich.Add("good.mp4", "hevc good");

        var (code, output, error) = await RunAsync("queue", "add", "immich");

        code.Should().Be((int)VideoOptimiser.Domain.ExitCode.PartialSuccess);
        output.Should().Be(Transcript(
            "Looking in Immich for every video that fits your rules.",
            "Issue: nested/clip.mp4: the name Immich gives it can't be saved safely on this PC; left alone.",
            Intro,
            "",
            "  1. corrupt.mp4  2024-03-04  issue: ffprobe exited with 1: Invalid data found when processing input; it will be tried again next run",
            "  2. good.mp4  2024-03-04  hevc 1080p 20 Mbps  9 B  queued",
            "",
            "Downloaded and checked 1 video: 1 queued.",
            "Also: 2 issues (above).",
            "Queued: 1.",
            $"Next: run 'video-optimiser run{ConfigArgument}'."));
        error.Should().Be(Transcript(
            "Left alone: pic.mp4 (Immich's search should not have returned it: it is a IMAGE, not a video).",
            "Immich's search returned 1 video it should have left out. They were left alone, but this Immich version's search may behave differently: check before running again."));
    }

    [Fact]
    public async Task QueueAddImmichNeedsImmichSetUp()
    {
        await WriteConfigAsync();

        var (code, output, error) = await RunAsync("queue", "add", "immich");

        (code, output, error).Should().Be((3, "", Transcript($"Set immich.baseUrl in the configuration first; 'video-optimiser doctor{ConfigArgument}' then checks the connection and the API key.")));
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunWithImmichAsync(params string[] args)
    {
        await WriteConfigAsync(immichConfigured: true);
        return await RunAsync(args);
    }

    /// <summary>An open Immich job for a video that is in Immich and has its downloaded copy; the pause keeps run order the order of the calls.</summary>
    private async Task<JobRecord> ImmichJobAsync(string name, string content)
    {
        var asset = _immich.Add(name, content);
        var path = Path.Combine(StagingDirectory, asset.Id, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        var job = await Repository.CreateAsync(DatabasePath, new JobRecord
        {
            Id = Guid.NewGuid(), SourcePath = path, SourceFingerprint = "staged", Status = JobStatus.Queued,
            SourceKind = JobSourceKind.Immich, ImmichAssetId = asset.Id, ImmichChecksum = asset.Checksum
        });
        await Task.Delay(5);
        return job;
    }
}
