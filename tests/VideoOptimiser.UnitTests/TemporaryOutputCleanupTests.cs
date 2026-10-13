using FluentAssertions;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Jobs;

namespace VideoOptimiser.UnitTests;

public sealed class TemporaryOutputCleanupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly string _outputFolder;
    private readonly string _databasePath;
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    public TemporaryOutputCleanupTests()
    {
        _outputFolder = Path.Combine(_directory, ".video-optimiser");
        Directory.CreateDirectory(_outputFolder);
        _databasePath = Path.Combine(_directory, "jobs.db");
    }

    [Theory]
    [InlineData("movie.{0}.2.encoding.mp4", 2)]
    [InlineData("movie.{0}.2.encoding.mp4.manifest.json", 2)]
    [InlineData("movie.{0}.13.encoding.mkv.partial", 13)]
    [InlineData("no-extension.{0}.1.encoding", 1)]
    [InlineData("dotted.name.{0}.1.encoding.mp4", 1)]
    [InlineData(".tmp.ab-av1-encoding.movie.{0}.3.encoding.mp4", 3)]
    public void ParsesTheJobAndAttemptFromEveryTemporaryOutputFile(string pattern, int expectedAttempt)
    {
        var id = Guid.NewGuid();

        TemporaryOutputClassifier.TryParse(pattern.Replace("{0}", id.ToString("N"), StringComparison.Ordinal), out var jobId, out var attempt).Should().BeTrue();

        jobId.Should().Be(id);
        attempt.Should().Be(expectedAttempt);
    }

    [Theory]
    [InlineData("movie.mp4")]
    [InlineData("movie.0123.1.encoding.mp4")]
    [InlineData("movie.{0}.0.encoding.mp4")]
    [InlineData("movie.{0}.1.mp4")]
    [InlineData("movie.{0}.1.original.pending-delete.mp4")]
    public void IgnoresFilesThatDoNotNameAJobAttempt(string pattern)
    {
        TemporaryOutputClassifier.TryParse(pattern.Replace("{0}", Guid.NewGuid().ToString("N"), StringComparison.Ordinal), out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(JobStatus.Completed, null, null, "local", 1, true)]
    [InlineData(JobStatus.Failed, null, "ValidationFailed", "local", 1, true)]
    [InlineData(JobStatus.Cancelled, null, null, "local", 1, true)]
    [InlineData(JobStatus.Skipped, null, "NotWorthConverting", "local", 1, true)]
    [InlineData(JobStatus.Skipped, null, "NotWorthConverting", "remoteSsh", 1, true)]
    [InlineData(JobStatus.Failed, null, "FinalizationFailed", "local", 1, false)]
    [InlineData(JobStatus.Interrupted, JobStatus.Finalizing, null, "local", 1, false)]
    [InlineData(JobStatus.Interrupted, JobStatus.Encoding, null, "local", 1, true)]
    [InlineData(JobStatus.Interrupted, JobStatus.Encoding, null, "remoteSsh", 1, false)]
    [InlineData(JobStatus.Interrupted, JobStatus.Downloading, null, "remoteSsh", 1, false)]
    [InlineData(JobStatus.Interrupted, JobStatus.Validating, null, "local", 1, false)]
    [InlineData(JobStatus.ReadyToFinalize, null, null, "local", 1, false)]
    [InlineData(JobStatus.Encoding, null, null, "local", 1, false)]
    [InlineData(JobStatus.Encoding, null, null, "local", 0, true)]
    [InlineData(JobStatus.ReadyToFinalize, null, null, "remoteSsh", 0, true)]
    public void ClassifiesOnlyFilesNoJobCanStillUseAsAbandoned(JobStatus status, JobStatus? resumeStatus, string? failureCategory, string mode, int attemptOffset, bool abandoned)
    {
        var job = new JobRecord { Status = status, ResumeStatus = resumeStatus, FailureCategory = failureCategory, ExecutionMode = mode, Attempt = 2 };

        var reason = TemporaryOutputClassifier.AbandonedReason(job, attempt: 1 + attemptOffset);

        (reason is not null).Should().Be(abandoned);
    }

    [Theory]
    [InlineData(JobStatus.Interrupted, JobStatus.Encoding, "local", "resume starts attempt 3 and deletes the partial output of attempt 2")]
    [InlineData(JobStatus.Interrupted, JobStatus.Encoding, "remoteSsh", "resume reattaches to remote attempt 2")]
    [InlineData(JobStatus.Interrupted, JobStatus.Validating, "local", "resume revalidates the existing output")]
    [InlineData(JobStatus.Interrupted, JobStatus.Finalizing, "local", null)]
    [InlineData(JobStatus.ReadyToFinalize, null, "local", null)]
    public void StatusDescribesWhatResumeDoesWithTheTemporaryOutput(JobStatus status, JobStatus? resumeStatus, string mode, string? expected)
    {
        var job = new JobRecord { Status = status, ResumeStatus = resumeStatus, ExecutionMode = mode, Attempt = 2 };

        TemporaryOutputClassifier.DescribeResume(job).Should().Be(expected);
    }

    [Fact]
    public async Task FindsAndDeletesOnlyAbandonedFilesOfJobsInThisDatabase()
    {
        var ready = await CreateJobAsync(JobStatus.ReadyToFinalize, attempt: 2);
        var completed = await CreateJobAsync(JobStatus.Completed, attempt: 1);
        var interrupted = await CreateJobAsync(JobStatus.Interrupted, attempt: 1, JobStatus.Encoding);
        var superseded = Write(ready, 1, ".mp4");
        var current = Write(ready, 2, ".mp4");
        var currentManifest = Write(ready, 2, ".mp4.manifest.json");
        var completedManifest = Write(completed, 1, ".mp4.manifest.json");
        var partialEncode = Write(interrupted, 1, ".mp4");
        var unknown = Path.Combine(_outputFolder, $"other.{Guid.NewGuid():N}.1.encoding.mp4");
        var unrelated = Path.Combine(_outputFolder, "notes.txt");
        File.WriteAllText(unknown, "data");
        File.WriteAllText(unrelated, "data");
        var service = new TemporaryOutputCleanupService(_repository);

        var plan = await service.FindAsync(_databasePath, string.Empty);
        var result = service.Delete(plan);

        plan.Abandoned.Select(file => file.Path).Should().BeEquivalentTo([superseded, completedManifest, partialEncode]);
        plan.UnknownJobFiles.Should().Equal(unknown);
        result.Deleted.Should().Be(3);
        result.Failures.Should().BeEmpty();
        new[] { superseded, completedManifest, partialEncode }.Should().OnlyContain(path => !File.Exists(path));
        new[] { current, currentManifest, unknown, unrelated }.Should().OnlyContain(path => File.Exists(path));
    }

    [Fact]
    public async Task ReportsAFileThatCannotBeDeletedAndContinues()
    {
        var failed = await CreateJobAsync(JobStatus.Failed, attempt: 1);
        var locked = Write(failed, 1, ".mp4");
        var manifest = Write(failed, 1, ".mp4.manifest.json");
        var service = new TemporaryOutputCleanupService(_repository);
        var plan = await service.FindAsync(_databasePath, string.Empty);

        TemporaryOutputCleanupResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = service.Delete(plan);
        }

        result.Deleted.Should().Be(1);
        result.Failures.Should().ContainSingle(failure => failure.StartsWith(locked));
        File.Exists(manifest).Should().BeFalse();
    }

    [Fact]
    public async Task FindsAndDeletesStagedOriginalsOnlyWhenNoOpenJobReadsThem()
    {
        var staging = Path.Combine(_directory, "staging");
        var failed = await CreateStagedJobAsync(staging, JobStatus.Failed, "EncodeFailed");
        var cancelled = await CreateStagedJobAsync(staging, JobStatus.Cancelled, "Cancelled");
        var completed = await CreateStagedJobAsync(staging, JobStatus.Completed, null);
        var skipped = await CreateStagedJobAsync(staging, JobStatus.Skipped, "NotWorthConverting");
        var open = await CreateStagedJobAsync(staging, JobStatus.Queued, null);
        var finalisationFailed = await CreateStagedJobAsync(staging, JobStatus.Failed, "FinalizationFailed");
        // A retry's queued job shares the failed job's path.
        var retried = await CreateStagedJobAsync(staging, JobStatus.Failed, "EncodeFailed");
        await CreateJobAtAsync(retried.SourcePath, JobStatus.Queued, retried.ImmichAssetId);
        var failedOutput = Path.Combine(Path.GetDirectoryName(failed.SourcePath)!, ".video-optimiser", $"clip.{failed.Id:N}.1.encoding.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(failedOutput)!);
        File.WriteAllText(failedOutput, "data");
        var service = new TemporaryOutputCleanupService(_repository);

        var plan = await service.FindAsync(_databasePath, staging);
        var result = service.Delete(plan);

        plan.StagedOriginals.Select(file => file.Path).Should().BeEquivalentTo([failed.SourcePath, cancelled.SourcePath, completed.SourcePath, skipped.SourcePath]);
        plan.StagedOriginals.Single(file => file.Path == skipped.SourcePath).Reason.Should().Be("staged Immich original; job skipped");
        plan.Abandoned.Select(file => file.Path).Should().Equal(failedOutput);
        result.Deleted.Should().Be(5);
        new[] { failed, cancelled, completed, skipped }.Should().OnlyContain(job => !Directory.Exists(Path.GetDirectoryName(job.SourcePath)));
        new[] { open, finalisationFailed, retried }.Should().OnlyContain(job => File.Exists(job.SourcePath));
    }

    [Fact]
    public async Task NeverDeletesAStagedOriginalOutsideItsAssetFolderOrWithoutAStagingDirectory()
    {
        var staging = Path.Combine(_directory, "staging");
        var outside = await CreateJobAtAsync(Path.Combine(_directory, "elsewhere", "clip.mp4"), JobStatus.Failed, Guid.NewGuid().ToString());
        var otherAsset = await CreateJobAtAsync(Path.Combine(staging, Guid.NewGuid().ToString(), "clip.mp4"), JobStatus.Failed, Guid.NewGuid().ToString());
        var staged = await CreateStagedJobAsync(staging, JobStatus.Failed, "EncodeFailed");
        var service = new TemporaryOutputCleanupService(_repository);

        var unconfigured = await service.FindAsync(_databasePath, string.Empty);
        var configured = await service.FindAsync(_databasePath, staging);

        unconfigured.StagedOriginals.Should().BeEmpty();
        configured.StagedOriginals.Select(file => file.Path).Should().Equal(staged.SourcePath);
        File.Exists(outside.SourcePath).Should().BeTrue();
        File.Exists(otherAsset.SourcePath).Should().BeTrue();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<JobRecord> CreateStagedJobAsync(string staging, JobStatus status, string? failureCategory)
    {
        var assetId = Guid.NewGuid().ToString();
        var job = await CreateJobAtAsync(Path.Combine(staging, assetId, "clip.mp4"), status, assetId);
        job.FailureCategory = failureCategory;
        await _repository.UpdateAsync(_databasePath, job);
        return job;
    }

    /// <summary>An Immich job whose staged file exists at <paramref name="path"/>.</summary>
    private async Task<JobRecord> CreateJobAtAsync(string path, JobStatus status, string? assetId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "original");
        return await _repository.CreateAsync(_databasePath, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = path,
            SourceFingerprint = "fingerprint",
            Status = status,
            SourceKind = JobSourceKind.Immich,
            ImmichAssetId = assetId,
            ImmichChecksum = "checksum"
        });
    }

    private async Task<JobRecord> CreateJobAsync(JobStatus status, int attempt, JobStatus? resumeStatus = null) =>
        await _repository.CreateAsync(_databasePath, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = Path.Combine(_directory, $"{status}.mp4"),
            SourceFingerprint = "fingerprint",
            Status = status,
            ResumeStatus = resumeStatus,
            Attempt = attempt
        });

    private string Write(JobRecord job, int attempt, string suffix)
    {
        var path = Path.Combine(_outputFolder, $"{job.Status}.{job.Id:N}.{attempt}.encoding{suffix}");
        File.WriteAllText(path, "data");
        return path;
    }
}
