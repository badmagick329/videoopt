using System.Security.Cryptography;
using System.Text.Json;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed class OutputManifestStore : IOutputManifestStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string GetPath(string outputPath) => outputPath + ".manifest.json";
    public async Task SaveAsync(OutputManifest manifest, CancellationToken cancellationToken = default) =>
        await File.WriteAllTextAsync(GetPath(manifest.OutputPath), JsonSerializer.Serialize(manifest, Options), cancellationToken);
    public async Task<OutputManifest> LoadAsync(string outputPath, CancellationToken cancellationToken = default) =>
        JsonSerializer.Deserialize<OutputManifest>(await File.ReadAllTextAsync(GetPath(outputPath), cancellationToken), Options) ?? throw new InvalidDataException("Output manifest is invalid.");
}

public sealed class FileFingerprintService : IFileFingerprintService
{
    public async Task<string> CreateAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Source file does not exist.", path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var edgeSize = (int)Math.Min(1_048_576, stream.Length);
        var first = new byte[edgeSize];
        _ = await stream.ReadAsync(first, cancellationToken);
        stream.Position = Math.Max(0, stream.Length - edgeSize);
        var last = new byte[edgeSize];
        _ = await stream.ReadAsync(last, cancellationToken);
        var text = $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{Convert.ToHexString(SHA256.HashData(first))}|{Convert.ToHexString(SHA256.HashData(last))}";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}
