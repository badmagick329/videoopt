using System.Globalization;
using System.Text.Json;
using VideoOptimiser.Application.Features.Worker;

namespace VideoOptimiser.Infrastructure.Features.Worker;

internal sealed record WorkerState(long ServerId, long PrimaryIpv4Id, string Name, string Address, string Location, string ServerType, string KnownHostsFile, string Controller, DateTimeOffset? CreatedUtc)
{
    public RemoteWorkerInfo ToInfo(string status) => new(ServerId, Name, Address, status, CreatedUtc, ServerType, Location);
}

/// <summary>
/// The worker's job on disk: the state file that records which server is ours, the known-hosts file beside it, and the lock
/// that serialises every lifecycle command on one database.
/// </summary>
internal static class WorkerStateStore
{
    internal static string StatePath(string databasePath) => Path.GetFullPath(databasePath) + ".hetzner-worker.json";
    internal static string KnownHostsPath(string databasePath, long serverId) => Path.GetFullPath(databasePath) + $".hetzner-{serverId.ToString(CultureInfo.InvariantCulture)}.known_hosts";

    internal static async Task<FileStream> AcquireLockAsync(string databasePath, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(databasePath) + ".hetzner-worker.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    internal static async Task<WorkerState?> ReadStateAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new WorkerState(
            root.GetProperty("serverId").GetInt64(),
            root.GetProperty("primaryIpv4Id").GetInt64(),
            root.GetProperty("name").GetString() ?? string.Empty,
            root.GetProperty("address").GetString() ?? string.Empty,
            root.TryGetProperty("location", out var location) ? location.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("serverType", out var serverType) ? serverType.GetString() ?? string.Empty : string.Empty,
            root.GetProperty("knownHostsFile").GetString() ?? string.Empty,
            root.GetProperty("controller").GetString() ?? string.Empty,
            root.TryGetProperty("createdUtc", out var created) && DateTimeOffset.TryParse(created.GetString(), out var parsed) ? parsed : null);
    }

    internal static async Task WriteStateAsync(string path, WorkerState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("serverId", state.ServerId);
            writer.WriteNumber("primaryIpv4Id", state.PrimaryIpv4Id);
            writer.WriteString("name", state.Name);
            writer.WriteString("address", state.Address);
            if (!string.IsNullOrWhiteSpace(state.Location)) writer.WriteString("location", state.Location);
            if (!string.IsNullOrWhiteSpace(state.ServerType)) writer.WriteString("serverType", state.ServerType);
            writer.WriteString("knownHostsFile", state.KnownHostsFile);
            writer.WriteString("controller", state.Controller);
            if (state.CreatedUtc is not null) writer.WriteString("createdUtc", state.CreatedUtc.Value);
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);
        }
        File.Move(partial, path, overwrite: true);
    }

    internal static void DeleteLocalState(string statePath, string knownHostsFile)
    {
        if (File.Exists(statePath)) File.Delete(statePath);
        if (!string.IsNullOrWhiteSpace(knownHostsFile) && File.Exists(knownHostsFile)) File.Delete(knownHostsFile);
    }
}
