using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

/// <summary>Fakes for running the real CLI through <see cref="ConsoleCapture"/> without Hetzner, SSH or the media tools.</summary>
internal static class CliFakes
{
    public static readonly RemoteWorkerInfo Worker = new(42, "video-optimiser-1", "203.0.113.10", "running", null, "cx43", "hel1");

    /// <summary>Settings validation checks real files (keys, tools); these tests are about what a run prints.</summary>
    public static void AcceptAnySettings(IServiceCollection services) => Diagnostics()(services);

    /// <summary>A settings validator that reports exactly these diagnostics, for tests of how a command shows them.</summary>
    public static Action<IServiceCollection> Diagnostics(params Diagnostic[] diagnostics) =>
        services => services.AddSingleton<ISettingsValidator>(new FixedDiagnostics(diagnostics));

    /// <summary>Redirected input: the worker question is never asked, and a test never waits on the console.</summary>
    public static void NotInteractive(IServiceCollection services) => services.AddSingleton<IConsoleInput>(new ScriptedInput(interactive: false));

    private sealed class FixedDiagnostics(Diagnostic[] diagnostics) : ISettingsValidator
    {
        public IReadOnlyList<Diagnostic> Validate(AppSettings settings) => diagnostics;
    }
}

/// <summary>What the tests that run the real CLI over a queue share: a config, queued videos, and the transcript they expect.</summary>
internal static class RunCli
{
    /// <summary>The config lines for a worker the Hetzner lifecycle manages.</summary>
    public static readonly string[] ManagedHetzner = ["processing:", "  mode: \"remoteSsh\"", "  remoteSsh:", "    lifecycle: \"hetzner\""];

    /// <summary>Writes <c>config.yaml</c> beside the database: the database path, then <paramref name="yaml"/>'s lines.</summary>
    public static async Task<string> WriteConfigAsync(string databasePath, params string[] yaml)
    {
        var config = Path.Combine(Path.GetDirectoryName(databasePath)!, "config.yaml");
        await File.WriteAllTextAsync(config, string.Join('\n', ["database:", $"  path: '{databasePath}'", .. yaml, ""]));
        return config;
    }

    /// <summary>Queues 1.mp4 to <paramref name="count"/>.mp4, which need not exist, beside the database in that run order.</summary>
    public static async Task<JobRecord[]> QueueJobsAsync(IJobRepository repository, string databasePath, int count)
    {
        var jobs = new List<JobRecord>();
        for (var index = 1; index <= count; index++)
        {
            jobs.Add(await repository.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = Path.Combine(Path.GetDirectoryName(databasePath)!, $"{index}.mp4"), Status = JobStatus.Queued }));
            // Run order is by creation time.
            await Task.Delay(5);
        }
        return [.. jobs];
    }

    public static async Task<JobStatus[]> StatusesAsync(IJobRepository repository, string databasePath, IEnumerable<JobRecord> jobs)
    {
        var statuses = new List<JobStatus>();
        foreach (var job in jobs) statuses.Add((await repository.GetAsync(databasePath, job.Id))!.Status);
        return [.. statuses];
    }

    /// <summary>The console output of these lines.</summary>
    public static string Transcript(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    public static string[] Lines(string output) => output.Split(Environment.NewLine);
}

/// <summary>
/// Converts each video through the statuses <see cref="JobProcessor"/> writes, without the media tools, scripted by the
/// video's index in the run. <see cref="During"/> runs partway, as a user pressing Ctrl+C or another terminal; a cancelled
/// token then stops the video in <see cref="StopStage"/>, as the real processor does. Every video it takes is written, so a
/// run that took one twice would find it changed rather than loop.
/// </summary>
internal sealed class ScriptedRunProcessor(bool remote = false) : IJobProcessor
{
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    /// <summary>Whether a video needs the worker; by default, every remote video that passes its local checks.</summary>
    public Func<JobRecord, bool>? NeedsWorker { get; init; }

    /// <summary>The videos that fail a local check: written failed as ineligible, with no line, as nothing converted.</summary>
    public Func<JobRecord, bool> Ineligible { get; init; } = _ => false;

    /// <summary>Whether a video reports its encode, with one progress line, before <see cref="During"/>.</summary>
    public bool ReportsEncoding { get; init; } = true;

