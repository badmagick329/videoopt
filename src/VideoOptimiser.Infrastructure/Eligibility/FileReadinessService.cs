using VideoOptimiser.Application.Eligibility;

namespace VideoOptimiser.Infrastructure.Eligibility;

public sealed class FileReadinessService : IFileReadinessService
{
    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION, in the low word of the IOException's HRESULT.
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

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

            // FileShare.Read denies write sharing, so the open fails while any other handle can write the file: a copy, a
            // download or a phone sync still in progress (VOPT-169).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult(new FileReadinessResult(true, "File can be read."));
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is SharingViolation or LockViolation)
        {
            // The .NET message names the full path, which every caller already prints beside the reason.
            return Task.FromResult(new FileReadinessResult(false, "File is open in another program, which may still be writing it."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new FileReadinessResult(false, $"File cannot be opened for reading: {exception.Message}"));
        }
    }
}
