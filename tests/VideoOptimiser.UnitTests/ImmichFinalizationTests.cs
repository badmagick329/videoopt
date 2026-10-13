using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Immich;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class ImmichFinalizationTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2025, 5, 1, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Modified = new(2025, 5, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly OutputManifestStore _manifests = new();
    private readonly FileFingerprintService _fingerprints = new();
    private readonly FakeImmich _immich = new();
    private readonly AppSettings _settings;
    private readonly FinalizationService _finalizer;

    public ImmichFinalizationTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Immich = new ImmichSettings { BaseUrl = "http://immich.test:2283", StagingDirectory = Path.Combine(_directory, "staging") }
        };
        _finalizer = new FinalizationService(_repository, _manifests, _fingerprints, new SafeFileInstaller(), new ImmichFinalizer(_immich, _repository, _manifests));
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 0)]
    public async Task RefusesBeforeTheUploadWhileImmichsTrashWouldPurgeAndLeavesTheJobReady(bool enabled, int days)
    {
        var original = _immich.Add("PXL_20250501.MOV", "original");
        var job = await ReadyJobAsync(original);
        _immich.Trash = new ImmichTrashSettings(enabled, days);

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        _immich.Calls.Should().BeEmpty();
        _immich.Get(original.Id).IsTrashed.Should().BeFalse();
        var stored = await JobAsync(job);
        stored.Status.Should().Be(JobStatus.ReadyToFinalize);
        stored.FailureMessage.Should().Be(ImmichTrash.DisabledMessage);
    }

    [Fact]
    public async Task UploadsTheAv1CopiesEverythingImmichKeepsThenTrashesTheOriginalAndDeletesTheStagingFolder()
    {
        var original = _immich.Add("PXL_20250501.MOV", "original", favourite: true, description: "Beach", rating: 4, tags: ["tag-1", "tag-2"]);
        var job = await ReadyJobAsync(original);

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        var replacement = _immich.Uploads.Should().ContainSingle().Subject;
        _immich.Calls.Should().Equal(
            $"get {original.Id}",
            "upload",
            $"update {replacement.Id}",
            $"copy {original.Id} {replacement.Id}",
            $"tag {replacement.Id} tag-1,tag-2",
            $"trash {original.Id}");
        replacement.Upload.Should().Be(new ImmichUpload(job.OutputPath!, "PXL_20250501.MP4", Created, Modified, IsFavorite: true));
        _immich.Updates.Should().ContainSingle().Which.Should().Be(new ImmichMetadataUpdate(Description: "Beach", Rating: 4));
        _immich.Get(original.Id).IsTrashed.Should().BeTrue();

        var stored = await JobAsync(job);
        stored.Status.Should().Be(JobStatus.Completed);
        stored.ImmichNewAssetId.Should().Be(replacement.Id);
        stored.FailureMessage.Should().BeNull();
        result.Message.Should().Be($"New asset {replacement.Id}; the original is in Immich's trash.");
        Directory.Exists(StagingFolder(original)).Should().BeFalse();
        Directory.Exists(_settings.Immich.StagingDirectory).Should().BeTrue();
    }

    [Theory]
    [InlineData("IMG_0897.MOV", "IMG_0897.MP4")]
    [InlineData("IMG_0897.mov", "IMG_0897.mp4")]
    [InlineData("clip.m4v", "clip.mp4")]
    [InlineData("clip.avi", "clip.mkv")]
    public async Task TheUploadTakesTheContainerTheEncodeWroteNotTheOriginalsExtension(string originalName, string uploadName)
    {
        var original = _immich.Add(originalName, "original");
        var job = await ReadyJobAsync(original);

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        _immich.Uploads.Should().ContainSingle().Which.Upload.FileName.Should().Be(uploadName);
    }

    [Fact]
    public async Task SkipsTheMetadataAndTagCallsWhenTheOriginalHasNothingToCopy()
    {
        var original = _immich.Add("PXL.mp4", "original");
        var job = await ReadyJobAsync(original);

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        var replacement = _immich.Uploads.Single().Id;
        _immich.Calls.Should().Equal($"get {original.Id}", "upload", $"copy {original.Id} {replacement}", $"trash {original.Id}");
    }

    public static TheoryData<string, bool> Steps()
    {
        var steps = new TheoryData<string, bool>();
        foreach (var step in new[] { "get", "upload", "update", "copy", "tag", "trash" })
        {
            steps.Add(step, false);
            steps.Add(step, true);
        }

        return steps;
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task AFailureAtAnyStepLeavesTheJobResumableAndTheNextFinalizeCompletesWithOneAv1(string step, bool responseLost)
    {
        var original = _immich.Add("PXL.mp4", "original", description: "Beach", tags: ["tag-1"]);
        var job = await ReadyJobAsync(original);
        // A lost response means Immich applied the call but the answer never arrived.
        (responseLost ? _immich.LoseResponseOnce : _immich.FailOnce)[step] = new ImmichApiException("ImmichUnavailable", $"Immich did not answer during {step}.");

        var failed = await FinalizeAsync(job);

        failed.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        failed.Message.Should().Contain($"during {step}").And.Contain("Run finalize again");
        _immich.Get(original.Id).IsTrashed.Should().Be(step == "trash" && responseLost);
        var waiting = await JobAsync(job);
        waiting.Status.Should().Be(JobStatus.ReadyToFinalize);
        waiting.FailureCategory.Should().Be(FinalizationService.IncompleteCategory);
        Directory.Exists(StagingFolder(original)).Should().BeTrue();

        var resumed = await FinalizeAsync(job);

        resumed.ExitCode.Should().Be(ExitCode.Success, resumed.Message);
        var replacement = _immich.Uploads.Should().ContainSingle().Subject.Id;
        _immich.Get(original.Id).IsTrashed.Should().BeTrue();
        // An original already trashed by the lost call is not trashed again.
        string[] lastCalls = step == "trash" && responseLost
            ? [$"copy {original.Id} {replacement}", $"tag {replacement} tag-1"]
            : [$"copy {original.Id} {replacement}", $"tag {replacement} tag-1", $"trash {original.Id}"];
        _immich.Calls.Should().EndWith(lastCalls);
        var completed = await JobAsync(job);
        completed.Status.Should().Be(JobStatus.Completed);
        completed.ImmichNewAssetId.Should().Be(replacement);
    }

    [Fact]
    public async Task AJobACrashLeftMidFinalisationIsReadyAgainAfterQueuePreparationAndFinalizeResumesIt()
    {
        var original = _immich.Add("PXL.mp4", "original");
        var job = await ReadyJobAsync(original);
        job.Status = JobStatus.Finalizing;
        job.ImmichNewAssetId = _immich.Add("PXL.mp4", "av1").Id;
        await _repository.UpdateAsync(_settings.Database.Path, job);
        // Preparation marks the crashed job interrupted first, as at the start of every queue run.
        var queue = new QueueService(null!, _repository, _fingerprints, null!);

        var preparation = await queue.PrepareAsync(_settings.Database.Path, _settings, JobSourceKind.Immich);

        preparation.RunnableJobs.Should().BeEmpty();
        preparation.FailedJobs.Should().BeEmpty();
        var ready = await JobAsync(job);
        ready.Status.Should().Be(JobStatus.ReadyToFinalize);
        ready.FailureMessage.Should().Be(FinalizationService.InterruptedMessage);

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        _immich.Uploads.Should().BeEmpty();
        _immich.Calls.Should().Contain($"copy {original.Id} {job.ImmichNewAssetId}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizeResumesAJobACrashLeftMidFinalisationWithoutWaitingForQueueRun(bool markedInterrupted)
    {
        var original = _immich.Add("PXL.mp4", "original");
        var job = await ReadyJobAsync(original);
        job.Status = JobStatus.Finalizing;
        await _repository.UpdateAsync(_settings.Database.Path, job);
        if (markedInterrupted) await _repository.MarkActiveJobsInterruptedAsync(_settings.Database.Path);

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        _immich.Get(original.Id).IsTrashed.Should().BeTrue();
    }

    [Fact]
    public async Task SavesTheUploadedAssetIdBeforeCopyingAnything()
    {
        var original = _immich.Add("PXL.mp4", "original");
        var job = await ReadyJobAsync(original);
        _immich.FailOnce["copy"] = new ImmichApiException("ImmichRequestFailed", "Immich answered 500.");

        await FinalizeAsync(job);

        (await JobAsync(job)).ImmichNewAssetId.Should().Be(_immich.Uploads.Single().Id);
    }

    [Fact]
    public async Task ResumingAfterTheAv1VanishedUploadsItAgain()
    {
        var original = _immich.Add("PXL.mp4", "original");
        var job = await ReadyJobAsync(original);
        _immich.FailOnce["copy"] = new ImmichApiException("ImmichRequestFailed", "Immich answered 500.");
        await FinalizeAsync(job);
        var vanished = _immich.Uploads.Single().Id;
        _immich.Remove(vanished);

        var resumed = await FinalizeAsync(job);

        resumed.ExitCode.Should().Be(ExitCode.Success, resumed.Message);
        _immich.Uploads.Should().HaveCount(2);
        (await JobAsync(job)).ImmichNewAssetId.Should().Be(_immich.Uploads[1].Id).And.NotBe(vanished);
    }

    [Fact]
    public async Task ADuplicateUploadContinuesWithTheAssetImmichAlreadyHolds()
    {
        var original = _immich.Add("PXL.mp4", "original");
        var existing = _immich.Add("PXL.mp4", "av1");
        var job = await ReadyJobAsync(original);

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        _immich.Calls.Should().Equal($"get {original.Id}", "upload", $"get {existing.Id}", $"copy {original.Id} {existing.Id}", $"trash {original.Id}");
        (await JobAsync(job)).ImmichNewAssetId.Should().Be(existing.Id);
    }

    [Fact]
    public async Task ADuplicateOfATrashedAssetStopsBeforeTheOriginalIsTouched()
    {
        var original = _immich.Add("PXL.mp4", "original");
        var trashed = _immich.Add("PXL.mp4", "av1", isTrashed: true);
        var job = await ReadyJobAsync(original);

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        result.Message.Should().Contain(trashed.Id).And.Contain("trash");
        _immich.Calls.Should().NotContain(call => call.StartsWith("copy") || call.StartsWith("trash"));
        (await JobAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    [Fact]
    public async Task AnOriginalThatChangedInImmichFailsTheJobBeforeUploading()
    {
        var original = _immich.Add("PXL.mp4", "original");
        var job = await ReadyJobAsync(original);
        _immich.Replace(original with { Checksum = FakeImmich.Checksum("edited") });

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        result.Message.Should().StartWith("Original changed in Immich");
        _immich.Calls.Should().Equal($"get {original.Id}");
        var failed = await JobAsync(job);
        failed.Status.Should().Be(JobStatus.Failed);
        failed.FailureCategory.Should().Be("FinalizationFailed");
    }

    [Fact]
    public async Task AStagedOriginalThatChangedAfterEncodingFailsTheJobWithoutCallingImmich()
    {
        var original = _immich.Add("PXL.mp4", "original");
        var job = await ReadyJobAsync(original);
        await File.WriteAllTextAsync(job.SourcePath, "changed");

        var result = await FinalizeAsync(job);

        result.Message.Should().Be("Original changed after encoding.");
        _immich.Calls.Should().BeEmpty();
        (await JobAsync(job)).Status.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public async Task AnOriginalAlreadyTrashedAfterTheUploadIsCopiedFromAgainButNotTrashedAgain()
    {
        // An earlier run trashed the original, then stopped before marking the job completed.
        var original = _immich.Add("PXL.mp4", "original");
        var job = await ReadyJobAsync(original);
        _immich.FailOnce["trash"] = new OperationCanceledException();
        await FluentActions.Awaiting(() => FinalizeAsync(job)).Should().ThrowAsync<OperationCanceledException>();
        _immich.Replace(_immich.Get(original.Id) with { IsTrashed = true });
        _immich.Calls.Clear();

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        var replacement = _immich.Uploads.Single().Id;
        _immich.Calls.Should().Equal($"get {original.Id}", $"get {replacement}", $"copy {original.Id} {replacement}");
        (await JobAsync(job)).Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task AnOriginalTrashedBeforeAnyUploadStopsWithoutUploading()
    {
        var original = _immich.Add("PXL.mp4", "original", isTrashed: true);
        var job = await ReadyJobAsync(original);

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.FinalisationFailure);
        result.Message.Should().Contain("Restore it");
        _immich.Uploads.Should().BeEmpty();
        (await JobAsync(job)).Status.Should().Be(JobStatus.ReadyToFinalize);
    }

    [Fact]
    public async Task AnOriginalOutsideItsStagingFolderIsLeftInPlaceWhileTheOutputIsDeleted()
    {
        var original = _immich.Add("PXL.mp4", "original");
        var job = await ReadyJobAsync(original, Path.Combine(_directory, "elsewhere"));

        var result = await FinalizeAsync(job);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        File.Exists(job.SourcePath).Should().BeTrue();
        File.Exists(job.OutputPath).Should().BeFalse();
        File.Exists(_manifests.GetPath(job.OutputPath!)).Should().BeFalse();
    }

    [Fact]
    public async Task CancellingKeepsTheUploadedAssetIdAndReturnsTheJobToReady()
    {
        var original = _immich.Add("PXL.mp4", "original");
        var job = await ReadyJobAsync(original);
        _immich.FailOnce["copy"] = new OperationCanceledException();

        await FluentActions.Awaiting(() => FinalizeAsync(job)).Should().ThrowAsync<OperationCanceledException>();

        var interrupted = await JobAsync(job);
        interrupted.Status.Should().Be(JobStatus.ReadyToFinalize);
        interrupted.ImmichNewAssetId.Should().Be(_immich.Uploads.Single().Id);
        _immich.Get(original.Id).IsTrashed.Should().BeFalse();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Task<FinalizationResult> FinalizeAsync(JobRecord job) => _finalizer.FinalizeAsync(_settings.Database.Path, job.Id, _settings);

    private async Task<JobRecord> JobAsync(JobRecord job) => (await _repository.GetAsync(_settings.Database.Path, job.Id))!;

    private string StagingFolder(ImmichAsset asset) => Path.Combine(_settings.Immich.StagingDirectory, asset.Id);

    /// <summary>A validated job laid out as discovery and the processor leave it: staged original, output and manifest in the asset's folder.</summary>
    private async Task<JobRecord> ReadyJobAsync(ImmichAsset original, string? folder = null)
    {
        var id = Guid.NewGuid();
        folder ??= StagingFolder(original);
        var sourcePath = Path.Combine(folder, original.OriginalFileName);
        var outputPath = Path.Combine(folder, ".video-optimiser", $"{Path.GetFileNameWithoutExtension(original.OriginalFileName)}.{id:N}.1.encoding{OutputContainer.ExtensionFor(original.OriginalFileName)}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(sourcePath, "original");
        await File.WriteAllTextAsync(outputPath, "av1");
        var fingerprint = await _fingerprints.CreateAsync(sourcePath);
        await _manifests.SaveAsync(new OutputManifest
        {
            SourcePath = sourcePath,
            SourceFingerprint = fingerprint,
            OutputPath = outputPath,
            Crf = 30,
            CreatedUtc = DateTimeOffset.UtcNow,
            Validation = new ValidationReport { Passed = true }
        });
        return await _repository.CreateAsync(_settings.Database.Path, new JobRecord
        {
            Id = id,
            SourcePath = sourcePath,
            SourceFingerprint = fingerprint,
            SourceKind = JobSourceKind.Immich,
            ImmichAssetId = original.Id,
            ImmichChecksum = original.Checksum,
            Status = JobStatus.ReadyToFinalize,
            Attempt = 1,
            OutputPath = outputPath
        });
    }

    private sealed record Uploaded(string Id, ImmichUpload Upload);

    /// <summary>
    /// Immich in memory. <see cref="Calls"/> records every call in order, by step name and asset IDs. <see cref="FailOnce"/>
    /// fails the next call of a step (get, upload, update, copy, tag, trash) with the given exception before it has any
    /// effect; <see cref="LoseResponseOnce"/> applies it and then fails. An upload answers as a duplicate of any asset that
    /// already holds the file's bytes, trashed or not, as Immich does.
    /// </summary>
    private sealed class FakeImmich : IImmichClientFactory, IImmichClient
    {
        private readonly List<ImmichAsset> _assets = [];

        public List<string> Calls { get; } = [];
        public List<Uploaded> Uploads { get; } = [];
        public List<ImmichMetadataUpdate> Updates { get; } = [];
        public Dictionary<string, Exception> FailOnce { get; } = [];
        public Dictionary<string, Exception> LoseResponseOnce { get; } = [];

        public static string Checksum(string content) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));

        public ImmichAsset Add(string fileName, string content, bool isTrashed = false, bool favourite = false, string? description = null, int? rating = null, string[]? tags = null)
        {
            var asset = new ImmichAsset(
                Guid.NewGuid().ToString(), Checksum(content), fileName, ImmichValues.Video, ImmichValues.Timeline,
                IsFavorite: favourite, IsTrashed: isTrashed, LibraryId: null, LivePhotoVideoId: null,
                Created, Modified, Created, 60_000, new ImmichExif(100, Created, null, null, description, rating, "Google", "Pixel 7"), tags ?? []);
            _assets.Add(asset);
            return asset;
        }

        public void Replace(ImmichAsset asset) => _assets[_assets.FindIndex(existing => existing.Id == asset.Id)] = asset;

        public void Remove(string assetId) => _assets.RemoveAll(asset => asset.Id == assetId);

        public ImmichAsset Get(string assetId) => _assets.Single(asset => asset.Id == assetId);

        public IImmichClient Create(ImmichSettings settings) => this;

        public Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) => Step("get", $"get {assetId}", () =>
            _assets.SingleOrDefault(asset => asset.Id == assetId) ?? throw new ImmichApiException("ImmichRequestFailed", $"Immich returned HTTP 404 for GET /api/assets/{assetId}."));

        public Task<ImmichUploadResult> UploadAsync(ImmichUpload upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => Step("upload", "upload", () =>
        {
            var content = File.ReadAllText(upload.FilePath);
            if (_assets.FirstOrDefault(asset => asset.Checksum == Checksum(content)) is { } existing) return new ImmichUploadResult(existing.Id, IsDuplicate: true);
            var asset = Add(upload.FileName, content);
            Uploads.Add(new Uploaded(asset.Id, upload));
            return new ImmichUploadResult(asset.Id, IsDuplicate: false);
        });

        public Task UpdateMetadataAsync(string assetId, ImmichMetadataUpdate update, CancellationToken cancellationToken = default) => Step("update", $"update {assetId}", () =>
        {
            Updates.Add(update);
            return true;
        });

        public Task CopyRelationsAsync(string sourceAssetId, string targetAssetId, CancellationToken cancellationToken = default) =>
            Step("copy", $"copy {sourceAssetId} {targetAssetId}", () => true);

        // The real client sends nothing for an empty list.
        public Task TagAssetAsync(string assetId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default) =>
            tagIds.Count == 0 ? Task.CompletedTask : Step("tag", $"tag {assetId} {string.Join(',', tagIds)}", () => true);

        public Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default) => Step("trash", $"trash {string.Join(',', assetIds)}", () =>
        {
            foreach (var id in assetIds) Replace(Get(id) with { IsTrashed = true });
            return true;
        });

        public Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ImmichTrashSettings Trash { get; set; } = new(Enabled: true, Days: 30);
        public Task<ImmichTrashSettings> GetTrashSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Trash);
        public Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ImmichAsset> SearchVideosAsync(DateTimeOffset takenBefore, bool includeFavorites, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadOriginalAsync(string assetId, string destinationPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImmichUploadCheck>> CheckUploadsAsync(IReadOnlyList<string> checksums, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private Task<T> Step<T>(string step, string call, Func<T> action)
        {
            if (FailOnce.Remove(step, out var failure)) return Task.FromException<T>(failure);
            Calls.Add(call);
            try
            {
                var result = action();
                return LoseResponseOnce.Remove(step, out var lost) ? Task.FromException<T>(lost) : Task.FromResult(result);
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
        }
    }
}
