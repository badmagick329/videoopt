using System.Globalization;

namespace VideoOptimiser.Infrastructure.Jobs;

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
    private readonly FileStream _stream;

    private DatabaseLock(FileStream stream) => _stream = stream;

    /// <param name="Command">What holds it, such as <c>queue run</c>.</param>
    public sealed record Holder(string Command, DateTimeOffset StartedUtc, int ProcessId);

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
        return new DatabaseLock(stream);
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

    public void Dispose() => _stream.Dispose();
}
