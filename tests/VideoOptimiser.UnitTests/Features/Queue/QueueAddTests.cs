using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.Queue;

/// <summary>
/// <c>queue add</c>, <c>queue add &lt;file&gt;</c> and <c>queue retry</c> through the real CLI (VOPT-54 step 5): every approved
/// line, whole, with its exit code. ffprobe and the readiness check are faked by file name: <c>hevc</c> probes as HEVC,
/// <c>broken</c> fails to probe, <c>locked</c> cannot be opened.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class QueueAddTests : QueueTestBase
{
    public QueueAddTests() => Directory.CreateDirectory(Elsewhere);

    private string Elsewhere => Path.Combine(Workspace, "Other");

    protected override string[] DefaultYaml => Rules();

    protected override void RegisterFakes(IServiceCollection services)
    {
        services.AddSingleton<IFileReadinessService>(new NamedReadiness());
        services.AddSingleton<Func<string, IMediaProbe>>(_ => new NamedProbe());
    }

    [Fact]
    public async Task AFileThatFitsTheRulesIsQueuedAndTheNextLineSaysToRun()
    {
        var path = await FileAsync(Root, "clip.mp4");

        var (code, output, error) = await RunAsync("queue", "add", path);

        var job = (await Repository.FindOpenBySourceAsync(DatabasePath, path))!;
        (code, output, error).Should().Be((0, Transcript($"Queued clip.mp4 as job {job.Id:N}.", $"Next: run 'video-optimiser run{ConfigArgument}'."), ""));
        job.Forced.Should().BeFalse();
        job.VideoFacts!.Codec.Should().Be("h264");
    }

    /// <summary>Only a waiting or stopped video that is not held and not mid-finalisation is ahead; each file added counts the one before it.</summary>
    [Fact]
    public async Task WithVideosAheadItSaysHowManyAndHowToPutItFirst()
    {
        await StopAsync(await QueuedAsync("second.mp4"), JobStatus.Encoding);
        await HeldAsync(await QueuedAsync("held.mp4"));
        await MoveAsync(await QueuedAsync("ready.mp4"), JobStatus.Validating, JobStatus.ReadyToFinalize);
        var finalising = await QueuedAsync("finalising.mp4");
        await MoveAsync(finalising, JobStatus.Validating, JobStatus.ReadyToFinalize);
        await StopAsync(finalising, JobStatus.Finalizing);
        var first = await FileAsync(Root, "first.mp4");
        var clip = await FileAsync(Root, "clip.mp4");

        var singular = await RunAsync("queue", "add", first);
        var plural = await RunAsync("queue", "add", clip);

        var firstJob = (await Repository.FindOpenBySourceAsync(DatabasePath, first))!;
        var clipJob = (await Repository.FindOpenBySourceAsync(DatabasePath, clip))!;
        singular.Should().Be((0, Transcript($"Queued first.mp4 as job {firstJob.Id:N}.", $"1 video is ahead of it; 'video-optimiser queue next {firstJob.Id:N}{ConfigArgument}' puts it first."), ""));
        plural.Should().Be((0, Transcript($"Queued clip.mp4 as job {clipJob.Id:N}.", $"2 videos are ahead of it; 'video-optimiser queue next {clipJob.Id:N}{ConfigArgument}' puts it first."), ""));
    }

    [Theory]
    [InlineData("Other", "clip.mp4", ", outside your rules (--force)")]
    [InlineData("Videos", "hevc.mp4", ", outside your rules (--force)")]
    [InlineData("Videos", "clip.avi", ", outside your rules (--force)")]
    [InlineData("Private", "clip.mp4", ", outside your rules (--force)")]
    [InlineData("Videos", "clip.mp4", "")]
    public async Task ForceQueuesAFileOutsideTheWatchRootsOrTheRulesAndRemembersIt(string folder, string name, string outside)
    {
        var path = await FileAsync(folder == "Private" ? Path.Combine(Root, folder) : Path.Combine(Workspace, folder), name);

        var (code, output, _) = await RunAsync("queue", "add", path, "--force");

        var job = (await Repository.FindOpenBySourceAsync(DatabasePath, path))!;
        (code, output).Should().Be((0, Transcript($"Queued {name} as job {job.Id:N}{outside}.", $"Next: run 'video-optimiser run{ConfigArgument}'.")));
        job.Forced.Should().BeTrue();
    }

    [Fact]
    public async Task AFileOutsideTheWatchRootsIsRefusedWithTheForceCommand()
    {
        var path = await FileAsync(Elsewhere, "clip.mp4");

        var result = await RunAsync("queue", "add", path);

        result.Should().Be((5, "", Transcript($"{path} is outside your watch roots. To queue it anyway: video-optimiser queue add \"{path}\" --force{ConfigArgument}")));
        (await Repository.FindOpenBySourceAsync(DatabasePath, path)).Should().BeNull();
    }

    [Theory]
    [InlineData("hevc.mp4", "hevc.mp4 doesn't fit your rules: rule 1: codec is hevc, needs h264.")]
    [InlineData("clip.avi", "clip.avi doesn't fit your rules: extension is not allowed.")]
    [InlineData(@"Private\clip.mp4", "clip.mp4 doesn't fit your rules: file is in an excluded directory.")]
    public async Task AFileOutsideTheRulesIsRefusedWithTheForceCommand(string name, string refusal)
    {
        var path = await FileAsync(Path.GetDirectoryName(Path.Combine(Root, name))!, Path.GetFileName(name));

        var result = await RunAsync("queue", "add", path);

        result.Should().Be((5, "", Transcript($"{refusal} To queue it anyway: video-optimiser queue add \"{path}\" --force{ConfigArgument}")));
    }

    /// <summary>No AV1 of a video this small could save <c>savings.minimumBytesSaved</c>, so <c>--force</c> does not lift it.</summary>
    [Theory]
    [InlineData("clip.mp4", true)]
    [InlineData("hevc.mp4", false)]
    public async Task AFileTooSmallToSaveEnoughIsRefusedEvenWithForce(string name, bool force)
    {
        await WriteConfigAsync(Rules(minimumBytesSaved: "1KiB"));
        var path = await FileAsync(Root, name);

        var result = force ? await RunAsync("queue", "add", path, "--force") : await RunAsync("queue", "add", path);

        result.Should().Be((5, "", Transcript($"{name} doesn't fit your rules: too small to save 1KiB (savings.minimumBytesSaved).")));
        (await Repository.FindOpenBySourceAsync(DatabasePath, path)).Should().BeNull();
    }

    [Theory]
    [InlineData(".video-optimiser", "file video-optimiser made")]
    [InlineData("staging", "file video-optimiser made")]
    [InlineData("Archive", "original video-optimiser archived")]
    public async Task AFileTheToolMadeOrArchivedIsRefusedEvenWithForce(string folder, string what)
    {
        var path = await FileAsync(folder == "staging" ? Path.Combine(StagingDirectory, Guid.NewGuid().ToString("D")) : Path.Combine(Root, folder), "clip.mp4");

        var result = await RunAsync("queue", "add", path, "--force");

        result.Should().Be((5, "", Transcript($"{path} is {(what.StartsWith('f') ? "a" : "an")} {what}, so it can't be queued.")));
    }

    [Fact]
    public async Task AMissingFileIsRefused()
    {
        var path = Path.Combine(Root, "clip.mp4");

        (await RunAsync("queue", "add", path)).Should().Be((6, "", Transcript($"Original does not exist: {path}")));
    }

    [Theory]
    [InlineData("locked.mp4", "Can't queue locked.mp4: file cannot be opened for reading: in use.")]
    [InlineData("broken.mp4", "Can't queue broken.mp4: ffprobe exited with 1: Invalid data found when processing input.")]
    public async Task AFileThatCannotBeReadOrProbedIsRefused(string name, string refusal)
    {
        var path = await FileAsync(Root, name);

        (await RunAsync("queue", "add", path)).Should().Be((6, "", Transcript(refusal)));
    }

    /// <summary>
    /// The last two rows are what a crash leaves: a converting or finalising status with no run or finalise going reads as
    /// stopped. The waiting row is <see cref="AFileWithAnOpenJobOutsideTheRootsNamesTheJob"/>.
    /// </summary>
    [Theory]
    [InlineData("held", "held")]
    [InlineData("stopped", "stopped")]
    [InlineData("ready", "ready to finalise")]
    [InlineData("stopped while finalising", "stopped while being finalised")]
    [InlineData("finalising", "finalising")]
    [InlineData("encoding", "encoding")]
    [InlineData("left encoding by a crash", "stopped")]
    [InlineData("left finalising by a crash", "stopped while being finalised")]
    public async Task AFileWithAnOpenJobNamesItAndItsStatus(string arrangement, string phrase)
    {
        var job = await QueuedAsync("clip.mp4");
        DatabaseLock? going = null;
        switch (arrangement)
        {
            case "held": await HeldAsync(job); break;
            case "stopped": await StopAsync(job, JobStatus.Encoding); break;
            case "ready": await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize); break;
            case "stopped while finalising":
                await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize);
                await StopAsync(job, JobStatus.Finalizing);
                break;
            case "finalising":
                await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
                going = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);
                break;
            case "encoding":
                await MoveAsync(job, JobStatus.Encoding);
                going = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), "video-optimiser run", out _);
                break;
            case "left encoding by a crash": await MoveAsync(job, JobStatus.Encoding); break;
            default: await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing); break;
        }

        using (going)
        {
            (await RunAsync("queue", "add", job.SourcePath)).Should().Be((0, Transcript($"clip.mp4 already has job {job.Id:N} ({phrase})."), ""));
        }
    }

    /// <summary>An open job is named before the file is judged: one queued with --force from outside the roots is not refused.</summary>
    [Fact]
    public async Task AFileWithAnOpenJobOutsideTheRootsNamesTheJob()
    {
        var path = await FileAsync(Elsewhere, "clip.mp4");
        var job = await Repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = path, SourceFingerprint = "original", Status = JobStatus.Queued, Forced = true });

        (await RunAsync("queue", "add", path)).Should().Be((0, Transcript($"clip.mp4 already has job {job.Id:N} (waiting)."), ""));
    }

    [Fact]
    public async Task AFileWhoseLastFinaliseFailedSaysWhy()
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize);
        job.FailureCategory = "FinalizationFailed";
        job.FailureMessage = "The original changed since it was converted.";
        await Repository.UpdateAsync(DatabasePath, job);

        (await RunAsync("queue", "add", job.SourcePath)).Should().Be((0, Transcript($"clip.mp4's last finalise failed (job {job.Id:N}): The original changed since it was converted."), ""));
    }

    [Theory]
    [InlineData(JobStatus.Failed, "clip.mp4 failed before (job {0}). To try it again: video-optimiser queue retry {0}{1}")]
    [InlineData(JobStatus.Cancelled, "clip.mp4 was dropped before (job {0}). To try it again: video-optimiser queue retry {0}{1}")]
    [InlineData(JobStatus.Skipped, "clip.mp4 was not worth converting before (job {0}). To try it again: video-optimiser queue retry {0}{1}")]
    [InlineData(JobStatus.Completed, "clip.mp4 was already converted (job {0}).")]
    public async Task AFileItsLastJobSettledPointsAtRetry(JobStatus settled, string line)
    {
        await WriteConfigAsync();
        var job = await QueuedAsync("clip.mp4");
        job.SourceFingerprint = await new FileFingerprintService().CreateAsync(job.SourcePath);
        // The quality settings that skipped it are the ones in force, so the skip still holds.
        job.QualityFingerprint = Application.Jobs.QualityFingerprint.Compute(await SettingsAsync());
        await Repository.UpdateAsync(DatabasePath, job);
        await MoveAsync(job, settled == JobStatus.Completed ? [JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing, settled] : [settled]);

        (await RunAsync("queue", "add", job.SourcePath)).Should().Be((0, Transcript(string.Format(System.Globalization.CultureInfo.InvariantCulture, line, job.Id.ToString("N"), ConfigArgument)), ""));
    }

    /// <summary>R11: another command queued the file between the check and the insert; its job is named, and no second one made.</summary>
    [Fact]
    public async Task AFileQueuedFromAnotherTerminalMeanwhileNamesThatJob()
    {
        var path = await FileAsync(Root, "clip.mp4");
        var repository = new QueueingFirstRepository(Repository);

        var (code, output, _) = await RunAsync(services => services.AddSingleton<IJobRepository>(repository), "queue", "add", path);

        (code, output).Should().Be((0, Transcript($"clip.mp4 already has job {repository.Raced!.Id:N} (waiting).")));
    }

    [Fact]
    public async Task QueueAddCountsHeldAndReadyVideosApartAndSaysWhatToRunForEach()
    {
        await HeldAsync(await QueuedAsync("held.mp4"));
        var ready = await QueuedAsync("ready.mp4");
        await MoveAsync(ready, JobStatus.Validating, JobStatus.ReadyToFinalize);
        var finalising = await QueuedAsync("finalising.mp4");
        await MoveAsync(finalising, JobStatus.Validating, JobStatus.ReadyToFinalize);
        await StopAsync(finalising, JobStatus.Finalizing);
        await FileAsync(Root, "new.mp4");

        var result = await RunAsync("queue", "add");

        result.Should().Be((0, Transcript(
            "Scanning 1 watch root for videos to convert; files not seen before are probed with ffprobe.",
            $"Queued in {Root}:",
            "  new.mp4",
            "Queued: 1. Held: 1. Ready to finalise: 2.",
            $"Next: run 'video-optimiser run{ConfigArgument}'.",
            $"2 videos are ready to finalise: review them, then run 'video-optimiser finalize --ready{ConfigArgument}'."), ""));
    }

    /// <summary>With no run going the line is the one the transcripts above print.</summary>
    [Theory]
    [InlineData("video-optimiser run", "The run going now picks them up.")]
    [InlineData("video-optimiser immich run", "The run going now picks them up.")]
    [InlineData("video-optimiser background run", "The run going now picks them up.")]
    [InlineData("video-optimiser queue process", "Next: run 'video-optimiser run{0}' once the run going now ends.")]
    public async Task TheNextLineDependsOnTheRunGoingNow(string runGoing, string line)
    {
        await QueuedAsync("waiting.mp4");
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), runGoing, out _);

        var (code, output, _) = await RunAsync("queue", "add");

        code.Should().Be(0);
        output.Should().EndWith(Transcript("Queued: 0. Already queued: 1.", string.Format(System.Globalization.CultureInfo.InvariantCulture, line, ConfigArgument)));
    }

    [Fact]
    public async Task AReadyVideoAloneGetsOnlyTheFinaliseLine()
    {
        await MoveAsync(await QueuedAsync("a.mp4"), JobStatus.Validating, JobStatus.ReadyToFinalize);

        var (_, output, _) = await RunAsync("queue", "add");

        output.Should().EndWith(Transcript("Queued: 0. Ready to finalise: 1.", $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready{ConfigArgument}'."));
    }

    /// <summary>VOPT-73: every Issue line counts towards the issues total, and the scan exits 10.</summary>
    [Fact]
    public async Task AFileTheScanCouldNotProbeCountsAsAnIssue()
    {
        await FileAsync(Root, "broken.mp4");

        var result = await RunAsync("queue", "add");

        result.Should().Be((10, Transcript(
            "Scanning 1 watch root for videos to convert; files not seen before are probed with ffprobe.",
            $"In {Root}:",
            "  Issue: broken.mp4: ffprobe exited with 1: Invalid data found when processing input.",
            "Queued: 0. Issues: 1."), ""));
    }

    [Fact]
    public async Task QueueAddWithNoWatchRootsPointsImmichUsersAtQueueAddImmich()
    {
        await WriteConfigAsync(Rules(roots: false));

        (await RunAsync("queue", "add")).Should().Be((3, "", Transcript($"queue add needs at least one watch.roots entry. For Immich videos, use 'video-optimiser queue add immich{ConfigArgument}'.")));
    }

    [Fact]
    public async Task RetryQueuesTheVideoAgainAndSaysToRun()
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Failed);

        var (code, output, _) = await RunAsync("queue", "retry", job.Id.ToString("N"));

        var replacement = (await Repository.FindOpenBySourceAsync(DatabasePath, job.SourcePath))!;
        (code, output).Should().Be((0, Transcript($"Queued clip.mp4 again as job {replacement.Id:N}.", $"Next: run 'video-optimiser run{ConfigArgument}'.")));
    }

    /// <summary>The run downloads a retried Immich video again only when its staged copy is gone, so only then does the retry say so.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryOfAnImmichVideoSaysTheRunDownloadsItFirstOnlyWhenItsCopyIsGone(bool copyIsGone)
    {
        var source = Path.Combine(StagingDirectory, "asset", "IMG_0001.MOV");
        if (!copyIsGone) await FileAsync(Path.GetDirectoryName(source)!, "IMG_0001.MOV");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = JobStatus.Queued, SourceKind = JobSourceKind.Immich, ImmichAssetId = "asset", ImmichChecksum = "checksum" };
        await Repository.CreateAsync(DatabasePath, job);
        await MoveAsync(job, JobStatus.Failed);

        var (code, output, _) = await RunAsync("queue", "retry", job.Id.ToString("N"));

        var replacement = (await Repository.FindOpenByImmichAssetAsync(DatabasePath, "asset"))!;
        string[] download = copyIsGone ? [$"Its downloaded copy is gone; 'video-optimiser run{ConfigArgument}' downloads it again first."] : [];
        (code, output).Should().Be((0, Transcript([$"Queued IMG_0001.MOV again as job {replacement.Id:N}.", .. download, $"Next: run 'video-optimiser run{ConfigArgument}'."])));
    }

    private string[] Rules(string minimumBytesSaved = "1B", bool roots = true) =>
    [
        "watch:",
        roots ? "  roots:" : "  roots: []",
        .. roots ? new[] { $"    - path: '{Root}'" } : [],
        "eligibility:",
        "  excludedDirectories: ['.video-optimiser', 'Archive', 'Private']",
        "  rules:",
        "    - codecs: ['h264']",
        "      resolution: '1080p-1440p'",
        "      minimumVideoBitrate: '1Mbps'",
        "      minimumFileSize: '1B'",
        "savings:",
        $"  minimumBytesSaved: '{minimumBytesSaved}'"
    ];

    private async Task<Application.Configuration.AppSettings> SettingsAsync() =>
        (await new Infrastructure.Configuration.YamlConfigurationLoader().LoadAsync(Config)).Settings;

    private sealed class NamedReadiness : IFileReadinessService
    {
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(
            Path.GetFileName(path).StartsWith("locked", StringComparison.Ordinal) ? new FileReadinessResult(false, "File cannot be opened for reading: in use.") : new FileReadinessResult(true, "File can be read."));
    }

    private sealed class NamedProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Path.GetFileName(path).StartsWith("broken", StringComparison.Ordinal) ? Task.FromException<MediaInfo>(new MediaProbeException("ffprobe exited with 1: Invalid data found when processing input."))
            : Task.FromResult(new MediaInfo(Path.GetFileName(path).StartsWith("hevc", StringComparison.Ordinal) ? "hevc" : "h264", 1, 0, 0, 0, null, null, 1920, 1080, 10_000_000));
    }

    /// <summary>Queues the file from "another terminal" just before the command's own insert.</summary>
    private sealed class QueueingFirstRepository(IJobRepository inner) : DelegatingJobRepository(inner)
    {
        public JobRecord? Raced { get; private set; }

        public override async Task<JobRecord> CreateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
        {
            Raced ??= await Inner.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = job.SourcePath, SourceFingerprint = job.SourceFingerprint, Status = JobStatus.Queued }, cancellationToken);
            return await Inner.CreateAsync(databasePath, job, cancellationToken);
        }
    }
}
