using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Features.Run;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class ResumePlanTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public ResumePlanTests() => Directory.CreateDirectory(_directory);

    /// <summary>
    /// One row per branch of the plan. <paramref name="remote"/> sets both the job's mode and the session, as they are past the
    /// compatibility check. <paramref name="finished"/> is the worker's answer, passed even where the plan does not ask, to show
    /// such an answer changes nothing. <paramref name="expected"/> names every flag that is set.
    /// </summary>
    [Theory]
    [InlineData("new, this PC", null, false, 0, false, false, false, false, "search encode new-attempt")]
    [InlineData("new, worker", null, true, 0, false, false, false, false, "worker stage search encode download new-attempt")]
    [InlineData("stopped checking, AV1 on this PC", JobStatus.Validating, true, 1, true, true, false, false, "reuse")]
    [InlineData("stopped checking, AV1 gone", JobStatus.Validating, true, 1, true, false, false, false, "worker stage encode download new-attempt")]
    [InlineData("stopped checking, original changed", JobStatus.Validating, true, 1, true, true, true, false, "worker stage search encode download new-attempt")]
    // Today a CRF that is gone is searched for even when the AV1 is reused.
    [InlineData("stopped checking, AV1 on this PC, no CRF", JobStatus.Validating, false, 1, false, true, false, false, "reuse search")]
    [InlineData("stopped encoding, finished on the worker", JobStatus.Encoding, true, 1, true, false, false, true, "worker reattach ask finished download says-finished")]
    [InlineData("stopped downloading, finished on the worker", JobStatus.Downloading, true, 1, true, false, false, true, "worker reattach ask finished download")]
    [InlineData("stopped encoding, still running on the worker", JobStatus.Encoding, true, 1, true, false, false, false, "worker reattach ask stage encode download")]
    [InlineData("stopped encoding, original changed", JobStatus.Encoding, true, 1, true, false, true, true, "worker stage search encode download new-attempt")]
    [InlineData("stopped encoding, no attempt counted", JobStatus.Encoding, true, 0, true, false, false, true, "worker stage encode download new-attempt")]
    [InlineData("stopped encoding, no CRF", JobStatus.Encoding, true, 1, false, false, false, true, "worker reattach stage search encode download")]
    [InlineData("stopped encoding on this PC", JobStatus.Encoding, false, 1, true, false, false, true, "encode new-attempt")]
    public void ThePlanRunsOnlyTheStagesAnEarlierRunLeftUndone(string row, JobStatus? resumeStatus, bool remote, int attempt, bool crfKnown, bool outputOnDisk, bool changed, bool finished, string expected)
    {
        var job = new JobRecord
        {
            Id = Guid.NewGuid(), SourcePath = "movie.mp4", SourceFingerprint = "fingerprint", Status = JobStatus.Queued,
            ExecutionMode = remote ? ProcessingModes.RemoteSsh : ProcessingModes.Local, Attempt = attempt, Crf = crfKnown ? 42 : null
        };

        var plan = ResumePlan.Plan(job, resumeStatus, changed, sessionIsRemote: remote, outputOnDisk, finished);

        Flags(plan).Should().Equal(expected.Split(' '), row);
    }

    /// <summary>
    /// The run creates a worker from <see cref="JobProcessor.NeedsWorkerAsync"/>'s answer, and the job then follows
    /// <see cref="ResumePlan"/>: for a job that passes every check of the original, the two agree on whether the worker is used.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task TheWorkerPredictionAgreesWithThePlan(bool outputOnDisk, bool changed)
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        var settings = Settings();
        var job = new JobRecord
        {
            Id = Guid.NewGuid(), SourcePath = source, Status = JobStatus.Interrupted, ResumeStatus = JobStatus.Validating, Crf = 42, Attempt = 1,
            SourceFingerprint = changed ? "the fingerprint of an earlier original" : await new FileFingerprintService().CreateAsync(source),
            OutputPath = Path.Combine(_directory, ".video-optimiser", $"movie.{Guid.NewGuid():N}.1.encoding.mp4")
        };
        JobCompatibility.Apply(job, JobCompatibility.ExpectedBinding(job.Id, settings));
        if (outputOnDisk)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);
            await File.WriteAllTextAsync(job.OutputPath, "av1");
            await new OutputManifestStore().SaveAsync(new OutputManifest { SourcePath = source, SourceFingerprint = job.SourceFingerprint, OutputPath = job.OutputPath, Crf = 42, CreatedUtc = DateTimeOffset.UtcNow });
        }
        var plan = ResumePlan.Plan(job, job.ResumeStatus, changed, sessionIsRemote: true, outputOnDisk);

        (await Processor().NeedsWorkerAsync(job, settings)).Should().Be(plan.UsesWorker);
    }

    private static IEnumerable<string> Flags(ResumePlan plan)
    {
        if (plan.ReusesOutput) yield return "reuse";
        if (plan.UsesWorker) yield return "worker";
        if (plan.ReattachesAttempt) yield return "reattach";
        if (plan.AsksWorkerForFinishedEncode) yield return "ask";
        if (plan.FinishedOnWorker) yield return "finished";
        if (plan.Stages) yield return "stage";
        if (plan.SearchesCrf) yield return "search";
        if (plan.Encodes) yield return "encode";
        if (plan.Downloads) yield return "download";
        if (plan.NewAttempt) yield return "new-attempt";
        if (plan.SaysEncodeFinished) yield return "says-finished";
    }

    private AppSettings Settings() => new()
    {
        Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") },
        Savings = new SavingsSettings { MinimumBytesSaved = "0B" },
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = _directory }] },
        Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "1Mbps", MinimumFileSize = "1B" }] },
        Processing = new ProcessingSettings { Mode = ProcessingModes.RemoteSsh, RemoteSsh = new RemoteSshSettings { Host = "video-worker" } }
    };

    private static JobProcessor Processor() => new(
        new SqliteJobRepository(new SqliteDatabaseInitializer()),
        new ReadableFiles(),
        _ => new H264Probe(),
        new FixedSessionFactory(new ResumedWorkerSession()),
        new FileFingerprintService(),
        new OutputManifestStore(),
        new PassingValidator(),
        new AbAv1ProgressTranslator(),
        new CommandHints(null));

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
