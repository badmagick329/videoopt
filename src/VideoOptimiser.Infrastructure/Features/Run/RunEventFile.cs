using System.Text.Json;
using VideoOptimiser.Application.Features.Run;

namespace VideoOptimiser.Infrastructure.Features.Run;

/// <summary>
/// The run's event file: JSON lines in <c>&lt;db&gt;.run.events</c>, written by the run and read by any other process.
/// Every file handle shares read, write and delete. Sharing delete lets <see cref="Begin"/> replace the file while an earlier
/// run's process still has it open (that process keeps writing to the orphaned file); sharing write lets a reader open it
/// beside a live writer. Logging is best effort: a disk error on the file never fails the run it describes.
/// </summary>
public sealed class RunEventFile : IRunEventLog, IDisposable
{
    private const FileShare Sharing = FileShare.ReadWrite | FileShare.Delete;
    private readonly Lock _gate = new();
    private FileStream? _writer;

    public static string PathFor(string databasePath) => Path.GetFullPath(databasePath) + ".run.events";

    public void Begin(string databasePath)
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            var path = PathFor(databasePath);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Delete(path);
                _writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, Sharing);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Leave the writer null: appends are dropped, the run goes on.
            }
        }
    }

    public void Append(RunEvent runEvent)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(runEvent, RunEventJsonContext.Default.RunEvent);
        // One buffer, one write: a reader never sees the line without its newline unless the process dies mid-write.
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                _writer.Write(line);
                _writer.Flush();
            }
            catch (IOException)
            {
                // Best effort, as in the type summary.
            }
        }
    }

    public IReadOnlyList<RunEvent> Read(string databasePath)
    {
        byte[] content;
        try
        {
            using var stream = new FileStream(PathFor(databasePath), FileMode.Open, FileAccess.Read, Sharing);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            content = buffer.ToArray();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }

        var events = new List<RunEvent>();
        var start = 0;
        while (start < content.Length)
        {
            var end = Array.IndexOf(content, (byte)'\n', start);
            // No newline: the writer is part-way through this line, or died in it.
            if (end < 0) break;
            if (end > start && Parse(content.AsSpan(start, end - start)) is { } runEvent) events.Add(runEvent);
            start = end + 1;
        }
        return events;
    }

    private static RunEvent? Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, RunEventJsonContext.Default.RunEvent);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
