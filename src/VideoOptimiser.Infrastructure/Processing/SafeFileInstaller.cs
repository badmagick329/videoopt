using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed class SafeFileInstaller : ISafeFileInstaller
{
    public Task InstallAsync(string sourcePath, string outputPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rollback = Path.Combine(Path.GetDirectoryName(sourcePath)!, $"{Path.GetFileNameWithoutExtension(sourcePath)}.{Guid.NewGuid():N}.original.pending-delete{Path.GetExtension(sourcePath)}");
        try
        {
            File.Move(sourcePath, rollback);
            File.Move(outputPath, sourcePath);
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0) throw new IOException("Installed output is missing.");
            File.Delete(rollback);
        }
        catch
        {
            if (!File.Exists(sourcePath) && File.Exists(rollback)) File.Move(rollback, sourcePath);
            throw;
        }

        return Task.CompletedTask;
    }
}