    public Dictionary<int, Func<IProgress<JobProgress>?, Task>> During { get; } = [];
    public Dictionary<int, Func<Task>> AfterVideo { get; } = [];
    public Dictionary<int, bool> Fail { get; } = [];
    public Dictionary<int, bool> Unreachable { get; } = [];
    public Dictionary<int, Exception> Throw { get; } = [];
    public JobStatus StopStage { get; set; } = JobStatus.Encoding;
    public int Calls { get; set; }
    public List<string> Processed { get; } = [];

    /// <summary>The command holding the run lock as each video converted.</summary>
    public List<string?> RunLockHolders { get; } = [];

    public Task<bool> NeedsWorkerAsync(JobRecord job, AppSettings settings, CancellationToken cancellationToken = default) =>
        Task.FromResult(NeedsWorker?.Invoke(job) ?? (remote && !Ineligible(job)));

    public async Task<JobProcessingResult> ProcessAsync(string databasePath, JobRecord job, AppSettings settings, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var index = Calls++;
        Processed.Add(Path.GetFileName(job.SourcePath));
        RunLockHolders.Add(DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(databasePath))?.Command);
        if (ReportsEncoding)
        {
            await WriteAsync(databasePath, job, JobStatus.Encoding);
            progress?.Report(JobProgress.Step("Encoding..."));
            progress?.Report(JobProgress.Detail("41%, 63 fps, about 7m 36s left") with { TimeLeft = new TimeSpan(0, 7, 36) });
        }
        if (During.TryGetValue(index, out var during)) await during(progress);
        if (Throw.TryGetValue(index, out var error)) throw error;
        if (cancellationToken.IsCancellationRequested)
        {
            job.ResumeStatus = StopStage;
            await WriteAsync(databasePath, job, JobStatus.Interrupted);
            throw new JobInterruptedException(job, StopStage, remote, cancellationToken);
        }

        JobProcessingResult result;
        if (Ineligible(job))
        {
            (job.FailureCategory, job.FailureMessage, job.CompletedUtc) = ("Ineligible", "Codec av1 is not in any rule.", DateTimeOffset.UtcNow);
            await WriteAsync(databasePath, job, JobStatus.Failed);
            result = new JobProcessingResult(job, ExitCode.NoEligibleFiles, job.FailureMessage);
        }
        else if (Unreachable.GetValueOrDefault(index))
        {
            (job.ResumeStatus, job.FailureCategory, job.FailureMessage) = (JobStatus.Encoding, FailureCategories.RemoteUnavailable, "SSH connection failed: timed out.");
            await WriteAsync(databasePath, job, JobStatus.Interrupted);
            progress?.Report(JobProgress.Outcome(JobTone.Failure, "Worker unreachable: SSH connection failed: timed out."));
            result = new JobProcessingResult(job, ExitCode.ProcessingFailure, job.FailureMessage);
        }
        else if (Fail.GetValueOrDefault(index))
        {
            (job.FailureCategory, job.FailureMessage) = ("EncodeFailed", "the encode broke.");
            await WriteAsync(databasePath, job, JobStatus.Failed);
            progress?.Report(JobProgress.Outcome(JobTone.Failure, "Failed: the encode broke."));
            result = new JobProcessingResult(job, ExitCode.ProcessingFailure, job.FailureMessage);
        }
        else
        {
            await WriteAsync(databasePath, job, JobStatus.Validating, JobStatus.ReadyToFinalize);
            progress?.Report(JobProgress.Outcome(JobTone.Success, "Ready to finalise."));
            result = new JobProcessingResult(job, ExitCode.Success, "Ready to finalise.");
        }
        if (AfterVideo.TryGetValue(index, out var after)) await after();
        return result;
    }

    private async Task WriteAsync(string databasePath, JobRecord job, params JobStatus[] steps)
    {
        foreach (var step in steps)
        {
            job.Status = step;
            await _repository.UpdateAsync(databasePath, job, CancellationToken.None);
        }
    }
}

/// <summary>
/// What the user types, line by line. A line queued with <see cref="Later"/> arrives only when the test calls
/// <see cref="Type"/>, as a user who answers while the video keeps converting; with nothing queued, input has ended (null).
/// </summary>
internal sealed class ScriptedInput(bool interactive = true) : IConsoleInput
{
    private readonly Queue<TaskCompletionSource<string?>> _lines = new();
    private readonly object _gate = new();
    private TaskCompletionSource<string?>? _later;

