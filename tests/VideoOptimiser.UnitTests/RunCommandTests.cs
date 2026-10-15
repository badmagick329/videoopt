using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// <c>run</c> through the real CLI (VOPT-54 step 5): it converts folder and Immich videos alike, gives an Immich video its
/// downloaded copy back or leaves it queued with a line, and names itself in the run lock. Restaging is the real
/// <c>ImmichDiscoveryService</c> against a fake Immich that serves <see cref="Content"/>.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class RunCommandTests : IDisposable
{
    private const string Content = "immich original";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeImmich _immich = new();
    private readonly ReadyProcessor _processor;
    private string _config = string.Empty;

    public RunCommandTests()
    {
        Directory.CreateDirectory(_directory);
        _processor = new ReadyProcessor(_repository);
    }

    private string DatabasePath => Path.Combine(_directory, "jobs.db");
    private string StagingDirectory => Path.Combine(_directory, "staging");

    [Fact]
    public async Task RunConvertsFolderAndImmichVideosInRunOrderAndNamesItselfInTheRunLock()
    {
        await WriteConfigAsync(immich: true);
        await FolderJobAsync("a.mp4");
        await ImmichJobAsync();
        await FolderJobAsync("b.mp4");

        var (code, _, _) = await RunAsync("run");

        code.Should().Be(0);
        _processor.Processed.Should().Equal("a.mp4", "IMG_0001.MOV", "b.mp4");
        _processor.RunLockHolders.Should().AllBe("video-optimiser run");
    }

    [Fact]
    public async Task WithoutImmichSetUpItsVideosStayQueuedAndFolderVideosConvert()
    {
        await WriteConfigAsync(immich: false);
        var folder = await FolderJobAsync("clip.mp4");
        var immich = new[] { await ImmichJobAsync(), await ImmichJobAsync("IMG_0002.MOV") };

        var (code, output, _) = await RunAsync("run");

        (code, output).Should().Be((0, Lines(
            "Encoding on this PC.",
            "2 Immich videos stay queued: immich.baseUrl isn't set.",
            "Converting 1 video.",
            "",
            "[1/1] clip.mp4 (8 B)",
            "      Ready to finalise.",
            "Ready to finalise: 1.",
            "Next: review the outputs, then run 'video-optimiser finalize --ready'.")));
        _processor.Processed.Should().Equal("clip.mp4");
        foreach (var job in immich) (await StoredAsync(job)).Status.Should().Be(JobStatus.Queued);
        (await StoredAsync(folder)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    [Fact]
    public async Task AMissingDownloadedCopyIsDownloadedAgainBeforeTheRunConvertsIt()
    {
        await WriteConfigAsync(immich: true);
        var job = await ImmichJobAsync(staged: false);

        var (code, output, _) = await RunAsync("run");

        (code, output).Should().Be((0, Lines(
            "Encoding on this PC.",
            $"Downloading again: IMG_0001.MOV (job {job.Id:N} is open but its staged copy is missing)",
            "  done: the staged copy is back",
            "Converting 1 video.",
            "",
            "[1/1] IMG_0001.MOV (15 B)",
            "      Ready to finalise.",
            "Ready to finalise: 1.",
            "Next: review the outputs, then run 'video-optimiser finalize --ready'.")));
        (await File.ReadAllTextAsync(job.SourcePath)).Should().Be(Content);
    }

    [Fact]
    public async Task ACopyImmichNoLongerHasFailsItsVideoAndTheRunListsIt()
    {
        await WriteConfigAsync(immich: true);
        var job = await ImmichJobAsync(staged: false);
        _immich.Gone = true;

        var (code, output, _) = await RunAsync("run");

        (code, output).Should().Be((6, Lines(
            "Encoding on this PC.",
            $"Downloading again: IMG_0001.MOV (job {job.Id:N} is open but its staged copy is missing)",
            "  issue: Immich did not return the video again (Immich returned HTTP 404 for GET /api/assets); the job is marked failed",
            "Failed: 1.",
            "",
            "Failed jobs:",
            $"{job.Id:N}  IMG_0001.MOV",
            $"SourceMissing: The staged original is missing, and Immich did not return asset {job.ImmichAssetId} again: Immich returned HTTP 404 for GET /api/assets.")));
        _processor.Processed.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenImmichCannotBeUsedItsVideosStayQueuedAndFolderVideosConvert()
    {
        await WriteConfigAsync(immich: true);
        await FolderJobAsync("clip.mp4");
        var immich = await ImmichJobAsync(staged: false);
        _immich.Failure = new ImmichApiException("ImmichUnavailable", "Immich is unreachable at http://immich.test: No such host is known.");

        var (code, output, _) = await RunAsync("run");

        (code, output).Should().Be((0, Lines(
            "Encoding on this PC.",
            $"Downloading again: IMG_0001.MOV (job {immich.Id:N} is open but its staged copy is missing)",
            "1 Immich video stays queued: Immich can't be used (Immich is unreachable at http://immich.test: No such host is known).",
            "Converting 1 video.",
            "",
            "[1/1] clip.mp4 (8 B)",
            "      Ready to finalise.",
            "Ready to finalise: 1.",
            "Next: review the outputs, then run 'video-optimiser finalize --ready'.")));
        (await StoredAsync(immich)).Status.Should().Be(JobStatus.Queued);
    }

    /// <summary>
    /// Only a gone asset fails its video. A server error says nothing about the asset, so Immich counts as unusable and the
    /// videos it did not fail stay queued; the one failed before it is still listed.
    /// </summary>
    [Fact]
    public async Task AServerErrorAfterAGoneAssetFailsOnlyThatOneAndLeavesTheRestQueued()
    {
        await WriteConfigAsync(immich: true);
        var gone = await ImmichJobAsync(staged: false);
        var erroring = await ImmichJobAsync("IMG_0002.MOV", staged: false);
        var later = await ImmichJobAsync("IMG_0003.MOV", staged: false);
        _immich.Failures[gone.ImmichAssetId!] = FakeImmich.NotFound;
        _immich.Failures[erroring.ImmichAssetId!] = new ImmichApiException("ImmichRequestFailed", "Immich returned HTTP 502 for GET /api/assets.", 502);

        var (code, output, _) = await RunAsync("run");

        (code, output).Should().Be((6, Lines(
            "Encoding on this PC.",
            $"Downloading again: IMG_0001.MOV (job {gone.Id:N} is open but its staged copy is missing)",
            "  issue: Immich did not return the video again (Immich returned HTTP 404 for GET /api/assets); the job is marked failed",
            $"Downloading again: IMG_0002.MOV (job {erroring.Id:N} is open but its staged copy is missing)",
            "2 Immich videos stay queued: Immich can't be used (Immich returned HTTP 502 for GET /api/assets).",
            "Failed: 1.",
            "",
            "Failed jobs:",
            $"{gone.Id:N}  IMG_0001.MOV",
            $"SourceMissing: The staged original is missing, and Immich did not return asset {gone.ImmichAssetId} again: Immich returned HTTP 404 for GET /api/assets.")));
        (await StoredAsync(erroring)).Status.Should().Be(JobStatus.Queued);
        (await StoredAsync(later)).Status.Should().Be(JobStatus.Queued);
    }

    /// <summary>A joining video whose copy Immich no longer has is failed and listed, as one the run prepared would be.</summary>
    [Fact]
    public async Task AJoiningImmichVideoImmichNoLongerHasIsListedAsFailed()
    {
        await WriteConfigAsync(immich: true);
        await FolderJobAsync("clip.mp4");
        var joining = await ImmichJobAsync(staged: false);
        (await _repository.SetHeldAsync(DatabasePath, joining, held: true)).Should().BeTrue();
        _immich.Gone = true;
        _processor.During = async () => (await _repository.SetHeldAsync(DatabasePath, await StoredAsync(joining), held: false)).Should().BeTrue();

        var (code, output, _) = await RunAsync("run");

        code.Should().Be(10);
        output.Should().EndWith(Lines(
            "Ready to finalise: 1. Failed: 1.",
            "",
            "Failed jobs:",
            $"{joining.Id:N}  IMG_0001.MOV",
            $"SourceMissing: The staged original is missing, and Immich did not return asset {joining.ImmichAssetId} again: Immich returned HTTP 404 for GET /api/assets.",
            "Next: review the outputs, then run 'video-optimiser finalize --ready'."));
        _processor.Processed.Should().Equal("clip.mp4");
    }

    /// <summary>Without Immich the run is kept to folder videos, so an Immich video released meanwhile waits too.</summary>
    [Fact]
    public async Task WithoutImmichSetUpAJoiningImmichVideoStaysQueued()
    {
        await WriteConfigAsync(immich: false);
        await FolderJobAsync("clip.mp4");
        var joining = await ImmichJobAsync();
        (await _repository.SetHeldAsync(DatabasePath, joining, held: true)).Should().BeTrue();
        _processor.During = async () => (await _repository.SetHeldAsync(DatabasePath, await StoredAsync(joining), held: false)).Should().BeTrue();

        _ = await RunAsync("run");

        _processor.Processed.Should().Equal("clip.mp4");
        (await StoredAsync(joining)).Status.Should().Be(JobStatus.Queued);
    }

    /// <summary>A video held from another terminal while its copy failed to come back keeps the hold, and the run goes on.</summary>
    [Fact]
    public async Task AVideoHeldWhileItsCopyFailsKeepsTheHold()
    {
        await WriteConfigAsync(immich: true);
        var job = await ImmichJobAsync(staged: false);
        _immich.Gone = true;
        var repository = new HoldingBeforeFailRepository(_repository);

        var (code, _, _) = await RunAsync(services => services.AddSingleton<IJobRepository>(repository), "run");

        code.Should().Be(0);
        var stored = await StoredAsync(job);
        (stored.Status, stored.Held).Should().Be((JobStatus.Queued, true));
    }

    /// <summary>An Immich video released during the run lost its copy meanwhile; it gets it back before it starts.</summary>
    [Fact]
    public async Task AJoiningImmichVideoGetsItsCopyBackBeforeItStarts()
    {
        await WriteConfigAsync(immich: true);
        await FolderJobAsync("clip.mp4");
        var joining = await ImmichJobAsync(staged: false);
        (await _repository.SetHeldAsync(DatabasePath, joining, held: true)).Should().BeTrue();
        _processor.During = async () => (await _repository.SetHeldAsync(DatabasePath, await StoredAsync(joining), held: false)).Should().BeTrue();

        var (code, output, _) = await RunAsync("run");

        code.Should().Be(0);
        output.Should().Contain(Lines(
            "      Ready to finalise.",
            $"Downloading again: IMG_0001.MOV (job {joining.Id:N} is open but its staged copy is missing)",
            "  done: the staged copy is back",
            "",
            "[2/2] IMG_0001.MOV (15 B)"));
        _processor.Processed.Should().Equal("clip.mp4", "IMG_0001.MOV");
    }

    [Fact]
    public async Task HeldVideosAreCountedBeforeTheRunConverts()
    {
        await WriteConfigAsync(immich: false);
        foreach (var name in new[] { "a.mp4", "b.mp4" }) (await _repository.SetHeldAsync(DatabasePath, await FolderJobAsync(name), held: true)).Should().BeTrue();
        await FolderJobAsync("clip.mp4");

        var (_, output, _) = await RunAsync("run");

        output.Should().StartWith(Lines("Encoding on this PC.", "2 videos are held; runs pass over them until they're released.", "Converting 1 video."));
        _processor.Processed.Should().Equal("clip.mp4");
    }

    /// <summary>The held line is <c>run</c>'s: <c>immich run</c>'s limit already leaves held videos out, and says nothing of them.</summary>
    [Fact]
    public async Task ImmichRunDoesNotCountHeldVideos()
    {
        await WriteConfigAsync(immich: true);
        var preparation = new FixedImmichPreparation(new QueuePreparationResult([], [], [], [], JobSourceKind.Immich, FollowQueue: false, Held: 2));

        var (_, output, _) = await RunAsync(services => services.AddSingleton<IImmichRunService>(preparation), "immich", "run");

        output.Should().NotContain("held");
    }

    [Fact]
    public async Task ImmichRunNamesItselfInTheRunLock()
    {
        await WriteConfigAsync(immich: true);
        var preparation = new HolderReadingPreparation(DatabasePath);

        _ = await RunAsync(services => services.AddSingleton<IImmichRunService>(preparation), "immich", "run");

        preparation.Holder.Should().Be("video-optimiser immich run");
    }

    [Theory]
    [InlineData("'queue run' is now 'run'.", "queue", "run")]
    [InlineData("'queue discover' is now 'queue add'.", "queue", "discover", "--first")]
    [InlineData("'process' was removed: use 'queue add <file>', then 'run'.", "process", "clip.mp4", "--force")]
    public async Task ARemovedCommandSaysWhatReplacedIt(string line, params string[] args)
    {
        var (code, output, error) = await ConsoleCapture.RunCliAsync(args);

        (code, output).Should().Be((2, ""));
        error.Should().StartWith(Lines(line));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) => RunAsync(_ => { }, args);

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(Action<IServiceCollection> configure, params string[] args) =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton<IImmichClientFactory>(_immich);
            services.AddSingleton<IJobProcessor>(_processor);
            configure(services);
        }, [.. args, "--config", _config]);

    private async Task WriteConfigAsync(bool immich)
    {
        _config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(_config, string.Join('\n',
            "database:",
            $"  path: '{DatabasePath}'",
            "immich:",
            immich ? "  baseUrl: 'http://immich.test'" : "",
            $"  stagingDirectory: '{StagingDirectory}'",
            ""));
    }

    private async Task<JobRecord> FolderJobAsync(string name)
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path, "original");
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = path, Status = JobStatus.Queued });
        await Task.Delay(5);
        return job;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Immich's checksum is SHA-1.")]
    private async Task<JobRecord> ImmichJobAsync(string name = "IMG_0001.MOV", bool staged = true)
    {
        var assetId = Guid.NewGuid().ToString("D");
        var path = Path.Combine(StagingDirectory, assetId, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (staged) await File.WriteAllTextAsync(path, Content);
        var job = await _repository.CreateAsync(DatabasePath, new JobRecord
        {
            Id = Guid.NewGuid(), SourcePath = path, Status = JobStatus.Queued, SourceKind = JobSourceKind.Immich, ImmichAssetId = assetId,
            ImmichChecksum = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(Content)))
        });
        await Task.Delay(5);
        return job;
    }

    private async Task<JobRecord> StoredAsync(JobRecord job) => (await _repository.GetAsync(DatabasePath, job.Id))!;

    /// <summary>Makes each video ready to finalise, noting who holds the run lock while it converts.</summary>
    private sealed class ReadyProcessor(SqliteJobRepository repository) : IJobProcessor
    {
        public List<string> Processed { get; } = [];
        public List<string?> RunLockHolders { get; } = [];
        public Func<Task>? During { get; set; }

        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public async Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Processed.Add(Path.GetFileName(job.SourcePath));
            RunLockHolders.Add(DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(databasePath))?.Command);
            job.Status = JobStatus.Validating;
            await repository.UpdateAsync(databasePath, job, CancellationToken.None);
            job.Status = JobStatus.ReadyToFinalize;
            await repository.UpdateAsync(databasePath, job, CancellationToken.None);
            progress?.Report(JobProgress.Outcome(JobTone.Success, "Ready to finalise."));
            if (During is { } during)
            {
                During = null;
                await during();
            }
            return new JobProcessingResult(job, ExitCode.Success, "Ready to finalise.");
        }
    }

    /// <summary>Holds the job from "another terminal" just before restaging writes it as failed.</summary>
    private sealed class HoldingBeforeFailRepository(IJobRepository inner) : DelegatingJobRepository(inner)
    {
        public override async Task UpdateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default)
        {
            if (job.Status == JobStatus.Failed) (await Inner.SetHeldAsync(databasePath, (await Inner.GetAsync(databasePath, job.Id, cancellationToken))!, held: true, cancellationToken)).Should().BeTrue();
            await Inner.UpdateAsync(databasePath, job, cancellationToken);
        }
    }

    /// <summary>An <c>immich run</c> whose guard and search found nothing, with <paramref name="queue"/> to convert.</summary>
    private sealed class FixedImmichPreparation(QueuePreparationResult queue) : IImmichRunService
    {
        public Task<ImmichRunPreparation> PrepareAsync(string databasePath, AppSettings settings, int? limit, IProgress<ScanProgress>? guardProgress = null, IProgress<ImmichProgress>? discoveryProgress = null, bool modeOverridden = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImmichRunPreparation(new ImmichReuploadGuardResult(0, [], [], []), new ImmichDiscoveryResult([], 0, 0, 0, 0, 0, 0, 0, 0, 0, []), queue));
    }

    /// <summary>Reads the run lock's holder as <c>immich run</c> prepares, then ends the run.</summary>
    private sealed class HolderReadingPreparation(string databasePath) : IImmichRunService
    {
        public string? Holder { get; private set; }

        public Task<ImmichRunPreparation> PrepareAsync(string ignored, AppSettings settings, int? limit, IProgress<ScanProgress>? guardProgress = null, IProgress<ImmichProgress>? discoveryProgress = null, bool modeOverridden = false, CancellationToken cancellationToken = default)
        {
            Holder = DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(databasePath))?.Command;
            throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// Serves <see cref="Content"/> for every download, or a 404 when <see cref="Gone"/>, or <see cref="Failure"/>, or the
    /// failure <see cref="Failures"/> names for that asset.
    /// </summary>
    private sealed class FakeImmich : IImmichClientFactory, IImmichClient
    {
        public static readonly ImmichApiException NotFound = new("ImmichRequestFailed", "Immich returned HTTP 404 for GET /api/assets.", 404);

        public bool Gone { get; set; }
        public Exception? Failure { get; set; }
        public Dictionary<string, Exception> Failures { get; } = [];

        public IImmichClient Create(ImmichSettings settings) => this;

        public async Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            if (Failures.TryGetValue(assetId, out var failure)) throw failure;
            if (Gone) throw NotFound;
            await File.WriteAllTextAsync(destinationPath, Content, cancellationToken);
        }

        public Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichTrashSettings> GetTrashSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
