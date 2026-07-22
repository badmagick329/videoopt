using VideoOptimiser.Application.Scanning;

namespace VideoOptimiser.Infrastructure.Scanning;

public sealed class FileReadinessService : IFileReadinessService
{
    public Task<FileReadinessResult> CheckAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Task.FromResult(new FileReadinessResult(false, "File no longer exists."));
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Task.FromResult(new FileReadinessResult(true, "File can be read."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new FileReadinessResult(false, $"File cannot be opened for reading: {exception.Message}"));
        }
    }
}
