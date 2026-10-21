using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Cli.Features.Tui;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests.Features.Tui;

/// <summary>The TUI must keep the CLI's discovery protections, even when an addition runs beside a conversion or is stopped.</summary>
public sealed class TuiQueueAdditionEffectTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WatchFoldersQueueBesideALiveRunAndCountOpenJobsApart()
    {
        using var rig = Rig();
        var fresh = rig.File("watch/new.mp4", "h264 fresh");
        var held = await AddFileAsync(rig, rig.File("watch/held.mp4", "h264 held"));
        await rig.Repository.SetHeldAsync(rig.DatabasePath, held, held: true);
        var ready = await AddFileAsync(rig, rig.File("watch/ready.mp4", "h264 ready"));
        await rig.MoveAsync(ready, JobStatus.Encoding, JobStatus.Validating, JobStatus.ReadyToFinalize);
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(rig.DatabasePath), "video-optimiser run", out _);
        run.Should().NotBeNull();

        var done = await rig.Actions.AddVideosAsync(new(QueueAdditionSource.WatchFolders), rig.Settings, (_, _) => { }, default);

        done.Result.Notice.Should().Be("Queued: 1. Held: 1. Ready to finalise: 1.");
        done.Result.Lines.Should().Contain(line => line.Text == "The run going now picks them up.");
        (await rig.Repository.FindOpenBySourceAsync(rig.DatabasePath, Path.GetFullPath(fresh))).Should().NotBeNull();
        (await rig.Repository.ListAsync(rig.DatabasePath, terminal: false)).Should().HaveCount(3);

        var one = await rig.Actions.AddVideosAsync(new(QueueAdditionSource.File, [rig.File("watch/later.mp4", "h264 later")]), rig.Settings, (_, _) => { }, default);
        one.Result.Lines.Should().Contain(line => line.Text == "The run going now picks them up.");
        one.Result.Lines.Should().Contain(line => line.Text == "1 video is ahead of it; select it in Queue and press n to put it first.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyWatchRootAndEligibilityRefusalsOfferForce(bool outsideRoot)
    {
        using var rig = Rig();
        var path = rig.File(outsideRoot ? "outside.mp4" : "watch/hevc.mp4", outsideRoot ? "h264 video" : "hevc video");
        var asked = new AddVideos(QueueAdditionSource.File, [path]);

        var refusal = await rig.Actions.AddVideosAsync(asked, rig.Settings, (_, _) => { }, default);
        refusal.ForceCandidates.Should().Equal(Path.GetFullPath(path));
        (await rig.Repository.ListAsync(rig.DatabasePath, terminal: false)).Should().BeEmpty();

        var done = await rig.Actions.AddVideosAsync(asked with { Force = true }, rig.Settings, (_, _) => { }, default);
        done.ForceCandidates.Should().BeNullOrEmpty();
        var job = (await rig.Repository.FindOpenBySourceAsync(rig.DatabasePath, Path.GetFullPath(path)))!;
        job.Forced.Should().BeTrue();
        done.Result.Lines[0].Text.Should().Be($"Queued {Path.GetFileName(path)} as job {job.Id:N}, outside your rules (--force).");

        var again = await rig.Actions.AddVideosAsync(asked with { Force = true }, rig.Settings, (_, _) => { }, default);
        again.ForceCandidates.Should().BeNullOrEmpty();
        again.Result.Notice.Should().Contain($"already has job {job.Id:N}");
        (await rig.Repository.ListAsync(rig.DatabasePath, terminal: false)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("av1", "already AV1")]
    [InlineData("tool", "a file video-optimiser made")]
    [InlineData("archive", "an original video-optimiser archived")]
    [InlineData("small", "doesn't fit your rules")]
    [InlineData("missing", "Original does not exist")]
    public async Task ForceNeverBypassesSafetyRefusals(string protection, string wording)
    {
        using var rig = Rig();
        var relative = protection switch
        {
            "tool" => "watch/.video-optimiser/clip.mp4",
            "archive" => "watch/Archive/clip.mp4",
            _ => "watch/clip.mp4"
        };
        var path = Path.Combine(rig.Folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (protection != "missing") File.WriteAllText(path, protection == "av1" ? "av1 video" : "h264 video");
        // ContentProbe models a 150 MB video stream, so the saving must exceed that measured size.
        if (protection == "small") rig.Settings.Savings.MinimumBytesSaved = "1GiB";

        var result = await rig.Actions.AddVideosAsync(new(QueueAdditionSource.File, [path], Force: true), rig.Settings, (_, _) => { }, default);

        result.ForceCandidates.Should().BeNullOrEmpty();
        result.Result.Notice.Should().Contain(wording);
        (await rig.Repository.ListAsync(rig.DatabasePath, terminal: false)).Should().BeEmpty();
    }

    [Fact]
    public async Task ABatchLogsEachFileAndAsksOnceToForceOnlyTheRulesRefusals()
    {
        using var rig = Rig();
        var queued = rig.File("watch/clip-a.mp4", "h264 a");
        var ineligible = rig.File("watch/clip-b.mp4", "hevc video");
        var outside = rig.File("clip-c.mp4", "h264 video");
        var av1 = rig.File("watch/clip-d.mp4", "av1 video");
        var missing = Path.Combine(rig.Folder, "watch", "clip-e.mp4");
        var logged = new List<LogLine>();

        var done = await rig.Actions.AddVideosAsync(new(QueueAdditionSource.File, [queued, ineligible, outside, av1, missing]), rig.Settings, (kind, text) => logged.Add(new(kind, text)), default);

        logged.Select(line => line.Kind).Should().Equal(LogKind.Success, LogKind.Warning, LogKind.Warning, LogKind.Warning, LogKind.Error);
        logged[0].Text.Should().StartWith("Queued clip-a.mp4 as job ");
        logged[3].Text.Should().Be("clip-d.mp4 is already AV1, so it can't be queued.");
        logged[4].Text.Should().Be($"Original does not exist: {Path.GetFullPath(missing)}");
        done.Result.Notice.Should().Be("Queued: 1. Doesn't fit your rules: 2. Can't be queued: 2.");
        done.Result.Lines.Select(line => line.Text).Should().Equal(done.Result.Notice, "Next: press s to start a run.");
        done.ForceCandidates.Should().Equal(Path.GetFullPath(ineligible), Path.GetFullPath(outside));

        var question = TuiUpdate.Update(TuiTestData.Model(), done).Model.Confirm!;
        question.Question.Should().Be("Add 2 files despite the rules?");
        var forced = await rig.Actions.AddVideosAsync((AddVideos)question.OnYes, rig.Settings, (_, _) => { }, default);
        forced.Result.Notice.Should().Be("Queued: 2.");
        forced.ForceCandidates.Should().BeEmpty();
        var jobs = await rig.Repository.ListAsync(rig.DatabasePath, terminal: false);
        jobs.Select(job => job.SourcePath).Should().BeEquivalentTo([Path.GetFullPath(queued), Path.GetFullPath(ineligible), Path.GetFullPath(outside)]);
    }

    [Fact]
    public async Task CancellingBetweenFilesKeepsTheFilesAlreadyQueuedAndTheirLines()
    {
        using var rig = Rig();
        var first = rig.File("watch/clip-a.mp4", "h264 a");
        var second = rig.File("watch/clip-b.mp4", "h264 b");
        using var stop = new CancellationTokenSource();
        var logged = new List<string>();

        // Esc lands just as the first file's line is logged: the second must not start.
        var done = await rig.Actions.AddVideosAsync(new(QueueAdditionSource.File, [first, second]), rig.Settings, (_, text) =>
        {
            logged.Add(text);
            stop.Cancel();
        }, stop.Token);

        done.Result.Notice.Should().Be("Stopped adding videos. Videos already queued stay queued.");
        logged.Should().ContainSingle().Which.Should().StartWith("Queued clip-a.mp4 as job ");
        (await rig.Repository.ListAsync(rig.DatabasePath, terminal: false)).Should().ContainSingle().Which.SourcePath.Should().Be(Path.GetFullPath(first));
    }

    [Fact]
    public async Task ImmichLimitCountsNewJobsAndRunsTheReuploadGuardFirst()
    {
        var immich = new InMemoryImmich(DateTimeOffset.UtcNow.AddDays(-200)) { FiltersSearch = true };
        using var rig = Rig(immich);
        var reupload = immich.Add("converted.mp4", "h264 old converted");
        await CompletedImmichAsync(rig, reupload);
        var open = immich.Add("open.mp4", "h264 open");
        var queued = await rig.Repository.CreateAsync(rig.DatabasePath, new JobRecord
        {
            Id = Guid.NewGuid(), SourcePath = Path.Combine(rig.Settings.Immich.StagingDirectory, open.Id, open.OriginalFileName),
            SourceFingerprint = "open", SourceKind = JobSourceKind.Immich, ImmichAssetId = open.Id,
            ImmichChecksum = open.Checksum, Status = JobStatus.Queued
        });
        var first = immich.Add("first.mp4", "h264 first");
        immich.Add("second.mp4", "h264 second");
        var progress = new List<string>();

        var result = await rig.Actions.AddVideosAsync(new(QueueAdditionSource.Immich, Limit: 1), rig.Settings, (_, text) => progress.Add(text), default);

        result.Result.Notice.Should().Be("Queued: 1. Already queued: 1.");
        immich.Trashes.Should().ContainSingle().Which.Should().Equal(reupload.Id);
        immich.Downloads.Should().Equal(first.Id);
        progress.Should().Contain(line => line.Contains("is back in Immich's trash", StringComparison.Ordinal));
        (await rig.StoredAsync(queued)).Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task GuardProgressSurvivesDiscoveryFailure()
    {
        var immich = new InMemoryImmich(DateTimeOffset.UtcNow.AddDays(-200)) { FailSearch = new IOException("Immich is unreachable.") };
        using var rig = Rig(immich);
        var reupload = immich.Add("converted.mp4", "h264 old converted");
        await CompletedImmichAsync(rig, reupload);
        var progress = new List<string>();

        var result = await rig.Actions.AddVideosAsync(new(QueueAdditionSource.Immich), rig.Settings, (_, text) => progress.Add(text), default);

        result.Result.Lines.Should().ContainSingle().Which.Kind.Should().Be(LogKind.Error);
        result.Result.Notice.Should().Contain("Immich is unreachable.");
        immich.Get(reupload.Id).IsTrashed.Should().BeTrue();
        progress.Should().Contain(line => line.Contains("converted.mp4", StringComparison.Ordinal) && line.Contains("back in Immich's trash", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not configured")]
    [InlineData("bad url")]
    [InlineData("bad staging")]
    [InlineData("zero limit")]
    [InlineData("trash off")]
    public async Task ImmichRefusesInvalidInputBeforeDiscovery(string invalid)
    {
        // Any unexpected search or write throws; the refusal must precede both.
        var immich = new ImmichClientStub();
        using var rig = Rig(immich);
        if (invalid == "not configured") rig.Settings.Immich.BaseUrl = "";
        if (invalid == "bad url") rig.Settings.Immich.BaseUrl = "file:///library";
        if (invalid == "bad staging") rig.Settings.Immich.StagingDirectory = rig.File("not-a-folder", "x");
        if (invalid == "trash off") immich.Trash = new(Enabled: false, Days: 0);

        var done = await rig.Actions.AddVideosAsync(new(QueueAdditionSource.Immich, Limit: invalid == "zero limit" ? 0 : null), rig.Settings, (_, _) => { }, default);

        done.ForceCandidates.Should().BeNullOrEmpty();
        done.Result.Lines.Should().OnlyContain(line => line.Kind == LogKind.Warning || line.Kind == LogKind.Error);
        done.Result.Notice.Should().NotContain("Specified method is not supported");
        (await rig.Repository.ListAsync(rig.DatabasePath, terminal: false)).Should().BeEmpty();
    }

    [Fact]
    public async Task CancellingAnImmichAdditionKeepsQueuedVideosAndDeletesThePartialDownload()
    {
        var immich = new PausingImmich();
        using var rig = Rig(immich);
        var channel = Channel.CreateUnbounded<TuiMsg>();
        var effects = new TuiEffects(rig.Reader, rig.Actions, rig.Configuration, channel.Writer);
        effects.Run(new AddVideos(QueueAdditionSource.Immich), CancellationToken.None);
        await immich.Downloading.Task.WaitAsync(Patience);

        effects.Run(new CancelQueueAddition(), CancellationToken.None);
        var done = await CompletionAsync(channel.Reader);

        done.Result.Lines.Should().ContainSingle().Which.Kind.Should().Be(LogKind.Warning);
        done.Result.Notice.Should().Be("Stopped adding videos. Videos already queued stay queued.");
        var jobs = await rig.Repository.ListAsync(rig.DatabasePath, terminal: false);
        jobs.Should().ContainSingle().Which.ImmichAssetId.Should().Be(immich.First.Id);
        File.Exists(jobs[0].SourcePath).Should().BeTrue();
        Directory.Exists(Path.Combine(rig.Settings.Immich.StagingDirectory, immich.Second.Id)).Should().BeFalse();

        // The old source's completion cannot make a later addition uncancellable or leave its busy key held.
        immich.Pause = false;
        effects.Run(new AddVideos(QueueAdditionSource.Immich, Limit: 1), CancellationToken.None);
        (await CompletionAsync(channel.Reader)).Result.Notice.Should().Be("Queued: 1. Already queued: 1.");
        (await rig.Repository.ListAsync(rig.DatabasePath, terminal: false)).Should().HaveCount(2);
    }

    [Fact]
    public async Task AnImmediateCancelFindsTheAdditionSourceBeforeItsTaskStarts()
    {
        var probe = new PausingProbe();
        using var rig = Rig(probe: probe);
        var channel = Channel.CreateUnbounded<TuiMsg>();
        var effects = new TuiEffects(rig.Reader, rig.Actions, rig.Configuration, channel.Writer);

        effects.Run(new AddVideos(QueueAdditionSource.File, [rig.File("watch/clip.mp4", "h264 video")]), CancellationToken.None);
        effects.Run(new CancelQueueAddition(), CancellationToken.None);

        (await CompletionAsync(channel.Reader)).Result.Notice.Should().StartWith("Stopped adding videos.");
        (await rig.Repository.ListAsync(rig.DatabasePath, terminal: false)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnExternalCancellationIsAnErrorAndStillPostsTheTypedCompletion()
    {
        using var rig = Rig(probe: new TimeoutProbe());
        var channel = Channel.CreateUnbounded<TuiMsg>();
        var asked = new AddVideos(QueueAdditionSource.File, [rig.File("watch/clip.mp4", "h264 video")]);

        new TuiEffects(rig.Reader, rig.Actions, rig.Configuration, channel.Writer).Run(asked, CancellationToken.None);

        var done = await CompletionAsync(channel.Reader);
        done.Asked.Should().Be(asked);
        done.Result.Lines.Should().ContainSingle().Which.Kind.Should().Be(LogKind.Error);
        done.Result.Notice.Should().Be("Couldn't add videos: ffprobe timed out.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0path.mp4")]
    public async Task InvalidFilePathsNeverOfferForce(string path)
    {
        using var rig = Rig();
        var done = await rig.Actions.AddVideosAsync(new(QueueAdditionSource.File, [path]), rig.Settings, (_, _) => { }, default);
        done.ForceCandidates.Should().BeNullOrEmpty();
        done.Result.Lines.Should().ContainSingle().Which.Kind.Should().Be(LogKind.Warning);
    }

    private static TuiRig Rig(ImmichClientStub? client = null, IMediaProbe? probe = null)
    {
        var rig = new TuiRig(services =>
        {
            services.AddSingleton<Func<string, IMediaProbe>>(_ => probe ?? new ContentProbe());
            if (client is not null) services.AddSingleton<IImmichClientFactory>(client);
        });
        var watch = Path.Combine(rig.Folder, "watch");
        Directory.CreateDirectory(watch);
        rig.Settings.Watch.Roots = [new() { Path = watch }];
        rig.Settings.Savings.MinimumBytesSaved = "0B";
        rig.Settings.Eligibility.Rules = [new() { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "1Mbps", MinimumFileSize = "0B" }];
        rig.Settings.Immich.BaseUrl = "http://immich.test:2283";
        rig.Settings.Immich.StagingDirectory = Path.Combine(rig.Folder, "staging");
        return rig;
    }

    private static async Task<JobRecord> AddFileAsync(TuiRig rig, string path)
    {
        var done = await rig.Actions.AddVideosAsync(new(QueueAdditionSource.File, [path]), rig.Settings, (_, _) => { }, default);
        var job = await rig.Repository.FindOpenBySourceAsync(rig.DatabasePath, Path.GetFullPath(path));
        job.Should().NotBeNull(done.Result.Notice);
        return job!;
    }

    private static Task<JobRecord> CompletedImmichAsync(TuiRig rig, ImmichAsset reupload) => rig.Repository.CreateAsync(rig.DatabasePath, new JobRecord
    {
        Id = Guid.NewGuid(), SourceKind = JobSourceKind.Immich, SourcePath = Path.Combine(rig.Folder, "converted.mp4"),
        SourceFingerprint = "converted", Status = JobStatus.Completed, CompletedUtc = DateTimeOffset.UtcNow,
        ImmichAssetId = Guid.NewGuid().ToString(), ImmichChecksum = reupload.Checksum
    });

    private static async Task<QueueAdditionDoneMsg> CompletionAsync(ChannelReader<TuiMsg> reader)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (true)
            if (await reader.ReadAsync(timeout.Token) is QueueAdditionDoneMsg done) return done;
    }

    private sealed class PausingProbe : IMediaProbe
    {
        private readonly TaskCompletionSource<MediaInfo> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) => _never.Task.WaitAsync(cancellationToken);
    }

    private sealed class TimeoutProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromException<MediaInfo>(new OperationCanceledException("ffprobe timed out."));
    }

    /// <summary>Only the second download pauses, after writing bytes: cancellation must retain the first and clean the second.</summary>
    private sealed class PausingImmich : ImmichClientStub
    {
        private readonly InMemoryImmich _inner = new(DateTimeOffset.UtcNow.AddDays(-200));
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PausingImmich()
        {
            First = _inner.Add("first.mp4", "h264 first");
            Second = _inner.Add("second.mp4", "h264 second");
        }
        public ImmichAsset First { get; }
        public ImmichAsset Second { get; }
        public bool Pause { get; set; } = true;
        public TaskCompletionSource Downloading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var asset in _inner.SearchVideosAsync(takenBefore, includeFavorites, cancellationToken)) yield return asset;
        }

        public override async Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            await _inner.DownloadOriginalAsync(assetId, destinationPath, progress, cancellationToken);
            if (assetId != Second.Id || !Pause) return;
            Downloading.TrySetResult();
            await _never.Task.WaitAsync(cancellationToken);
        }
    }
}
