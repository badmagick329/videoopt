using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// What happens to the managed worker as a remote run ends (VOPT-54 step 3, approved wording): the question at the first
/// Ctrl+C, the end question, and the fate of every other end. The real CLI runs with a fake worker, scripted input, and a
/// processor that presses Ctrl+C partway through a video.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class WorkerEndRunTests : IDisposable
{
    private const string Worker = "video-optimiser-1";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());
    private readonly FakeWorkerLifecycle _lifecycle = new();
    private ScriptedInput _input = new();
    private RunStop _stop = new();
    private RunWorkerState _state;
    private readonly RemoteProcessor _processor;
    private string _config = "";

    public WorkerEndRunTests()
    {
        Directory.CreateDirectory(_directory);
        _state = new RunWorkerState(_input);
        _processor = new RemoteProcessor(_repository, this);
    }

    private string DatabasePath => Path.Combine(_directory, "jobs.db");
    private string Config => $"--config \"{_config}\"";

    /// <summary>
    /// The night case: Ctrl+C once, answer y, leave. The video finishes, the worker is deleted, and the next run creates a new
    /// worker and converts the rest.
    /// </summary>
    [Fact]
    public async Task TheNightCaseFinishesTheVideoDeletesTheWorkerAndTheNextRunConvertsTheRest()
    {
        await WriteConfigAsync();
        var jobs = await QueueJobsAsync(3);
        _input.Then("y");
        _processor.During[0] = async _ => { _stop.Press(); await _state.Question.Acknowledged; };

        var (code, output, _) = await RunAsync();

        output.Should().Be(Transcript(
            Banner,
            "Converting 3 videos.",
            "",
            "[1/3] 1.mp4",
            "      Encoding...",
            "        41%, 63 fps, about 7m 36s left",
            "Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.",
            "Delete the worker when the run stops? [y/N] The worker will be deleted when the run stops. Keep this window open until it says it's deleted.",
            "      Ready to finalise.",
            "",
            "Stopped after 1.mp4; 2 videos are still waiting.",
            "Ready to finalise: 1.",
            "Next: review the outputs, then run 'video-optimiser finalize --ready'.",
            $"To convert the rest: video-optimiser run {Config}",
            "Deleting Hetzner server 42.",
            "Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped."));
        code.Should().Be((int)ExitCode.Cancelled);
        _lifecycle.DeleteCalls.Should().Be(1);
        (await StatusesAsync(jobs)).Should().Equal(JobStatus.ReadyToFinalize, JobStatus.Queued, JobStatus.Queued);

        NextRun();
        var (morningCode, morning, _) = await RunAsync();

        morningCode.Should().Be((int)ExitCode.Success);
        _lifecycle.EnsureCalls.Should().Be(2);
        Lines(morning).Should().ContainInOrder("Converting 2 videos.", "[1/2] 2.mp4", "[2/2] 3.mp4", "Ready to finalise: 2.", "Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped.");
        _lifecycle.DeleteCalls.Should().Be(2);
        (await StatusesAsync(jobs)).Should().OnlyContain(status => status == JobStatus.ReadyToFinalize);
    }

    /// <summary>
    /// The video's lines wait while the question does, so they do not run into the typing; of the progress lines only the
    /// latest prints.
    /// </summary>
    [Fact]
    public async Task AnNAtTheFirstPressKeepsTheWorkerAndTheVideosLinesWaitForTheAnswer()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(2);
        _input.Later();
        _processor.During[0] = async progress =>
        {
            _stop.Press();
            progress!.Report(JobProgress.Detail("52%, 63 fps, about 6m 10s left"));
            progress.Report(JobProgress.Detail("63%, 63 fps, about 4m 44s left"));
            _input.Type("n");
            await _state.Question.Acknowledged;
        };

        var (code, output, _) = await RunAsync();

        output.Should().Be(Transcript(
            Banner,
            "Converting 2 videos.",
            "",
            "[1/2] 1.mp4",
            "      Encoding...",
            "        41%, 63 fps, about 7m 36s left",
            "Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.",
            "Delete the worker when the run stops? [y/N] The worker will stay up when the run stops.",
            "        63%, 63 fps, about 4m 44s left",
            "      Ready to finalise.",
            "",
            "Stopped after 1.mp4; 1 video is still waiting.",
            "Ready to finalise: 1.",
            "Next: review the outputs, then run 'video-optimiser finalize --ready'.",
            $"To convert the rest: video-optimiser run {Config}",
            $"The worker {Worker} stays up, billed hourly, until it is deleted.",
            $"To delete it: video-optimiser worker delete {Config}"));
        code.Should().Be((int)ExitCode.Cancelled);
        _lifecycle.DeleteCalls.Should().Be(0);
    }

    /// <summary>
    /// The second press can end the run before it ends the first read; that read then ends with no answer from the user, so
    /// the run reads the end question's answer anew.
    /// </summary>
    [Fact]
    public async Task ASecondPressThatEndsTheFirstReadLateStillWaitsForTheEndAnswer()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(2);
        _input.Later().Then("n");
        _processor.During[0] = progress =>
        {
            _stop.Press();
            _stop.Press();
            _ = AnswerOnceTakenOverAsync(null);
            return Task.CompletedTask;
        };

        var (_, output, _) = await RunAsync();

        output.Should().EndWith(Transcript(
            "Delete the worker when the run stops? [y/N] ",
            "",
            $"Stopped watching. The encode of 1.mp4 is still running on the worker {Worker}.",
            "The worker is billed hourly until it is deleted.",
            $"To pick it up again: video-optimiser run {Config}",
            $"Delete the worker now? The encode in progress is lost. [y/N] To delete it later: video-optimiser worker delete {Config} (the work in progress is lost)"));
        _input.Reads.Should().Be(2);
    }

    /// <summary>The video finishes while the question waits: the held lines print, then the end question takes its place.</summary>
    [Fact]
    public async Task AVideoThatFinishesBeforeTheAnswerEndsWithTheQuestionAgain()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(2);
        _input.Later();
        _processor.During[0] = _ => { _stop.Press(); return Task.CompletedTask; };
        _processor.AfterVideo[0] = () => { _ = AnswerOnceTakenOverAsync("y"); return Task.CompletedTask; };

        var (code, output, _) = await RunAsync();

        output.Should().Be(Transcript(
            Banner,
            "Converting 2 videos.",
            "",
            "[1/2] 1.mp4",
            "      Encoding...",
            "        41%, 63 fps, about 7m 36s left",
            "Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.",
            "Delete the worker when the run stops? [y/N] ",
            "      Ready to finalise.",
            "",
            "Stopped after 1.mp4; 1 video is still waiting.",
            "Ready to finalise: 1.",
            "Next: review the outputs, then run 'video-optimiser finalize --ready'.",
            $"To convert the rest: video-optimiser run {Config}",
            "Delete the worker now? [y/N] Deleting Hetzner server 42.",
            "Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped."));
        code.Should().Be((int)ExitCode.Cancelled);
        _input.Reads.Should().Be(1);
    }

    /// <summary>The second press ends the question's read with no answer; the run asks again at the end, naming the lost encode.</summary>
    [Fact]
    public async Task ASecondPressWithNoAnswerAsksAtTheEndAndNamesTheLostEncode()
    {
        await WriteConfigAsync();
        var jobs = await QueueJobsAsync(2);
        _input.Later().Then("n");
        _processor.During[0] = async _ =>
        {
            _stop.Press();
            _stop.Press();
            _input.Type(null);
            await _state.Question.Acknowledged;
        };

        var (code, output, _) = await RunAsync();

        output.Should().Be(Transcript(
            Banner,
            "Converting 2 videos.",
            "",
            "[1/2] 1.mp4",
            "      Encoding...",
            "        41%, 63 fps, about 7m 36s left",
            "Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.",
            "Delete the worker when the run stops? [y/N] ",
            "",
            $"Stopped watching. The encode of 1.mp4 is still running on the worker {Worker}.",
            "The worker is billed hourly until it is deleted.",
            $"To pick it up again: video-optimiser run {Config}",
            $"Delete the worker now? The encode in progress is lost. [y/N] To delete it later: video-optimiser worker delete {Config} (the work in progress is lost)"));
        code.Should().Be((int)ExitCode.Cancelled);
        _lifecycle.DeleteCalls.Should().Be(0);
        (await _repository.GetAsync(DatabasePath, jobs[0].Id))!.ResumeStatus.Should().Be(JobStatus.Encoding);
    }

    /// <summary>A y given before the second press did not know the encode it would throw away, so the run asks again.</summary>
    [Fact]
    public async Task AYesThenASecondPressDuringTheDownloadAsksAgainBeforeDeleting()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(2);
        _input.Then("y").Then("y");
        _processor.StopStage = JobStatus.Downloading;
        _processor.During[0] = async _ => { _stop.Press(); await _state.Question.Acknowledged; _stop.Press(); };

        var (code, output, _) = await RunAsync();

        output.Should().EndWith(Transcript(
            "",
            $"Stopped. The encoded output is waiting on the worker {Worker}, which stays up, billed hourly.",
            $"To resume: video-optimiser run {Config}",
            "Delete the worker now? The finished encode is lost. [y/N] Deleting Hetzner server 42.",
            "Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped."));
        code.Should().Be((int)ExitCode.Cancelled);
        _lifecycle.DeleteCalls.Should().Be(1);
    }

    /// <summary>With nothing on the worker to lose, a y deletes it at once, and the video resumes on a new worker.</summary>
    [Fact]
    public async Task AYesThenASecondPressWithNothingOnTheWorkerDeletesIt()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(2);
        _input.Then("y");
        _processor.StopStage = JobStatus.Staging;
        _processor.During[0] = async _ => { _stop.Press(); await _state.Question.Acknowledged; _stop.Press(); };

        var (code, output, _) = await RunAsync();

        output.Should().EndWith(Transcript(
            "",
            "Stopped now. 1.mp4 continues from its last finished step on the next run.",
            $"To resume: video-optimiser run {Config}",
            "Deleting Hetzner server 42.",
            "Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped."));
        code.Should().Be((int)ExitCode.Cancelled);
    }

    [Fact]
    public async Task AStopNowWithNothingOnTheWorkerAsksWithoutSayingTheVideoNeedsThisWorker()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(1);
        _input.Later().Then("n");
        _processor.StopStage = JobStatus.Staging;
        _processor.During[0] = async _ => { _stop.Press(); _stop.Press(); _input.Type(null); await _state.Question.Acknowledged; };

        var (_, output, _) = await RunAsync();

        output.Should().EndWith(Transcript(
            "",
            $"Stopped. Nothing is running on the worker {Worker}, but it stays up, billed hourly.",
            $"To resume: video-optimiser run {Config}",
            $"Delete the worker now? [y/N] To delete it later: video-optimiser worker delete {Config}"));
    }

    /// <summary>Redirected input is never asked: a stop now keeps the worker with the lines approved under VOPT-47.</summary>
    [Fact]
    public async Task RedirectedInputKeepsTheWorkerWithTheStopLinesUnchanged()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(2);
        _input = new ScriptedInput(interactive: false);
        _state = new RunWorkerState(_input);
        _processor.During[0] = _ => { _stop.Press(); _stop.Press(); return Task.CompletedTask; };

        var (code, output, _) = await RunAsync();

        output.Should().EndWith(Transcript(
            "Stopping after this video (the encode has about 7m 36s left). Press Ctrl+C again to stop now.",
            "",
            $"Stopped watching. The encode of 1.mp4 is still running on the worker {Worker}.",
            "The worker is billed hourly until it is deleted.",
            $"To pick it up again: video-optimiser run {Config}",
            $"To stop it instead: video-optimiser worker delete {Config} (the work in progress is lost)"));
        code.Should().Be((int)ExitCode.Cancelled);
        _input.Reads.Should().Be(0);
    }

    [Fact]
    public async Task ARedirectedRunThatStopsAfterAVideoKeepsTheWorker()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(2);
        _input = new ScriptedInput(interactive: false);
        _state = new RunWorkerState(_input);
        _processor.During[0] = _ => { _stop.Press(); return Task.CompletedTask; };

        var (_, output, _) = await RunAsync();

        output.Should().EndWith(Transcript(
            $"To convert the rest: video-optimiser run {Config}",
            $"The worker {Worker} stays up, billed hourly, until it is deleted.",
            $"To delete it: video-optimiser worker delete {Config}"));
        _input.Reads.Should().Be(0);
    }

    /// <summary>Failed videos no longer prompt or keep the worker: deleteAfterRun alone decides.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFinishedRunWithAFailureFollowsDeleteAfterRun(bool deleteAfterRun)
    {
        await WriteConfigAsync(deleteAfterRun);
        await QueueJobsAsync(2);
        _processor.Fail[1] = true;

        var (code, output, _) = await RunAsync();

        var worker = deleteAfterRun
            ? new[] { "Deleting Hetzner server 42.", "Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped." }
            : [$"The worker {Worker} stays up, billed hourly, until it is deleted.", $"To delete it: video-optimiser worker delete {Config}"];
        output.Should().Be(Transcript([
            Banner,
            "Converting 2 videos.",
            "",
            "[1/2] 1.mp4",
            "      Encoding...",
            "        41%, 63 fps, about 7m 36s left",
            "      Ready to finalise.",
            "",
            "[2/2] 2.mp4",
            "      Encoding...",
            "        41%, 63 fps, about 7m 36s left",
            "      Failed: the encode broke.",
            "Ready to finalise: 1. Failed: 1.",
            "",
            "Failed jobs:",
            $"{_processor.Ids[1]:N}  2.mp4",
            "EncodeFailed: the encode broke.",
            "Next: review the outputs, then run 'video-optimiser finalize --ready'.",
            .. worker]));
        code.Should().Be((int)ExitCode.PartialSuccess);
        _lifecycle.DeleteCalls.Should().Be(deleteAfterRun ? 1 : 0);
        _input.Reads.Should().Be(0);
    }

    [Fact]
    public async Task AnErrorOnceTheWorkerExistsPrintsTheErrorThenThatTheWorkerBills()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(1);
        _processor.Throw[0] = new InvalidOperationException("disk full");

        var (code, output, error) = await RunAsync();

        error.Should().Be(Transcript("Unexpected failure: disk full"));
        output.Should().EndWith(Transcript(
            "      Encoding...",
            "        41%, 63 fps, about 7m 36s left",
            $"The worker {Worker} stays up, billed hourly, until it is deleted.",
            $"To delete it: video-optimiser worker delete {Config}"));
        code.Should().Be((int)ExitCode.GeneralFailure);
        _lifecycle.DeleteCalls.Should().Be(0);
    }

    /// <summary>A worker kept from an earlier run that this run never needed is the user's to delete, but it still bills.</summary>
    [Fact]
    public async Task ARunWithNothingToConvertSaysTheKeptWorkerBills()
    {
        await WriteConfigAsync();

        var (code, output, _) = await RunAsync();

        output.Should().Be(Transcript(
            Banner,
            "Nothing to finalise.",
            $"The worker {Worker} stays up, billed hourly, until it is deleted.",
            $"To delete it: video-optimiser worker delete {Config}"));
        code.Should().Be((int)ExitCode.Success);
        _lifecycle.DeleteCalls.Should().Be(0);
    }

    /// <summary>An error while deciding the worker's fate is reported, and a stopped run still exits 9.</summary>
    [Fact]
    public async Task AStopWhoseWorkerStateCannotBeReadReportsItAndStillExitsNine()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(1);
        _input = new ScriptedInput(interactive: false);
        _state = new RunWorkerState(_input);
        _processor.During[0] = _ =>
        {
            _stop.Press();
            _stop.Press();
            _lifecycle.ReadFailure = new IOException("the worker's state file is unreadable");
            return Task.CompletedTask;
        };

        var (code, _, error) = await RunAsync();

        error.Should().Be(Transcript("Unexpected failure: the worker's state file is unreadable"));
        code.Should().Be((int)ExitCode.Cancelled);
    }

    [Fact]
    public async Task AFailedDeleteSaysTheWorkerStillBillsAndExitsOne()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(1);
        _lifecycle.DeleteFailure = new HttpRequestException("Hetzner answered 503");

        var (code, output, error) = await RunAsync();

        output.Should().EndWith(Transcript("Deleting Hetzner server 42."));
        error.Should().Be(Transcript(
            $"Couldn't delete the worker {Worker}: Hetzner answered 503",
            $"It's still billing hourly. To delete it: video-optimiser worker delete {Config}"));
        code.Should().Be((int)ExitCode.GeneralFailure);
    }

    /// <summary>A press during the delete would cut it off and leave the worker billing, so it only says what is happening.</summary>
    [Fact]
    public async Task ACtrlCDuringTheDeleteLetsItFinish()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(1);
        _lifecycle.DuringDelete = () => _stop.Press();

        var (code, output, _) = await RunAsync();

        output.Should().EndWith(Transcript(
            "Deleting Hetzner server 42.",
            "Deleting the worker; closing this window now leaves it billing.",
            "Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped."));
        code.Should().Be((int)ExitCode.Success);
    }

    /// <summary>A y answered before the worker stopped answering still deletes it, through the Hetzner API, instead of U3's lines.</summary>
    [Fact]
    public async Task AYesThenAnUnreachableWorkerDeletesItAfterU2()
    {
        await WriteConfigAsync();
        await QueueJobsAsync(2);
        _input.Then("y");
        _processor.During[0] = async _ => { _stop.Press(); await _state.Question.Acknowledged; };
        _processor.Unreachable[0] = true;

        var (code, output, _) = await RunAsync();

        output.Should().EndWith(Transcript(
            "",
            $"Stopped: the worker {Worker} can't be reached, so 2 videos were not converted.",
            "Nothing to finalise.",
            "Deleting Hetzner server 42.",
            "Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped."));
        output.Should().NotContain("stays up");
        code.Should().Be((int)ExitCode.ProcessingFailure);
    }

    private const string Banner = "Encoding on Hetzner cx43 in hel1, kept from an earlier run and still billing.";

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync() =>
        ConsoleCapture.RunCliAsync(services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton(_stop);
            services.AddSingleton<IConsoleInput>(_input);
            services.AddSingleton(_state);
            services.AddSingleton<IRemoteWorkerLifecycle>(_lifecycle);
            services.AddSingleton<IJobProcessor>(_processor);
        }, "run", "--config", _config);

    /// <summary>A new command: its own Ctrl+C, input and worker state.</summary>
    private void NextRun()
    {
        _stop.Dispose();
        _stop = new RunStop();
        _input = new ScriptedInput();
        _state = new RunWorkerState(_input);
        _processor.Calls = 0;
        _processor.During.Clear();
    }

    private async Task AnswerOnceTakenOverAsync(string? line)
    {
        while (!_state.Question.IsTakenOver) await Task.Delay(10);
        _input.Type(line);
    }

    private async Task<JobRecord[]> QueueJobsAsync(int count)
    {
        var jobs = new List<JobRecord>();
        for (var index = 1; index <= count; index++)
        {
            jobs.Add(await _repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(_directory, $"{index}.mp4"), Status = JobStatus.Queued }));
            // Run order is by creation time.
            await Task.Delay(5);
        }
        _processor.Ids = [.. jobs.Select(job => job.Id)];
        return [.. jobs];
    }

    private async Task<JobStatus[]> StatusesAsync(IEnumerable<JobRecord> jobs)
    {
        var statuses = new List<JobStatus>();
        foreach (var job in jobs) statuses.Add((await _repository.GetAsync(DatabasePath, job.Id))!.Status);
        return [.. statuses];
    }

    private async Task WriteConfigAsync(bool deleteAfterRun = true)
    {
        _config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(_config, string.Join('\n',
            "database:",
            $"  path: '{DatabasePath}'",
            "processing:",
            "  mode: \"remoteSsh\"",
            "  remoteSsh:",
            "    lifecycle: \"hetzner\"",
            "    hetzner:",
            $"      deleteAfterRun: {(deleteAfterRun ? "true" : "false")}",
            ""));
    }

    private static string Transcript(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private static string[] Lines(string output) => output.Split(Environment.NewLine);


    public void Dispose()
    {
        _stop.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// A remote video through the statuses <see cref="JobProcessor"/> writes. <see cref="During"/> runs partway, as a user
    /// pressing Ctrl+C; a cancelled token then stops the video in <see cref="StopStage"/> on the worker, as the real one does.
    /// </summary>
    private sealed class RemoteProcessor(SqliteJobRepository repository, WorkerEndRunTests test) : IJobProcessor
    {
        public Dictionary<int, Func<IProgress<JobProgress>?, Task>> During { get; } = [];
        public Dictionary<int, Func<Task>> AfterVideo { get; } = [];
        public Dictionary<int, bool> Fail { get; } = [];
        public Dictionary<int, bool> Unreachable { get; } = [];
        public Dictionary<int, Exception> Throw { get; } = [];
        public JobStatus StopStage { get; set; } = JobStatus.Encoding;
        public Guid[] Ids { get; set; } = [];
        public int Calls { get; set; }

        public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public async Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var index = Calls++;
            job.Status = JobStatus.Encoding;
            await repository.UpdateAsync(databasePath, job, CancellationToken.None);
            progress?.Report(JobProgress.Step("Encoding..."));
            progress?.Report(JobProgress.Detail("41%, 63 fps, about 7m 36s left") with { TimeLeft = new TimeSpan(0, 7, 36) });
            if (During.TryGetValue(index, out var during)) await during(progress);
            if (Throw.TryGetValue(index, out var error)) throw error;
            if (cancellationToken.IsCancellationRequested)
            {
                job.Status = JobStatus.Interrupted;
                job.ResumeStatus = StopStage;
                await repository.UpdateAsync(databasePath, job, CancellationToken.None);
                throw new JobInterruptedException(job, StopStage, remote: true, cancellationToken);
            }

            JobProcessingResult result;
            if (Unreachable.GetValueOrDefault(index))
            {
                job.Status = JobStatus.Interrupted;
                job.ResumeStatus = JobStatus.Encoding;
                job.FailureCategory = FailureCategories.RemoteUnavailable;
                job.FailureMessage = "SSH connection failed: timed out.";
                await repository.UpdateAsync(databasePath, job, CancellationToken.None);
                progress?.Report(JobProgress.Outcome(JobTone.Failure, "Worker unreachable: SSH connection failed: timed out."));
                result = new JobProcessingResult(job, ExitCode.ProcessingFailure, job.FailureMessage);
            }
            else if (Fail.GetValueOrDefault(index))
            {
                job.Status = JobStatus.Failed;
                job.FailureCategory = "EncodeFailed";
                job.FailureMessage = "the encode broke.";
                await repository.UpdateAsync(databasePath, job, CancellationToken.None);
                progress?.Report(JobProgress.Outcome(JobTone.Failure, "Failed: the encode broke."));
                result = new JobProcessingResult(job, ExitCode.ProcessingFailure, job.FailureMessage);
            }
            else
            {
                job.Status = JobStatus.Validating;
                await repository.UpdateAsync(databasePath, job, CancellationToken.None);
                job.Status = JobStatus.ReadyToFinalize;
                await repository.UpdateAsync(databasePath, job, CancellationToken.None);
                progress?.Report(JobProgress.Outcome(JobTone.Success, "Ready to finalise."));
                result = new JobProcessingResult(job, ExitCode.Success, "Ready to finalise.");
            }
            if (AfterVideo.TryGetValue(index, out var after)) await after();
            _ = test;
            return result;
        }
    }
}
