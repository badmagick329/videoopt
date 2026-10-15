using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Features.Finalize;
using VideoOptimiser.Infrastructure.Features.Run;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests.Features.ImmichRun;

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
    private readonly InMemoryImmich _immich = new(Old) { FiltersSearch = true };
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
            Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "10Mbps", MinimumFileSize = "5B" }] }
        };
        var fingerprints = new FileFingerprintService();
        var manifests = new OutputManifestStore();
        var processor = new JobProcessor(
            _repository,
            new ReadableFiles(),
            _ => new H264Probe(bitrate: 20_000_000),
            new ProcessingSessionFactory(_ => new FixedCrfSearch(), _ => new FakeEncoder(), new UnexpectedExternalProcessRunner()),
            fingerprints,
            manifests,
            new PassingValidator(),
            new AbAv1ProgressTranslator());
        _queue = new QueueService(_repository, processor, new DatabaseLocks());
        _run = new ImmichRunService(
            _immich,
            new ImmichReuploadGuard(_immich, _repository),
            new ImmichDiscoveryService(_immich, _repository, _ => new H264Probe(bitrate: 20_000_000), fingerprints, new ImmichRestaging(_immich, _repository, () => Now), () => Now),
            _queue);
        _finalizer = new FinalizationService(_repository, manifests, fingerprints, new SafeFileInstaller(), new ImmichFinalizer(_immich, _repository, manifests));
    }

    [Fact]
    public async Task ARunStopsBeforeTheGuardAndDiscoveryWhileImmichsTrashIsOff()
    {
        _immich.Add("PXL_20260301.mp4", "original");
        _immich.Trash = new ImmichTrashSettings(Enabled: false, Days: 30);

        var call = () => _run.PrepareAsync(_settings.Database.Path, _settings, limit: null);

        (await call.Should().ThrowAsync<ImmichApiException>()).Which.Code.Should().Be(ImmichTrash.DisabledCode);
        (await _repository.ListAsync(_settings.Database.Path, terminal: false)).Should().BeEmpty();
    }

    [Fact]
    public async Task OneRoundConvertsAnOldVideoAndTheNextRoundTrashesTheOriginalThePhoneUploadedAgain()
    {
        var original = _immich.Add("PXL_20260301.mp4", "original");

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
        av1.IsTrashed.Should().BeFalse();

        // Immich purges the trashed original after 30 days; a copy still on the phone then uploads again.
        _immich.Remove(original.Id);
        var reupload = _immich.Add("PXL_20260301.mp4", "original");

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
        _immich.Add("a.mp4", "video a");
        _immich.Add("b.mp4", "video b");
        _immich.Add("c.mp4", "video c");
        await _run.PrepareAsync(_settings.Database.Path, _settings, limit: null);

        var preparation = await _run.PrepareAsync(_settings.Database.Path, _settings, limit: 2);

        preparation.Discovery.Queued.Should().BeEmpty();
        preparation.Queue.RunnableJobs.Should().HaveCount(2);
    }

    [Fact]
    public async Task AFolderOnlyPreparationLeavesImmichJobsAndAnImmichOneLeavesFileJobs()
    {
        var immichJob = await SeedImmichJobAsync(JobStatus.Queued);
        var fileJob = await _repository.CreateAsync(_settings.Database.Path, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, "movie.mp4"), SourceFingerprint = "file", Status = JobStatus.Queued });

        var fileRun = await _queue.PrepareAsync(_settings.Database.Path, _settings, JobSourceKind.File);
        var immichRun = await _queue.PrepareAsync(_settings.Database.Path, _settings, JobSourceKind.Immich);

        fileRun.RunnableJobs.Select(job => job.Id).Should().Equal(fileJob.Id);
        immichRun.RunnableJobs.Select(job => job.Id).Should().Equal(immichJob.Id);
    }

    [Fact]
    public async Task ADiscoveryErrorAfterTheGuardKeepsTheGuardsTrashAndItsReport()
    {
        await SeedImmichJobAsync(JobStatus.Completed);
        _immich.Add("PXL.mp4", "original");
        _immich.FailSearch = new ImmichApiException("ImmichUnavailable", "Immich did not answer.");
        var reports = new List<ScanProgress>();

        var action = () => _run.PrepareAsync(_settings.Database.Path, _settings, limit: null, new SynchronousProgress<ScanProgress>(reports.Add));

        await action.Should().ThrowAsync<ImmichApiException>();
        reports.Should().ContainSingle(report => report.Stage == "Trashed");
        (await _repository.ListAsync(_settings.Database.Path, terminal: false)).Should().BeEmpty();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>An Immich job for the content "original"; a completed one has its converted asset, which is what the guard watches for.</summary>
    private Task<JobRecord> SeedImmichJobAsync(JobStatus status)
    {
        var assetId = Guid.NewGuid().ToString();
        var completed = status == JobStatus.Completed;
        return _repository.CreateAsync(_settings.Database.Path, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = Path.Combine(_settings.Immich.StagingDirectory, assetId, "PXL.mp4"),
            SourceFingerprint = "fingerprint",
            Status = status,
            CompletedUtc = completed ? DateTimeOffset.UtcNow : null,
            SourceKind = JobSourceKind.Immich,
            ImmichAssetId = assetId,
            ImmichChecksum = InMemoryImmich.Checksum("original"),
            ImmichNewAssetId = completed ? Guid.NewGuid().ToString() : null
        });
    }
}
