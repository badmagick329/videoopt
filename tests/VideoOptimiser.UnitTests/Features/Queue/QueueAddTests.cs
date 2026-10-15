using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Features.Queue;

/// <summary>
/// <c>queue add</c>, <c>queue add &lt;file&gt;</c> and <c>queue retry</c> through the real CLI (VOPT-54 step 5): every approved
/// line, whole, with its exit code. ffprobe and the readiness check are faked by file name: <c>hevc</c> probes as HEVC,
/// <c>broken</c> fails to probe, <c>locked</c> cannot be opened.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class QueueAddTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private string _config = string.Empty;

    public QueueAddTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Elsewhere);
    }

    private string DatabasePath => Path.Combine(_directory, "jobs.db");
    private string Root => Path.Combine(_directory, "Videos");
    private string Elsewhere => Path.Combine(_directory, "Other");
    private string StagingDirectory => Path.Combine(_directory, "staging");
    private string ConfigArgument => $" --config \"{_config}\"";

    [Fact]
    public async Task AFileThatFitsTheRulesIsQueuedAndTheNextLineSaysToRun()
    {
        var path = await FileAsync(Root, "clip.mp4");

        var (code, output, error) = await RunAsync("queue", "add", path);

        var job = (await _repository.FindOpenBySourceAsync(DatabasePath, path))!;
        (code, output, error).Should().Be((0, Lines($"Queued clip.mp4 as job {job.Id:N}.", $"Next: run 'video-optimiser run{ConfigArgument}'."), ""));
        job.Forced.Should().BeFalse();
        job.VideoFacts!.Codec.Should().Be("h264");
    }

    [Fact]
    public async Task WithVideosAheadItSaysHowManyAndHowToPutItFirst()
    {
        await QueuedAsync("first.mp4");
        await StopAsync(await QueuedAsync("second.mp4"), JobStatus.Encoding);
        await HeldAsync(await QueuedAsync("held.mp4"));
        await MoveAsync(await QueuedAsync("ready.mp4"), JobStatus.Validating, JobStatus.ReadyToFinalize);
        var finalising = await QueuedAsync("finalising.mp4");
        await MoveAsync(finalising, JobStatus.Validating, JobStatus.ReadyToFinalize);
        await StopAsync(finalising, JobStatus.Finalizing);
        var path = await FileAsync(Root, "clip.mp4");

        var (code, output, _) = await RunAsync("queue", "add", path);

        var job = (await _repository.FindOpenBySourceAsync(DatabasePath, path))!;
        (code, output).Should().Be((0, Lines($"Queued clip.mp4 as job {job.Id:N}.", $"2 videos are ahead of it; 'video-optimiser queue next {job.Id:N}{ConfigArgument}' puts it first.")));
    }

    [Theory]
    [InlineData("Other", "clip.mp4", ", outside your rules (--force)")]
    [InlineData("Videos", "hevc.mp4", ", outside your rules (--force)")]
    [InlineData("Videos", "clip.avi", ", outside your rules (--force)")]
    [InlineData("Private", "clip.mp4", ", outside your rules (--force)")]
    [InlineData("Videos", "clip.mp4", "")]
    public async Task ForceQueuesAFileOutsideTheWatchRootsOrTheRulesAndRemembersIt(string folder, string name, string outside)
    {
        var path = await FileAsync(folder == "Private" ? Path.Combine(Root, folder) : Path.Combine(_directory, folder), name);

        var (code, output, _) = await RunAsync("queue", "add", path, "--force");

        var job = (await _repository.FindOpenBySourceAsync(DatabasePath, path))!;
        (code, output).Should().Be((0, Lines($"Queued {name} as job {job.Id:N}{outside}.", $"Next: run 'video-optimiser run{ConfigArgument}'.")));
        job.Forced.Should().BeTrue();
    }

    [Fact]
    public async Task AFileOutsideTheWatchRootsIsRefusedWithTheForceCommand()
    {
        var path = await FileAsync(Elsewhere, "clip.mp4");

        var result = await RunAsync("queue", "add", path);

        result.Should().Be((5, "", Lines($"{path} is outside your watch roots. To queue it anyway: video-optimiser queue add \"{path}\" --force{ConfigArgument}")));
        (await _repository.FindOpenBySourceAsync(DatabasePath, path)).Should().BeNull();
    }

    [Theory]
    [InlineData("hevc.mp4", "hevc.mp4 doesn't fit your rules: rule 1: codec is hevc, needs h264.")]
    [InlineData("clip.avi", "clip.avi doesn't fit your rules: extension is not allowed.")]
    [InlineData(@"Private\clip.mp4", "clip.mp4 doesn't fit your rules: file is in an excluded directory.")]
    public async Task AFileOutsideTheRulesIsRefusedWithTheForceCommand(string name, string refusal)
    {
        var path = await FileAsync(Path.GetDirectoryName(Path.Combine(Root, name))!, Path.GetFileName(name));

        var result = await RunAsync("queue", "add", path);

        result.Should().Be((5, "", Lines($"{refusal} To queue it anyway: video-optimiser queue add \"{path}\" --force{ConfigArgument}")));
    }

    /// <summary>No AV1 of a video this small could save <c>savings.minimumBytesSaved</c>, so <c>--force</c> does not lift it.</summary>
    [Theory]
    [InlineData("clip.mp4", false)]
    [InlineData("clip.mp4", true)]
    [InlineData("hevc.mp4", false)]
    public async Task AFileTooSmallToSaveEnoughIsRefusedEvenWithForce(string name, bool force)
    {
        await WriteConfigAsync(minimumBytesSaved: "1KiB");
        var path = await FileAsync(Root, name);

        var result = force ? await RunAsync("queue", "add", path, "--force") : await RunAsync("queue", "add", path);

        result.Should().Be((5, "", Lines($"{name} doesn't fit your rules: too small to save 1KiB (savings.minimumBytesSaved).")));
        (await _repository.FindOpenBySourceAsync(DatabasePath, path)).Should().BeNull();
    }

    [Theory]
    [InlineData(".video-optimiser", "file video-optimiser made")]
    [InlineData("staging", "file video-optimiser made")]
    [InlineData("Archive", "original video-optimiser archived")]
    public async Task AFileTheToolMadeOrArchivedIsRefusedEvenWithForce(string folder, string what)
    {
        var path = await FileAsync(folder == "staging" ? Path.Combine(StagingDirectory, Guid.NewGuid().ToString("D")) : Path.Combine(Root, folder), "clip.mp4");

        var result = await RunAsync("queue", "add", path, "--force");

        result.Should().Be((5, "", Lines($"{path} is {(what.StartsWith('f') ? "a" : "an")} {what}, so it can't be queued.")));
    }

    [Fact]
    public async Task AMissingFileIsRefused()
    {
        var path = Path.Combine(Root, "clip.mp4");

        (await RunAsync("queue", "add", path)).Should().Be((6, "", Lines($"Original does not exist: {path}")));
    }

    [Theory]
    [InlineData("locked.mp4", "locked.mp4 can't be read, so it can't be queued: file cannot be opened for reading: in use.")]
    [InlineData("broken.mp4", "ffprobe can't read broken.mp4, so it can't be queued: ffprobe exited with 1: Invalid data found when processing input.")]
    public async Task AFileThatCannotBeReadOrProbedIsRefused(string name, string refusal)
    {
        var path = await FileAsync(Root, name);

        (await RunAsync("queue", "add", path)).Should().Be((6, "", Lines(refusal)));
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("held")]
    [InlineData("stopped")]
    [InlineData("ready to finalise")]
    [InlineData("stopped while being finalised")]
    [InlineData("finalising")]
    [InlineData("encoding")]
    [InlineData("stopped, left encoding by a crash")]
    public async Task AFileWithAnOpenJobNamesItAndItsStatus(string status)
    {
        var job = await QueuedAsync("clip.mp4");
        DatabaseLock? going = null;
        switch (status)
        {
            case "waiting": break;
            case "held": await HeldAsync(job); break;
            case "stopped": await StopAsync(job, JobStatus.Encoding); break;
            case "ready to finalise": await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize); break;
            case "stopped while being finalised":
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
            default: await MoveAsync(job, JobStatus.Encoding); break;
        }

        using (going)
        {
            (await RunAsync("queue", "add", job.SourcePath)).Should().Be((0, Lines($"clip.mp4 already has job {job.Id:N} ({status.Split(',')[0]})."), ""));
        }
    }

    /// <summary>An open job is named before the file is judged: one queued with --force from outside the roots is not refused.</summary>
    [Fact]
    public async Task AFileWithAnOpenJobOutsideTheRootsNamesTheJob()
    {
        var path = await FileAsync(Elsewhere, "clip.mp4");
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = path, SourceFingerprint = "original", Status = JobStatus.Queued, Forced = true });

        (await RunAsync("queue", "add", path)).Should().Be((0, Lines($"clip.mp4 already has job {job.Id:N} (waiting)."), ""));
    }

    [Fact]
    public async Task AFileWhoseLastFinaliseFailedSaysWhy()
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize);
        job.FailureCategory = "FinalizationFailed";
        job.FailureMessage = "The original changed since it was converted.";
        await _repository.UpdateAsync(DatabasePath, job);

        (await RunAsync("queue", "add", job.SourcePath)).Should().Be((0, Lines($"clip.mp4's last finalise failed (job {job.Id:N}): The original changed since it was converted."), ""));
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
        await _repository.UpdateAsync(DatabasePath, job);
        await MoveAsync(job, settled == JobStatus.Completed ? [JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing, settled] : [settled]);

        (await RunAsync("queue", "add", job.SourcePath)).Should().Be((0, Lines(string.Format(System.Globalization.CultureInfo.InvariantCulture, line, job.Id.ToString("N"), ConfigArgument)), ""));
    }

    /// <summary>An original that changed since its job settled it is a new video, and is queued.</summary>
    [Fact]
    public async Task AFileThatChangedSinceItsJobFailedIsQueued()
    {
        var job = await QueuedAsync("clip.mp4");
        job.SourceFingerprint = "the bytes before the change";
        await _repository.UpdateAsync(DatabasePath, job);
        await MoveAsync(job, JobStatus.Failed);

        var (code, output, _) = await RunAsync("queue", "add", job.SourcePath);

        var queued = (await _repository.FindOpenBySourceAsync(DatabasePath, job.SourcePath))!;
        (code, output).Should().Be((0, Lines($"Queued clip.mp4 as job {queued.Id:N}.", $"Next: run 'video-optimiser run{ConfigArgument}'.")));
    }

    /// <summary>R11: another command queued the file between the check and the insert; its job is named, and no second one made.</summary>
    [Fact]
    public async Task AFileQueuedFromAnotherTerminalMeanwhileNamesThatJob()
    {
        var path = await FileAsync(Root, "clip.mp4");
        var repository = new QueueingFirstRepository(_repository);

        var (code, output, _) = await RunAsync(services => services.AddSingleton<IJobRepository>(repository), "queue", "add", path);

        (code, output).Should().Be((0, Lines($"clip.mp4 already has job {repository.Raced!.Id:N} (waiting).")));
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

        result.Should().Be((0, Lines(
            "Scanning 1 watch root for videos to convert; files not seen before are probed with ffprobe.",
            $"Queued in {Root}:",
            "  new.mp4",
            "Queued: 1. Held: 1. Ready to finalise: 2. Real probes: 4.",
            $"Next: run 'video-optimiser run{ConfigArgument}'.",
            $"2 videos are ready to finalise: review them, then run 'video-optimiser finalize --ready{ConfigArgument}'."), ""));
    }

    [Theory]
    [InlineData(null, "Next: run 'video-optimiser run{0}'.")]
    [InlineData("video-optimiser run", "The run going now picks them up.")]
    [InlineData("video-optimiser immich run", "Next: run 'video-optimiser run{0}' once the run going now ends.")]
    public async Task TheNextLineDependsOnTheRunGoingNow(string? runGoing, string line)
    {
        await QueuedAsync("waiting.mp4");
        using var run = runGoing is null ? null : DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), runGoing, out _);

        var (code, output, _) = await RunAsync("queue", "add");

        code.Should().Be(0);
        output.Should().EndWith(Lines("Queued: 0. Already queued: 1. Real probes: 1.", string.Format(System.Globalization.CultureInfo.InvariantCulture, line, ConfigArgument)));
    }

    [Fact]
    public async Task AReadyVideoAloneGetsOnlyTheFinaliseLine()
    {
        await MoveAsync(await QueuedAsync("a.mp4"), JobStatus.Validating, JobStatus.ReadyToFinalize);

        var (_, output, _) = await RunAsync("queue", "add");

        output.Should().EndWith(Lines("Queued: 0. Ready to finalise: 1. Real probes: 1.", $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready{ConfigArgument}'."));
    }

    /// <summary>VOPT-73: every Issue line counts towards the issues total, and the scan exits 10.</summary>
    [Fact]
    public async Task AFileTheScanCouldNotProbeCountsAsAnIssue()
    {
        await FileAsync(Root, "broken.mp4");

        var result = await RunAsync("queue", "add");

        result.Should().Be((10, Lines(
            "Scanning 1 watch root for videos to convert; files not seen before are probed with ffprobe.",
            $"In {Root}:",
            "  Issue: broken.mp4: ffprobe exited with 1: Invalid data found when processing input.",
            "Queued: 0. Issues: 1."), ""));
    }

    [Fact]
    public async Task QueueAddWithNoWatchRootsPointsImmichUsersAtImmichRun()
    {
        await WriteConfigAsync(roots: false);

        (await RunAsync("queue", "add")).Should().Be((3, "", Lines("queue add needs at least one watch.roots entry. For Immich videos, use 'immich run'.")));
    }

    [Fact]
    public async Task RetryQueuesTheVideoAgainAndSaysToRun()
    {
        var job = await QueuedAsync("clip.mp4");
        await MoveAsync(job, JobStatus.Failed);

        var (code, output, _) = await RunAsync("queue", "retry", job.Id.ToString("N"));

        var replacement = (await _repository.FindOpenBySourceAsync(DatabasePath, job.SourcePath))!;
        (code, output).Should().Be((0, Lines($"Queued clip.mp4 again as job {replacement.Id:N}.", $"Next: run 'video-optimiser run{ConfigArgument}'.")));
    }

    [Fact]
    public async Task RetryOfAnImmichVideoWhoseCopyIsGoneSaysTheRunDownloadsItFirst()
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(StagingDirectory, "asset", "IMG_0001.MOV"), SourceFingerprint = "original", Status = JobStatus.Queued, SourceKind = JobSourceKind.Immich, ImmichAssetId = "asset", ImmichChecksum = "checksum" };
        await _repository.CreateAsync(DatabasePath, job);
        await MoveAsync(job, JobStatus.Failed);

        var (code, output, _) = await RunAsync("queue", "retry", job.Id.ToString("N"));

        var replacement = (await _repository.FindOpenByImmichAssetAsync(DatabasePath, "asset"))!;
        (code, output).Should().Be((0, Lines(
            $"Queued IMG_0001.MOV again as job {replacement.Id:N}.",
            $"Its downloaded copy is gone; 'video-optimiser run{ConfigArgument}' downloads it again first.",
            $"Next: run 'video-optimiser run{ConfigArgument}'.")));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) => RunAsync(_ => { }, args);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(Action<IServiceCollection> configure, params string[] args)
    {
        if (_config.Length == 0) await WriteConfigAsync();
        return await ConsoleCapture.RunCliAsync(services =>
        {
            services.AddSingleton<IFileReadinessService>(new NamedReadiness());
            services.AddSingleton<Func<string, IMediaProbe>>(_ => new NamedProbe());
            configure(services);
        }, [.. args, "--config", _config]);
    }

    private async Task WriteConfigAsync(string minimumBytesSaved = "1B", bool roots = true)
    {
        _config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(_config, string.Join('\n',
            "database:",
            $"  path: '{DatabasePath}'",
            "watch:",
            roots ? "  roots:" : "  roots: []",
            roots ? $"    - path: '{Root}'" : "",
            "eligibility:",
            "  excludedDirectories: ['.video-optimiser', 'Archive', 'Private']",
            "  rules:",
            "    - codecs: ['h264']",
            "      resolution: '1080p-1440p'",
            "      minimumVideoBitrate: '1Mbps'",
            "      minimumFileSize: '1B'",
            "savings:",
            $"  minimumBytesSaved: '{minimumBytesSaved}'",
            "immich:",
            $"  stagingDirectory: '{StagingDirectory}'",
            ""));
    }

    private async Task<Application.Configuration.AppSettings> SettingsAsync() =>
        (await new Infrastructure.Configuration.YamlConfigurationLoader().LoadAsync(_config)).Settings;

    private static async Task<string> FileAsync(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, "original video bytes");
        return path;
    }

    private async Task<JobRecord> QueuedAsync(string name)
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = await FileAsync(Root, name), SourceFingerprint = "original", Status = JobStatus.Queued };
        await _repository.CreateAsync(DatabasePath, job);
        await Task.Delay(5);
        return job;
    }

    private async Task MoveAsync(JobRecord job, params JobStatus[] steps)
    {
        foreach (var step in steps)
        {
            job.Status = step;
            await _repository.UpdateAsync(DatabasePath, job);
        }
    }

    private async Task StopAsync(JobRecord job, JobStatus stage)
    {
        await MoveAsync(job, stage);
        job.ResumeStatus = stage;
        await MoveAsync(job, JobStatus.Interrupted);
    }

    private async Task HeldAsync(JobRecord job) => (await _repository.SetHeldAsync(DatabasePath, job, held: true)).Should().BeTrue();

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