    public bool IsInteractive => interactive;
    public int Reads { get; private set; }

    /// <summary>A line typed at once when read.</summary>
    public ScriptedInput Then(string? line)
    {
        var typed = new TaskCompletionSource<string?>();
        typed.SetResult(line);
        lock (_gate) _lines.Enqueue(typed);
        return this;
    }

    /// <summary>A read that waits until <see cref="Type"/>.</summary>
    public ScriptedInput Later()
    {
        lock (_gate)
        {
            _later = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _lines.Enqueue(_later);
        }
        return this;
    }

    /// <summary>Answers the read <see cref="Later"/> queued; null is Ctrl+C ending it.</summary>
    public void Type(string? line) => _later!.SetResult(line);

    public string? ReadLine()
    {
        TaskCompletionSource<string?>? next;
        lock (_gate)
        {
            Reads++;
            next = _lines.Count > 0 ? _lines.Dequeue() : null;
        }
        return next?.Task.GetAwaiter().GetResult();
    }
}

/// <summary>
/// A managed worker, up until it is deleted and up again once a run creates it. Managed exactly when the real lifecycle would
/// be: remote mode and the Hetzner lifecycle. Its delete prints the real lifecycle's lines.
/// </summary>
internal sealed class FakeWorkerLifecycle : IRemoteWorkerLifecycle
{
    public int EnsureCalls { get; private set; }
    public int DeleteCalls { get; private set; }

    /// <summary>The database path each create, status and delete was given; it picks the state file, the lock and the server's owner label.</summary>
    public List<string> DatabasePaths { get; } = [];

    /// <summary>Whether the local state file records a worker.</summary>
    public bool Exists { get; set; } = true;

    /// <summary>Thrown by the delete, after its first line, as a Hetzner error would be.</summary>
    public Exception? DeleteFailure { get; set; }

    /// <summary>Runs partway through the delete, such as a Ctrl+C.</summary>
    public Action? DuringDelete { get; set; }

    public bool IsManaged(AppSettings settings) =>
        settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(settings.Processing.RemoteSsh.Lifecycle, RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The price quote the real lifecycle asks the user to approve before it creates a server. With none, a create goes ahead
    /// unasked, as when a worker already exists; with one, a decline throws as the real lifecycle does and creates nothing.
    /// </summary>
    public RemoteWorkerCreationQuote? Quote { get; init; }

    /// <summary>The answer to <see cref="Quote"/>, or null while it was never asked.</summary>
    public bool? Approved { get; private set; }

    public async Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, Func<RemoteWorkerCreationQuote, CancellationToken, Task<bool>>? confirmCreation = null, CancellationToken cancellationToken = default)
    {
        EnsureCalls++;
        DatabasePaths.Add(databasePath);
        if (confirmCreation is not null && Quote is not null)
        {
            Approved = await confirmCreation(Quote, cancellationToken);
            if (!Approved.Value) throw new RemoteWorkerCreationDeclinedException();
            progress?.Report("Creating Hetzner cx43 in hel1.");
        }
        Exists = true;
        return CliFakes.Worker;
    }

    public async Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default)
    {
        DatabasePaths.Add(databasePath);
        var worker = await ReadRecordedAsync(databasePath, cancellationToken);
        // As the real lifecycle does, so a host check goes to the worker it found.
        if (worker is not null) settings.Processing.RemoteSsh.Host = worker.Address;
        return worker;
    }

    /// <summary>Thrown when the state file is read, as an unreadable one would be.</summary>
    public Exception? ReadFailure { get; set; }

    public Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default) =>
        ReadFailure is null ? Task.FromResult(Exists ? CliFakes.Worker : null) : Task.FromException<RemoteWorkerInfo?>(ReadFailure);

    public Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCalls++;
        DatabasePaths.Add(databasePath);
        if (!Exists) return Task.FromResult(false);
        progress?.Report($"Deleting Hetzner server {CliFakes.Worker.ServerId}.");
        DuringDelete?.Invoke();
        if (DeleteFailure is not null) throw DeleteFailure;
        Exists = false;
        progress?.Report("Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped.");
        return Task.FromResult(true);
    }
}
