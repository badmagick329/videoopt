using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Immich;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// <c>immich run</c> then <c>immich finalize</c> through the real services, with Immich and the encoder faked: what the CLI
/// runs, minus the console and the worker lifecycle.
/// </summary>
public sealed class ImmichRunTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = Now.AddDays(-200);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeImmich _immich = new();
    private readonly AppSettings _settings;
    private readonly QueueService _queue;
    private readonly ImmichRunService _run;
    private readonly FinalizationService _finalizer;

    public ImmichRunTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Savings = new SavingsSettings { MinimumBytesSaved = "0B" },
            Immich = new ImmichSettings { BaseUrl = "http://immich.test:2283", StagingDirectory = Path.Combine(_directory, "staging") },
            Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["hevc"], Resolution = "1080p-1440p", MinimumVideoBitrate = "10Mbps", MinimumFileSize = "5B" }] }
        };
        var fingerprints = new FileFingerprintService();
        var manifests = new OutputManifestStore();
        var processor = new JobProcessor(
            _repository,
            new ReadableFiles(),
            _ => new HevcProbe(),
            new ProcessingSessionFactory(_ => new FixedCrfSearch(), _ => new FakeEncoder(), new UnexpectedExternalProcessRunner()),
            fingerprints,
            manifests,
            new PassingValidator());
        _queue = new QueueService(null!, _repository, fingerprints, processor);
        _run = new ImmichRunService(
            _immich,
            new ImmichReuploadGuard(_immich, _repository),
            new ImmichDiscoveryService(_immich, _repository, _ => new HevcProbe(), fingerprints, () => Now),
            _queue);
        _finalizer = new FinalizationService(_repository, manifests, fingerprints, new SafeFileInstaller(), new ImmichFinalizer(_immich, _repository, manifests));
    }

    [Fact]
    public async Task ARunStopsBeforeTheGuardAndDiscoveryWhileImmichsTrashIsOff()
    {
        _immich.Add("PXL_20260301.mp4", "hevc original");
        _immich.Trash = new ImmichTrashSettings(Enabled: false, Days: 30);

        var call = () => _run.PrepareAsync(_settings.Database.Path, _settings, limit: null);

        (await call.Should().ThrowAsync<ImmichApiException>()).Which.Code.Should().Be(ImmichTrash.DisabledCode);
        (await _repository.ListAsync(_settings.Database.Path, terminal: false)).Should().BeEmpty();
    }

    [Fact]
    public async Task OneRoundConvertsAnOldVideoAndTheNextRoundTrashesTheOriginalThePhoneUploadedAgain()
    {
        var original = _immich.Add("PXL_20260301.mp4", "hevc original");

        var preparation = await _run.PrepareAsync(_settings.Database.Path, _settings, limit: null);
        var processed = await _queue.RunAsync(_settings.Database.Path, _settings, preparation.Queue);

        preparation.Guard.Checked.Should().Be(0);
        // The round is the set its limit chose (until phase A step 7): a video queued meanwhile waits for the next round.
        preparation.Queue.FollowQueue.Should().BeFalse();
        var job = preparation.Discovery.Queued.Should().ContainSingle().Subject;
        processed.ReadyToFinalize.Should().Be(1);
        _immich.Get(original.Id).IsTrashed.Should().BeFalse("nothing touches Immich before finalize");

        var finalized = await _finalizer.FinalizeAsync(_settings.Database.Path, job.Id, _settings);

        finalized.ExitCode.Should().Be(ExitCode.Success, finalized.Message);
        _immich.Get(original.Id).IsTrashed.Should().BeTrue();
        var av1 = _immich.Get((await _repository.GetAsync(_settings.Database.Path, job.Id))!.ImmichNewAssetId!);
        av1.OriginalFileName.Should().Be("PXL_20260301.mp4");
        av1.IsTrashed.Should().BeFalse();
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, original.Id)).Should().BeFalse();

        // Immich purges the trashed original after 30 days; a copy still on the phone then uploads again.
        _immich.Remove(original.Id);
        var reupload = _immich.Add("PXL_20260301.mp4", "hevc original");

        var next = await _run.PrepareAsync(_settings.Database.Path, _settings, limit: null);

        next.Guard.Trashed.Should().ContainSingle().Which.AssetId.Should().Be(reupload.Id);
        _immich.Get(reupload.Id).IsTrashed.Should().BeTrue();
        next.Discovery.Queued.Should().BeEmpty();
        next.Discovery.AlreadyConverted.Should().Be(1, "the AV1 asset is old-dated, so the search returns it");
        next.Queue.RunnableJobs.Should().BeEmpty();
        _immich.Get(av1.Id).IsTrashed.Should().BeFalse();
    }

    [Fact]
    public async Task ALimitProcessesAtMostThatManyOpenJobs()
    {
        _immich.Add("a.mp4", "hevc a");
        _immich.Add("b.mp4", "hevc b");
        _immich.Add("c.mp4", "hevc c");
        await _run.PrepareAsync(_settings.Database.Path, _settings, limit: null);

        var preparation = await _run.PrepareAsync(_settings.Database.Path, _settings, limit: 2);

        preparation.Discovery.Queued.Should().BeEmpty();
        preparation.Queue.RunnableJobs.Should().HaveCount(2);
    }

    [Fact]
    public async Task AFolderOnlyPreparationLeavesImmichJobsAndAnImmichOneLeavesFileJobs()
    {
        _immich.Add("PXL.mp4", "hevc original");
        var immichJob = (await _run.PrepareAsync(_settings.Database.Path, _settings, limit: null)).Discovery.Queued.Single();
        var fileJob = await _repository.CreateAsync(_settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "movie.mp4"), SourceFingerprint = "file", Status = JobStatus.Queued });

        var fileRun = await _queue.PrepareAsync(_settings.Database.Path, _settings, JobSourceKind.File);
        var immichRun = await _queue.PrepareAsync(_settings.Database.Path, _settings, JobSourceKind.Immich);

        fileRun.RunnableJobs.Select(job => job.Id).Should().Equal(fileJob.Id);
        immichRun.RunnableJobs.Select(job => job.Id).Should().Equal(immichJob.Id);
    }

    [Fact]
    public async Task ADiscoveryErrorAfterTheGuardKeepsTheGuardsTrashAndItsReport()
    {
        _immich.Add("PXL.mp4", "hevc original");
        var preparation = await _run.PrepareAsync(_settings.Database.Path, _settings, limit: null);
        await _queue.RunAsync(_settings.Database.Path, _settings, preparation.Queue);
        await _finalizer.FinalizeAsync(_settings.Database.Path, preparation.Discovery.Queued.Single().Id, _settings);
        var original = preparation.Discovery.Queued.Single().ImmichAssetId!;
        _immich.Remove(original);
        _immich.Add("PXL.mp4", "hevc original");
        _immich.Add("later.mp4", "hevc later");
        _immich.FailSearch = new ImmichApiException("ImmichUnavailable", "Immich did not answer.");
        var reports = new List<ScanProgress>();

        var action = () => _run.PrepareAsync(_settings.Database.Path, _settings, limit: null, new SynchronousProgress(reports.Add));

        await action.Should().ThrowAsync<ImmichApiException>();
        reports.Should().ContainSingle(report => report.Stage == "Trashed");
        (await _repository.ListAsync(_settings.Database.Path, terminal: false)).Should().BeEmpty();
    }

    [Fact]
    public async Task AFolderOnlyPreparationReturnsACrashedImmichFinalisationToReadyToo()
    {
        _immich.Add("PXL.mp4", "hevc original");
        var preparation = await _run.PrepareAsync(_settings.Database.Path, _settings, limit: null);
        var job = preparation.Discovery.Queued.Single();
        await _queue.RunAsync(_settings.Database.Path, _settings, preparation.Queue);
        var crashed = (await _repository.GetAsync(_settings.Database.Path, job.Id))!;
        crashed.Status = JobStatus.Finalizing;
        await _repository.UpdateAsync(_settings.Database.Path, crashed);

        var fileRun = await _queue.PrepareAsync(_settings.Database.Path, _settings, JobSourceKind.File);

        fileRun.RunnableJobs.Should().BeEmpty();
        fileRun.FailedJobs.Should().BeEmpty();
        (await _repository.GetAsync(_settings.Database.Path, job.Id))!.Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Immich's asset checksum is SHA-1.")]
    private static string Sha1(string content) => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class SynchronousProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

    private sealed class ReadableFiles : IFileReadinessService
    {
        public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new FileReadinessResult(true, "Readable."));
    }

    /// <summary>Every original is HEVC; an encoder output is AV1.</summary>
    private sealed class HevcProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaInfo(path.EndsWith(".encoding.mp4", StringComparison.OrdinalIgnoreCase) ? "av1" : "hevc", 1, 1, 0, 0, 60, new FileInfo(path).Length, 1920, 1080, 20_000_000));
    }

    private sealed class FixedCrfSearch : ICrfSearchClient
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, QualitySettings settings) => [];
        public Task<CrfSearchResult> SearchAsync(string inputPath, QualitySettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new CrfSearchResult(42, string.Empty, string.Empty, TimeSpan.Zero));
    }

    private sealed class FakeEncoder : IVideoEncoder
    {
        public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams) => [];

        public async Task<EncodeResult> EncodeAsync(string inputPath, string outputPath, int crf, QualitySettings settings, IReadOnlyList<int> flacAudioStreams, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(outputPath, $"av1 of {await File.ReadAllTextAsync(inputPath, cancellationToken)}", cancellationToken);
            return new EncodeResult(outputPath, TimeSpan.Zero);
        }
    }

    private sealed class PassingValidator : IOutputValidationService
    {
        public Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationReport { Passed = true, SourceSizeBytes = 400_000_000, OutputSizeBytes = 100_000_000, PercentageSaved = 75, ValidatedUtc = DateTimeOffset.UtcNow });
    }

    private sealed class UnexpectedExternalProcessRunner : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Processing is local in these tests.");
    }

    /// <summary>
    /// Immich in memory, as the Immich source sees it: the search leaves out trashed assets as the real filter does, an upload
    /// answers as a duplicate of any asset holding the same bytes, and the upload check names an asset holding a checksum.
    /// </summary>
    private sealed class FakeImmich : IImmichClientFactory, IImmichClient
    {
        private readonly List<ImmichAsset> _assets = [];
        private readonly Dictionary<string, string> _contents = [];

        public Exception? FailSearch { get; set; }

        public ImmichAsset Add(string fileName, string content)
        {
            var asset = new ImmichAsset(
                Guid.NewGuid().ToString(), Sha1(content), fileName, ImmichValues.Video, ImmichValues.Timeline,
                IsFavorite: false, IsTrashed: false, LibraryId: null, LivePhotoVideoId: null,
                Old, Old, Old, 60_000, new ImmichExif(Encoding.UTF8.GetByteCount(content), Old, null, null, null, null, "Google", "Pixel 7"), []);
            _assets.Add(asset);
            _contents[asset.Id] = content;
            return asset;
        }

        public ImmichAsset Get(string assetId) => _assets.Single(asset => asset.Id == assetId);

        public void Remove(string assetId) => _assets.RemoveAll(asset => asset.Id == assetId);

        public IImmichClient Create(ImmichSettings settings) => this;

        public async IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (FailSearch is { } failure) throw failure;
            foreach (var asset in _assets.Where(asset => !asset.IsTrashed && asset.FileCreatedAt < takenBefore).ToArray())
            {
                await Task.Yield();
                yield return asset;
            }
        }

        public Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) =>
            _assets.SingleOrDefault(asset => asset.Id == assetId) is { } asset
                ? Task.FromResult(asset)
                : Task.FromException<ImmichAsset>(new ImmichApiException("ImmichRequestFailed", $"Immich returned HTTP 404 for GET /api/assets/{assetId}."));

        public Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
            File.WriteAllTextAsync(destinationPath, _contents[assetId], cancellationToken);

        public async Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            var content = await File.ReadAllTextAsync(upload.FilePath, cancellationToken);
            if (_assets.FirstOrDefault(asset => asset.Checksum == Sha1(content)) is { } existing) return new ImmichUploadResult(existing.Id, IsDuplicate: true);
            return new ImmichUploadResult(Add(upload.FileName, content).Id, IsDuplicate: false);
        }

        public Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
        {
            foreach (var id in assetIds) _assets[_assets.FindIndex(asset => asset.Id == id)] = Get(id) with { IsTrashed = true };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImmichUploadCheck>>(checksums.Select(checksum =>
                _assets.FirstOrDefault(asset => asset.Checksum == checksum) is { } match
                    ? new ImmichUploadCheck(checksum, Rejected: true, "duplicate", match.Id, match.IsTrashed)
                    : new ImmichUploadCheck(checksum, Rejected: false, null, null, IsTrashed: false)).ToArray());

        public Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ImmichTrashSettings Trash { get; set; } = new(Enabled: true, Days: 30);
        public Task<ImmichTrashSettings> GetTrashSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Trash);
        public Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
