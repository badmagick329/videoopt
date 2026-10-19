using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;

namespace VideoOptimiser.UnitTests.Features.ImmichRun;

public sealed class ImmichDiscoveryServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = Now.AddDays(-200);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly InMemoryImmich _immich = new(Old);
    private readonly AppSettings _settings;

    public ImmichDiscoveryServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new AppSettings
        {
            Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
            Savings = new SavingsSettings { MinimumBytesSaved = "0B" },
            Immich = new ImmichSettings { BaseUrl = "http://immich.test:2283", StagingDirectory = Path.Combine(_directory, "staging") },
            Eligibility = new EligibilitySettings { Rules = [HevcRule()] }
        };
    }

    [Fact]
    public async Task QueuesAnEligibleVideoAsAnImmichJobUnderItsStagingFolder()
    {
        var asset = _immich.Add("PXL_20250101_120000000.mp4", "hevc video");

        var result = await DiscoverAsync();

        var job = result.Queued.Should().ContainSingle().Subject;
        job.SourceKind.Should().Be(JobSourceKind.Immich);
        job.ImmichAssetId.Should().Be(asset.Id);
        job.ImmichChecksum.Should().Be(asset.Checksum);
        job.SourcePath.Should().Be(Path.Combine(_settings.Immich.StagingDirectory, asset.Id, "PXL_20250101_120000000.mp4"));
        job.ExecutionMode.Should().Be(ProcessingModes.Local);
        (await File.ReadAllTextAsync(job.SourcePath)).Should().Be("hevc video");
        var stored = (await _repository.GetAsync(_settings.Database.Path, job.Id))!;
        stored.Status.Should().Be(JobStatus.Queued);
        stored.VideoFacts.Should().Be(new VideoFacts("hevc", 1920, 1080, 20_000_000, 60, "hevc video".Length));
        _immich.SearchedBefore.Should().Be(Now.AddDays(-90));
        _immich.SearchedWithFavorites.Should().BeFalse();
    }

    [Theory]
    [InlineData("image", "is a IMAGE, not a video")]
    [InlineData("too new", "is newer than the age gate")]
    [InlineData("favourite", "is a favourite")]
    [InlineData("trashed", "is in the trash")]
    [InlineData("hidden", "has visibility hidden")]
    [InlineData("external library", "belongs to an external library")]
    public async Task ReChecksEverySearchResultAndLeavesMismatchesAlone(string mismatch, string reason)
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Replace(mismatch switch
        {
            "image" => asset with { Type = "IMAGE" },
            "too new" => asset with { FileCreatedAt = Now.AddDays(-89) },
            "favourite" => asset with { IsFavorite = true },
            "trashed" => asset with { IsTrashed = true },
            "hidden" => asset with { Visibility = "hidden" },
            _ => asset with { LibraryId = Guid.NewGuid().ToString() }
        });

        var events = new List<ImmichProgress>();

        var result = await DiscoverAsync(progress: new SynchronousProgress<ImmichProgress>(events.Add));

        result.Excluded.Should().Be(1);
        result.Queued.Should().BeEmpty();
        _immich.Downloads.Should().BeEmpty();
        events.Should().Contain(new ImmichDiscoveryProgress.AssetExcluded("clip.mp4", asset.Id, reason));
    }

    [Fact]
    public async Task QueuesAFavouriteWhenSkipFavoritesIsOff()
    {
        _settings.Immich.SkipFavorites = false;
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Replace(asset with { IsFavorite = true, Visibility = ImmichValues.Archive });

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
        _immich.SearchedWithFavorites.Should().BeTrue();
    }

    [Fact]
    public async Task LeavesAssetsWithOpenOrCompletedJobsWithoutDownloadingThem()
    {
        var open = _immich.Add("open.mp4", "hevc open");
        var converted = _immich.Add("converted.mp4", "hevc converted");
        var reuploaded = _immich.Add("reuploaded.mp4", "hevc reuploaded");
        await CreateJobAsync(open, JobStatus.Encoding);
        await CreateJobAsync(converted, JobStatus.Completed);
        // The phone uploaded a converted original again: a new asset ID with the old checksum.
        await CreateJobAsync(reuploaded with { Id = Guid.NewGuid().ToString() }, JobStatus.Completed);

        var result = await DiscoverAsync();

        result.AlreadyQueued.Should().Be(1);
        result.AlreadyConverted.Should().Be(2);
        result.Queued.Should().BeEmpty();
        _immich.Downloads.Should().BeEmpty();
    }

    [Theory]
    [InlineData(JobStatus.Finalizing)]
    [InlineData(JobStatus.Failed)]
    public async Task LeavesTheAv1AssetThatFinalisationUploadedAlone(JobStatus status)
    {
        var original = _immich.Add("PXL.mp4", "hevc original");
        var replacement = _immich.Add("PXL.av1.mp4", "hevc replacement");
        _immich.Remove(original.Id);
        var job = await CreateJobAsync(original, status, status == JobStatus.Failed ? "FinalisationFailed" : null);
        job.ImmichNewAssetId = replacement.Id;
        await _repository.UpdateAsync(_settings.Database.Path, job);

        var result = await DiscoverAsync();

        result.AlreadyConverted.Should().Be(1);
        _immich.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task JudgesAnAssetTheSearchReturnsTwiceOnce()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Duplicate(asset);

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
        _immich.Downloads.Should().ContainSingle();
        File.Exists(result.Queued[0].SourcePath).Should().BeTrue();
    }

    [Theory]
    [InlineData(JobStatus.Failed, "EncodeFailed", false)]
    [InlineData(JobStatus.Cancelled, "Cancelled", false)]
    [InlineData(JobStatus.Failed, "SourceNotReady", true)]
    public async Task RequeuesAFailedAssetOnlyWhenTheFailureJudgedItsStateAtRunTime(JobStatus status, string category, bool requeued)
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        await CreateJobAsync(asset, status, category);

        var result = await DiscoverAsync();

        result.Queued.Should().HaveCount(requeued ? 1 : 0);
        result.PreviouslyFailed.Should().Be(requeued ? 0 : 1);
    }

    /// <summary>VOPT-112: the user looked at that AV1 and did not want it, so the asset is not downloaded again, and it is counted as rejected, not as failed. A changed checksum makes it new (<see cref="QueuesASettledAssetAgainWhenItsChecksumChanged"/>).</summary>
    [Fact]
    public async Task PassesOverARejectedAssetWithoutDownloadingItAndCountsItApartFromFailures()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        await CreateJobAsync(asset, JobStatus.Rejected, FailureCategories.Rejected);

        var result = await DiscoverAsync();

        result.PreviouslyRejected.Should().Be(1);
        result.PreviouslyFailed.Should().Be(0);
        result.Queued.Should().BeEmpty();
        _immich.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task JudgesAFailedAssetByItsLatestJob()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        await CreateJobAsync(asset, JobStatus.Failed, "EncodeFailed");
        await CreateJobAsync(asset, JobStatus.Failed, "SourceNotReady");

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("preset", false)]
    public async Task CountsASkippedAssetAsPreviouslySkippedOnlyWhileTheQualityBarIsUnchanged(string change, bool stillSettled)
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        var skipped = await CreateJobAsync(asset, JobStatus.Skipped, "NotWorthConverting");
        skipped.QualityFingerprint = QualityFingerprint.Compute(_settings);
        await _repository.UpdateAsync(_settings.Database.Path, skipped);
        if (change == "preset") _settings.Quality.Preset = 4;

        var result = await DiscoverAsync();

        result.PreviouslySkipped.Should().Be(stillSettled ? 1 : 0);
        result.PreviouslyFailed.Should().Be(0);
        result.Queued.Should().HaveCount(stillSettled ? 0 : 1);
    }

    [Theory]
    [InlineData(JobStatus.Failed, "EncodeFailed")]
    [InlineData(JobStatus.Skipped, "NotWorthConverting")]
    [InlineData(JobStatus.Rejected, "Rejected")]
    public async Task QueuesASettledAssetAgainWhenItsChecksumChanged(JobStatus status, string category)
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        var settled = await CreateJobAsync(asset with { Checksum = "earlier-checksum" }, status, category);
        settled.QualityFingerprint = QualityFingerprint.Compute(_settings);
        await _repository.UpdateAsync(_settings.Database.Path, settled);

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
    }

    [Fact]
    public async Task RecordsAnIneligibleAssetAsASkipAndDeletesItsStagedCopy()
    {
        var asset = _immich.Add("clip.mp4", "h264 video");

        var result = await DiscoverAsync();

        result.Skipped.Should().Be(1);
        result.Queued.Should().BeEmpty();
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, asset.Id)).Should().BeFalse();
        var skip = (await _repository.ListImmichSkipsAsync(_settings.Database.Path))[asset.Id];
        skip.Checksum.Should().Be(asset.Checksum);
        skip.RulesFingerprint.Should().Be(EligibilityEvaluator.RulesFingerprint(_settings.Eligibility));
        skip.Reason.Should().Be("no rule for h264");
        skip.RecordedUtc.Should().Be(Now);
        skip.VideoFacts.Should().Be(new VideoFacts("h264", 1920, 1080, 20_000_000, 60, "h264 video".Length));
    }

    [Fact]
    public async Task AChangedChecksumMakesASkipStale()
    {
        var asset = _immich.Add("clip.mp4", "h264 video");
        await DiscoverAsync();
        _immich.Replace(asset, "hevc edited video");

        var result = await DiscoverAsync();

        result.Queued.Should().ContainSingle();
        _immich.Downloads.Should().HaveCount(2);
    }

    /// <summary>Immich gives no bitrate, so the file size decides: an asset no bigger than the minimum cannot save it.</summary>
    [Fact]
    public async Task RejectsAnAssetTooSmallToSaveTheMinimumWithoutDownloadingIt()
    {
        _settings.Savings.MinimumBytesSaved = "1KiB";
        var atMinimum = _immich.Add("at-minimum.mp4", "hevc video");
        _immich.Replace(atMinimum with { Exif = atMinimum.Exif with { FileSizeInBytes = 1024 } });
        var overMinimum = _immich.Add("over-minimum.mp4", "hevc video");
        _immich.Replace(overMinimum with { Exif = overMinimum.Exif with { FileSizeInBytes = 1025 } });

        var result = await DiscoverAsync();

        result.Ineligible.Should().Be(1);
        _immich.Downloads.Should().Equal(overMinimum.Id);
    }

    [Fact]
    public async Task RejectsTooSmallOrWrongExtensionAssetsWithoutDownloadingThem()
    {
        var small = _immich.Add("small.mp4", "hevc video");
        _immich.Replace(small with { Exif = small.Exif with { FileSizeInBytes = 3 } });
        _immich.Add("clip.avi", "hevc video");
        var unknownSize = _immich.Add("unknown-size.mp4", "hevc video");
        _immich.Replace(unknownSize with { Exif = unknownSize.Exif with { FileSizeInBytes = null } });

        var result = await DiscoverAsync();

        result.Ineligible.Should().Be(2);
        result.Queued.Should().ContainSingle().Which.ImmichAssetId.Should().Be(unknownSize.Id);
        _immich.Downloads.Should().Equal(unknownSize.Id);
    }

    [Fact]
    public async Task ALimitCountsOnlyTheNewVideosItQueues()
    {
        var open = _immich.Add("open.mp4", "hevc open");
        _immich.Add("next1.mp4", "hevc next1");
        _immich.Add("next2.mp4", "hevc next2");
        _immich.Add("next3.mp4", "hevc next3");
        await CreateJobAsync(open, JobStatus.Queued);

        var result = await DiscoverAsync(limit: 2);

        result.Queued.Select(job => Path.GetFileName(job.SourcePath)).Should().Equal("next1.mp4", "next2.mp4");
        _immich.Downloads.Should().HaveCount(2);
    }

    /// <summary>The counts <c>queue add</c> prints cover every open job, also those the search never returns or stops before.</summary>
    [Fact]
    public async Task CountsEveryOpenJobByWhatHappensToItNextWhetherOrNotTheSearchReachesIt()
    {
        var waiting = _immich.Add("waiting.mp4", "hevc waiting");
        var converting = _immich.Add("converting.mp4", "hevc converting");
        var held = _immich.Add("held.mp4", "hevc held");
        var ready = _immich.Add("ready.mp4", "hevc ready");
        var finalising = _immich.Add("finalising.mp4", "hevc finalising");
        var notReturned = _immich.Add("not-returned.mp4", "hevc not returned");
        _immich.Remove(notReturned.Id);
        await CreateJobAsync(waiting, JobStatus.Queued);
        await CreateJobAsync(converting, JobStatus.Encoding);
        await CreateJobAsync(notReturned, JobStatus.Queued);
        (await _repository.SetHeldAsync(_settings.Database.Path, await CreateJobAsync(held, JobStatus.Queued), held: true)).Should().BeTrue();
        await CreateJobAsync(ready, JobStatus.ReadyToFinalize);
        await CreateJobAsync(finalising, JobStatus.Finalizing);

        var result = await DiscoverAsync(limit: 1);

        (result.AlreadyQueued, result.Held, result.ReadyToFinalize).Should().Be((3, 1, 2));
        result.Queued.Should().BeEmpty();
        _immich.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchItsChecksumIsAnIssue()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Replace(asset with { Checksum = InMemoryImmich.Checksum("something else") });

        var result = await DiscoverAsync();

        result.Issues.Should().Be(1);
        result.Queued.Should().BeEmpty();
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, asset.Id)).Should().BeFalse();
        (await _repository.ListImmichSkipsAsync(_settings.Database.Path)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("not-a-guid", "clip.mp4")]
    [InlineData(" id", "clip.mp4")]
    [InlineData(null, "nested/clip.mp4")]
    [InlineData(null, "clip.mp4:stream.mp4")]
    public async Task NeverStagesOutsideTheAssetFolder(string? id, string fileName)
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Replace(asset with { Id = id?.Replace("id", asset.Id, StringComparison.Ordinal) ?? asset.Id, OriginalFileName = fileName });

        var result = await DiscoverAsync();

        result.Issues.Should().Be(1);
        _immich.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnreachableImmichEndsTheRun()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        _immich.Failures[asset.Id] = new ImmichApiException("ImmichUnavailable", "Immich is unreachable.");

        var action = async () => await DiscoverAsync();

        await action.Should().ThrowAsync<ImmichApiException>().Where(exception => exception.Code == "ImmichUnavailable");
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, asset.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task AFileFfprobeCannotReadIsAnIssueAndTheRunGoesOn()
    {
        var unreadable = _immich.Add("unreadable.mp4", "corrupt video");
        _immich.Add("next.mp4", "hevc next");

        var result = await DiscoverAsync();

        result.Issues.Should().Be(1);
        result.Queued.Should().ContainSingle();
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, unreadable.Id)).Should().BeFalse();
        (await _repository.ListImmichSkipsAsync(_settings.Database.Path)).Should().BeEmpty();
    }

    [Fact]
    public async Task AMissingFfprobeEndsTheRunAfterOneDownload()
    {
        var asset = _immich.Add("clip.mp4", "missing-ffprobe video");
        _immich.Add("next.mp4", "hevc next");

        var action = async () => await DiscoverAsync();

        await action.Should().ThrowAsync<System.ComponentModel.Win32Exception>();
        _immich.Downloads.Should().ContainSingle();
        Directory.Exists(Path.Combine(_settings.Immich.StagingDirectory, asset.Id)).Should().BeFalse();
    }

    /// <summary>
    /// R11: an asset queued from another terminal during its download counts as queued, and keeps its staged copy, which that
    /// job reads at the same path.
    /// </summary>
    [Fact]
    public async Task AnAssetQueuedDuringItsDownloadCountsAsQueuedAndKeepsItsCopy()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        var job = await CreateJobAsync(asset, JobStatus.Queued);

        var result = await DiscoverAsync(repository: new BlindToOpenJobs(_repository, seenBeforeTheDownload: false));

        result.AlreadyQueued.Should().Be(1);
        result.Queued.Should().BeEmpty();
        File.Exists(job.SourcePath).Should().BeTrue();
        (await _repository.ListAsync(_settings.Database.Path, terminal: false)).Should().ContainSingle();
    }

    /// <summary>
    /// VOPT-147: an asset queued from another terminal after discovery read the open jobs, but before its download, is not
    /// downloaded. The download would replace that job's staged copy and change its fingerprint.
    /// </summary>
    [Fact]
    public async Task AnAssetQueuedBeforeItsDownloadIsNotDownloadedAndItsCopyIsUntouched()
    {
        var asset = _immich.Add("clip.mp4", "hevc video");
        var job = await CreateJobAsync(asset, JobStatus.Queued);

        var result = await DiscoverAsync(repository: new BlindToOpenJobs(_repository, seenBeforeTheDownload: true));

        result.AlreadyQueued.Should().Be(1);
        result.Queued.Should().BeEmpty();
        _immich.Downloads.Should().BeEmpty();
        (await File.ReadAllTextAsync(job.SourcePath)).Should().Be("staged");
    }

    /// <summary>R7, R10: a held video is parked. It is not downloaded or failed behind the user's back, and its asset is not queued a second time.</summary>
    [Fact]
    public async Task AHeldVideoIsLeftAloneAndItsAssetIsNotQueuedAgain()
    {
        var held = _immich.Add("held.mp4", "hevc held");
        _immich.Add("next1.mp4", "hevc next1");
        var heldJob = await CreateJobAsync(held, JobStatus.Queued, staged: false);
        (await _repository.SetHeldAsync(_settings.Database.Path, heldJob, held: true)).Should().BeTrue();

        var result = await DiscoverAsync(limit: 1);

        result.Held.Should().Be(1);
        result.Queued.Should().ContainSingle().Which.SourcePath.Should().EndWith("next1.mp4");
        _immich.Downloads.Should().NotContain(held.Id);
        var stored = (await _repository.GetAsync(_settings.Database.Path, heldJob.Id))!;
        stored.Status.Should().Be(JobStatus.Queued);
        stored.Held.Should().BeTrue();
    }

    [Fact]
    public async Task AnnouncesEachDownloadBeforeItStartsAndSettlesItWithItsOutcome()
    {
        _immich.Add("notes.txt", "hevc notes");
        _immich.Add("a-h264.mp4", "h264 video");
        var corrupt = _immich.Add("b-bad-checksum.mp4", "hevc video");
        _immich.Replace(corrupt with { Checksum = "not-the-checksum" });
        var gone = _immich.Add("c-gone.mp4", "hevc gone");
        _immich.Failures[gone.Id] = new ImmichApiException("ImmichRequestFailed", "Immich answered 404.");
        _immich.Add("d-good.mp4", "hevc good");
        var events = new List<ImmichProgress>();

        var result = await DiscoverAsync(limit: 3, new SynchronousProgress<ImmichProgress>(events.Add));

        // notes.txt needs no download, so it is counted and never listed.
        result.Ineligible.Should().Be(1);
        events.Should().Equal(
            new ImmichDiscoveryProgress.Started(3, 0, 1),
            new ImmichDiscoveryProgress.CandidateDownloading(1, "a-h264.mp4", Old.DateTime),
            new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Skipped, "no rule for h264", null, 0, new CandidateFacts("h264", 1920, 1080, 20_000_000, 10)),
            new ImmichDiscoveryProgress.CandidateDownloading(2, "b-bad-checksum.mp4", Old.DateTime),
            new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Issue, "the download does not match Immich's checksum", "it will be tried again next run", 0),
            new ImmichDiscoveryProgress.CandidateDownloading(3, "c-gone.mp4", Old.DateTime),
            new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Issue, "Immich answered 404", "it will be tried again next run", 0),
            new ImmichDiscoveryProgress.CandidateDownloading(4, "d-good.mp4", Old.DateTime),
            new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 1, new CandidateFacts("hevc", 1920, 1080, 20_000_000, 9), 1));
    }

    [Fact]
    public async Task TalliesSkipsByTheirReasonAndCountsOnlyJudgedVideosAsChecked()
    {
        _immich.Add("a.mp4", "h264 one");
        _immich.Add("b.mp4", "h264 two");
        _immich.Add("c.mp4", "vp9 three");
        _immich.Add("d.mp4", "hevc four");
        var gone = _immich.Add("e.mp4", "hevc gone");
        _immich.Failures[gone.Id] = new ImmichApiException("ImmichRequestFailed", "Immich answered 404.");

        var result = await DiscoverAsync();

        result.SkipReasons.Should().Equal(new SkipReasonCount("no rule for h264", 2), new SkipReasonCount("no rule for vp9", 1));
        result.Skipped.Should().Be(3);
        result.Checked.Should().Be(4);
        result.Issues.Should().Be(1);
    }

    [Fact]
    public async Task ReportsTheFactsOfEachJudgedVideoAndTheRuleItFitsOrTheRuleItMissed()
    {
        // The same size can fail the h264 rule and fit the hevc one; the events must carry what tells the two apart.
        _settings.Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "10Mbps", MinimumFileSize = "20B" }, HevcRule()] };
        _immich.Add("a-h264.mp4", "h264 tiny");
        _immich.Add("b-hevc.mp4", "hevc small");
        var events = new List<ImmichProgress>();

        await DiscoverAsync(progress: new SynchronousProgress<ImmichProgress>(events.Add));

        events.OfType<ImmichDiscoveryProgress.Started>().Single().RuleCount.Should().Be(2);
        var judged = events.OfType<ImmichDiscoveryProgress.CandidateJudged>().ToArray();
        judged[0].Should().Be(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Skipped, "rule 1 needs 20B", null, 0, new CandidateFacts("h264", 1920, 1080, 20_000_000, 9)));
        judged[1].Should().Be(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 1, new CandidateFacts("hevc", 1920, 1080, 20_000_000, 10), 2));
    }

    [Fact]
    public async Task StartsByCountingTheSkipsRecordedUnderOtherRules()
    {
        var stale = _immich.Add("stale.mp4", "h264 stale");
        var current = _immich.Add("current.mp4", "h264 current");
        await _repository.PutImmichSkipAsync(_settings.Database.Path, new ImmichSkipRecord(stale.Id, stale.Checksum, "an-older-rules-fingerprint", "codec h264", Now));
        await _repository.PutImmichSkipAsync(_settings.Database.Path, new ImmichSkipRecord(current.Id, current.Checksum, EligibilityEvaluator.RulesFingerprint(_settings.Eligibility), "codec h264", Now));
        var events = new List<ImmichProgress>();

        await DiscoverAsync(progress: new SynchronousProgress<ImmichProgress>(events.Add));

        events.First().Should().Be(new ImmichDiscoveryProgress.Started(null, 1, 1));
        events.OfType<ImmichDiscoveryProgress.CandidateDownloading>().Select(candidate => candidate.FileName).Should().Equal("stale.mp4");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStaleSkipRejectedWithoutADownloadKeepsItsFactsOnlyWhileTheChecksumIsUnchanged(bool sameChecksum)
    {
        var small = _immich.Add("small.mp4", "h264 small");
        _immich.Replace(small with { Exif = small.Exif with { FileSizeInBytes = 3 } });
        var facts = new VideoFacts("h264", 1920, 1080, 20_000_000, 60, 10);
        await _repository.PutImmichSkipAsync(_settings.Database.Path, new ImmichSkipRecord(small.Id, sameChecksum ? small.Checksum : "an-older-checksum", "an-older-rules-fingerprint", "codec h264", Now, facts));

        await DiscoverAsync();

        var stored = (await _repository.ListImmichSkipsAsync(_settings.Database.Path))[small.Id];
        stored.VideoFacts.Should().Be(sameChecksum ? facts : null);
        // Recorded under the current rules, so later runs stop counting it as stale.
        stored.RulesFingerprint.Should().Be(EligibilityEvaluator.RulesFingerprint(_settings.Eligibility));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Task<ImmichDiscoveryResult> DiscoverAsync(int? limit = null, IProgress<ImmichProgress>? progress = null, IJobRepository? repository = null) =>
        new ImmichDiscoveryService(_immich, repository ?? _repository, _ => new ContentProbe(), new FileFingerprintService(), () => Now)
            .DiscoverAsync(_settings.Database.Path, _settings, limit, progress);

    /// <summary>
    /// Lists open jobs as if there were none yet, as discovery would just before a retry from another terminal queued one.
    /// With <paramref name="seenBeforeTheDownload"/> the retry lands before the check ahead of the download; without it,
    /// during the download, so that check misses it too.
    /// </summary>
    private sealed class BlindToOpenJobs(IJobRepository inner, bool seenBeforeTheDownload) : DelegatingJobRepository(inner)
    {
        public override async Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default) =>
            terminal ? await Inner.ListAsync(databasePath, terminal, cancellationToken) : [];

        public override Task<JobRecord?> FindOpenByImmichAssetAsync(string databasePath, string assetId, CancellationToken cancellationToken = default) =>
            seenBeforeTheDownload ? Inner.FindOpenByImmichAssetAsync(databasePath, assetId, cancellationToken) : Task.FromResult<JobRecord?>(null);
    }

    /// <summary>An Immich job for the asset. An open one has its staged copy unless <paramref name="staged"/> is false.</summary>
    private async Task<JobRecord> CreateJobAsync(ImmichAsset asset, JobStatus status, string? failureCategory = null, bool staged = true)
    {
        var path = Path.Combine(_settings.Immich.StagingDirectory, asset.Id, asset.OriginalFileName);
        if (staged && status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped or JobStatus.Rejected))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "staged");
        }

        return await _repository.CreateAsync(_settings.Database.Path, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = path,
            SourceFingerprint = "fingerprint",
            Status = status,
            FailureCategory = failureCategory,
            SourceKind = JobSourceKind.Immich,
            ImmichAssetId = asset.Id,
            ImmichChecksum = asset.Checksum
        });
    }

    private static EligibilityRuleSettings HevcRule() => new() { Codecs = ["hevc"], Resolution = "1080p-1440p", MinimumVideoBitrate = "10Mbps", MinimumFileSize = "5B" };
}
