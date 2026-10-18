using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Features.Validate;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.Run;

/// <summary>
/// <c>run</c> resuming a job that an earlier run stopped on a managed worker, through the real CLI and the real
/// <see cref="JobProcessor"/> over a fake worker session: what a resume asks of the worker, and what a stop in it says.
/// </summary>
public sealed class RemoteResumeRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeWorkerLifecycle _lifecycle = new();

    public RemoteResumeRunTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    private const string Banner = "Encoding on the Hetzner worker video-optimiser-1, a cx43 in hel1, kept from an earlier run and still billing.";

    /// <summary>
    /// The AV1 is on this PC and only its check is left, so the run creates no worker for it (VOPT-89). Before, the job reached
    /// a remote stage first and the run had to start a server it did not use.
    /// </summary>
    [Fact]
    public async Task AVideoStoppedWhileItsAv1WasCheckedIsCheckedOnThisPcWithoutCreatingAWorker()
    {
        var config = await WriteConfigAsync();
        // No worker is up, so a run that created one would be doing it for nothing.
        _lifecycle.Exists = false;
        var job = await StopJobAsync(JobStatus.Validating);
        var output = Path.Combine(_directory, ".video-optimiser", $"movie.{job.Id:N}.1.encoding.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "av1");
        var manifests = new OutputManifestStore();
        await manifests.SaveAsync(new OutputManifest { SourcePath = Path.Combine(_directory, "movie.mp4"), SourceFingerprint = "fp", OutputPath = output, Crf = 42, CreatedUtc = DateTimeOffset.UtcNow });
        (job.OutputPath, job.ManifestPath) = (output, manifests.GetPath(output));
        await _repository.UpdateAsync(DatabasePath, job);
        var session = new ResumedWorkerSession();

        var (code, text, error) = await RunAsync(session, config);

        (code, error).Should().Be((0, string.Empty));
        text.Should().Be(Transcript(
            "Encoding on a new Hetzner worker created for this run, billed hourly, trying cx43, cx53, cpx42, ccx33 at up to EUR 0.30/h.",
            "Converting 1 video.",
            "",
            "[1/1] movie.mp4 (19 B)",
            "      Checking the output: streams, duration, savings and decode test.",
            "      Ready to finalise: 100 B -> 50 B, 50.0% saved.",
            "Ready to finalise: 1.",
            $"1 video is ready to finalise: review it, then run 'video-optimiser finalize --ready --config \"{config}\"'."));
        _lifecycle.EnsureCalls.Should().Be(0);
        session.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// A rerun that finds the encode finished on the worker has the video downloading before anything can stop it, so a stop
    /// now says the finished encode waits there, not that an encode is still running (VOPT-86).
    /// </summary>
    [Fact]
    public async Task ACtrlCWhileFetchingAnEncodeThatFinishedWhileStoppedSaysTheFinishedEncodeIsWaiting()
    {
        var config = await WriteConfigAsync();
        await StopJobAsync(JobStatus.Encoding);
        var session = new ResumedWorkerSession { EncodeFinished = true, RetrieveFailure = new OperationCanceledException() };

        var (code, text, error) = await RunAsync(session, config);

        error.Should().BeEmpty();
        code.Should().Be((int)ExitCode.Cancelled);
        text.Should().Be(Transcript(
            Banner,
            "Converting 1 video.",
            "",
            "[1/1] movie.mp4 (19 B)",
            "      The encode on the worker had already finished.",
            "",
            "Stopped. The encoded output is waiting on the worker video-optimiser-1, which stays up, billed hourly.",
            $"To resume: video-optimiser run --config \"{config}\"",
            $"To delete it instead: video-optimiser worker delete --config \"{config}\" (the finished encode is lost)"));
        session.Calls.Should().Equal("StopAndRemove", "HasFinishedEncode 1", "Retrieve 1");
        (await _repository.ListAsync(DatabasePath, terminal: false)).Single().ResumeStatus.Should().Be(JobStatus.Downloading);
    }

    [Fact]
    public async Task AWorkerThatStopsAnsweringWhileFetchingAnEncodeThatFinishedWhileStoppedSaysTheFinishedEncodeIsLostOnDelete()
    {
        var config = await WriteConfigAsync();
        await StopJobAsync(JobStatus.Encoding);
        var session = new ResumedWorkerSession { EncodeFinished = true, RetrieveFailure = new RemoteConnectionException("SSH connection failed: timed out.") };

        var (code, text, _) = await RunAsync(session, config);

        code.Should().Be((int)ExitCode.ProcessingFailure);
        text.Should().Be(Transcript(
            Banner,
            "Converting 1 video.",
            "",
            "[1/1] movie.mp4 (19 B)",
            "      The encode on the worker had already finished.",
            "      Worker unreachable: SSH connection failed: timed out.",
            "",
            "Stopped: the worker video-optimiser-1 can't be reached, so 1 video was not converted.",
            "Nothing to finalise.",
            "",
            "The worker stays up, billed hourly, until it is deleted.",
            $"To resume: video-optimiser run --config \"{config}\"",
            $"To delete it instead: video-optimiser worker delete --config \"{config}\" (the finished encode is lost)"));
    }

    /// <summary>A job an earlier run stopped on the managed worker in <paramref name="stoppedIn"/>, with its CRF chosen and one attempt made.</summary>
    private async Task<JobRecord> StopJobAsync(JobStatus stoppedIn)
    {
        var source = Path.Combine(_directory, "movie.mp4");
        await File.WriteAllTextAsync(source, "original video data");
        // The original as the earlier run read it, so the rerun takes it as unchanged.
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = await new FileFingerprintService().CreateAsync(source), Status = JobStatus.Queued, ExecutionMode = ProcessingModes.RemoteSsh, RemoteHost = RemoteExecutionIdentity.ManagedHetzner };
        job.RemoteWorkspace = $"/var/tmp/video-optimiser/{job.Id:N}";
        job = await _repository.CreateAsync(DatabasePath, job);
        (job.Status, job.ResumeStatus, job.Crf, job.Attempt) = (JobStatus.Interrupted, stoppedIn, 42, 1);
        await _repository.UpdateAsync(DatabasePath, job);
        return job;
    }

    private Task<string> WriteConfigAsync() => RunCli.WriteConfigAsync(DatabasePath,
        ["watch:", "  roots:", $"    - path: '{_directory}'", "eligibility:", "  rules:", "    - codecs: ['h264']", "      resolution: '1080p-1440p'", "      minimumVideoBitrate: '1Mbps'", "      minimumFileSize: '1B'", "savings:", "  minimumBytesSaved: '0B'", .. ManagedHetzner]);

    /// <summary>The real processor over the fake worker session, with the probe, readiness and validation fakes the media tools would need.</summary>
    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(ResumedWorkerSession session, string config) =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            CliFakes.NotInteractive(services);
            services.AddSingleton<IRemoteWorkerLifecycle>(_lifecycle);
            services.AddSingleton<IProcessingSessionFactory>(new FixedSessionFactory(session));
            services.AddSingleton<Func<string, IMediaProbe>>(_ => new H264Probe());
            services.AddSingleton<IFileReadinessService>(new ReadableFiles());
            services.AddSingleton<IOutputValidationService>(new PassingValidator());
        }, "run", "--config", config);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
