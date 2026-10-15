using System.Diagnostics;
using System.Globalization;
using VideoOptimiser.Application.Jobs;

namespace VideoOptimiser.Infrastructure.Persistence;

public sealed class DatabaseLocks : IDatabaseLocks
{
    public IDisposable? TryHoldFinalizeWhilePreparing(string databasePath) =>
        DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(databasePath), DatabaseLock.RunPreparing, out _);

    public IDisposable? TryHoldRun(string databasePath, string command) =>
        DatabaseLock.TryAcquire(DatabaseLock.RunPath(databasePath), command, out _);

    public bool IsFinaliseGoing(string databasePath) =>
        DatabaseLock.ReadLiveHolder(DatabaseLock.FinalizePath(databasePath)) is { } holder && holder.Command != DatabaseLock.RunPreparing;

    public LiveRunHolder? LiveRun(string databasePath) =>
        DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(databasePath)) is { } holder && !LockHolders.QueueVerbs.Contains(holder.Command)
            ? new LiveRunHolder(holder.Command, holder.StartedUtc, holder.ProcessId)
            : null;

    public async Task<IDisposable?> TakeFinalizeAsync(string databasePath, string command, CancellationToken cancellationToken) =>
        (await DatabaseLock.AcquireFinalizeAsync(databasePath, command, cancellationToken)).Lock;
}

/// <summary>
/// An exclusive lock file beside the database. A run takes <see cref="RunPath"/>, so a second run cannot mark the first one's
/// active jobs interrupted; a finalisation takes <see cref="FinalizePath"/>, so a run leaves its <c>Finalizing</c> job alone.
/// The holder keeps write access and lets others read, so a refused command can name who holds it. The OS closes the handle
/// when the process dies, so a lock never goes stale; the file itself is never deleted, since deleting it under a reader
/// would let a second holder in.
/// </summary>
public sealed class DatabaseLock : IDisposable
{
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    // The paths this process holds. A lock file keeps its last holder's details after release, so for this process's own
    // ID the set, not the file, says whether it still holds it.
    private static readonly HashSet<string> HeldHere = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileStream _stream;
    private readonly string _path;

    private DatabaseLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
        lock (HeldHere) HeldHere.Add(path);
    }

    /// <param name="Command">What holds it, such as <c>video-optimiser run</c>.</param>
    public sealed record Holder(string Command, DateTimeOffset StartedUtc, int ProcessId);

    /// <summary>How a run names itself while it holds the finalise lock to prepare, which takes moments.</summary>
    public const string RunPreparing = "run preparing";

    public static string RunPath(string databasePath) => Path.GetFullPath(databasePath) + ".run.lock";
    public static string FinalizePath(string databasePath) => Path.GetFullPath(databasePath) + ".finalize.lock";

    /// <summary>The lock, or null with <paramref name="holder"/> naming who has it; null there too while the holder is still writing.</summary>
    public static DatabaseLock? TryAcquire(string path, string command, out Holder? holder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream stream;
        try
        {
            // Create truncates only once the handle is granted, so a refused opener never wipes the holder's details.
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is SharingViolation or LockViolation)
        {
            holder = Read(path);
            return null;
        }

        using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            writer.Write(string.Create(CultureInfo.InvariantCulture, $"{command}\n{DateTimeOffset.UtcNow:O}\n{Environment.ProcessId}\n"));
        }
        stream.Flush(flushToDisk: true);
        holder = null;
        return new DatabaseLock(stream, Path.GetFullPath(path));
    }

    /// <summary>
    /// Who holds the lock, read without taking it, so asking never refuses a run that starts meanwhile; null when nobody does.
    /// The file outlives its holder, so its details count only while that process is alive and started no later than they
    /// say (a reused process ID started later). A process whose start time cannot be read counts as alive.
    /// </summary>
    public static Holder? ReadLiveHolder(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || Read(path) is not { } holder) return null;
        if (holder.ProcessId == Environment.ProcessId)
        {
            lock (HeldHere) return HeldHere.Contains(path) ? holder : null;
        }
        try
        {
            using var process = Process.GetProcessById(holder.ProcessId);
            if (process.HasExited) return null;
            return process.StartTime.ToUniversalTime() <= holder.StartedUtc.UtcDateTime ? holder : null;
        }
        catch (ArgumentException)
        {
            // No process has that ID.
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return holder;
        }
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the lock. A run holds the finalise lock only while it prepares, which takes
    /// moments, so a finalisation that starts then waits instead of being refused.
    /// </summary>
    public static async Task<(DatabaseLock? Lock, Holder? Holder)> AcquireAsync(string path, string command, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            var acquired = TryAcquire(path, command, out var holder);
            if (acquired is not null || DateTimeOffset.UtcNow >= deadline) return (acquired, holder);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    /// <summary>
    /// The finalise lock for a command. A run holds it only while it prepares, which takes moments, so this waits for a run
    /// however long it takes and gives up only on another finalise, whose holder it returns. One place for the loop, so the
    /// CLI's finalize (which words the refusal from the holder) and the queue verbs wait alike.
    /// </summary>
    public static async Task<(DatabaseLock? Lock, Holder? Holder)> AcquireFinalizeAsync(string databasePath, string command, CancellationToken cancellationToken)
    {
        DatabaseLock? taken;
        Holder? holder;
        do
        {
            (taken, holder) = await AcquireAsync(FinalizePath(databasePath), command, TimeSpan.FromSeconds(5), cancellationToken);
        }
        while (taken is null && holder?.Command == RunPreparing);
        return (taken, holder);
    }

    private static Holder? Read(string path)
    {
        try
        {
            // ReadWrite: the holder has write access, and a reader that refuses to share it is itself refused.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            return lines.Length >= 3 &&
                lines[0].Length > 0 &&
                DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started) &&
                int.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                ? new Holder(lines[0], started, processId)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (HeldHere) HeldHere.Remove(_path);
        _stream.Dispose();
    }
}
