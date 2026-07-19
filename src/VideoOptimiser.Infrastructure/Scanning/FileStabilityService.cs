using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Scanning;

public sealed class FileStabilityService : IFileStabilityService
{
    public async Task<StabilityResult> WaitUntilStableAsync(
        string path,
        StabilitySettings settings,
        bool requireRepeatedObservations = true,
        CancellationToken cancellationToken = default)
    {
        if (!HumanReadableValues.TryParseDuration(settings.PollInterval, out var pollInterval) ||
            !HumanReadableValues.TryParseDuration(settings.MinimumAge, out var minimumAge) ||
            !HumanReadableValues.TryParseDuration(settings.Timeout, out var timeout))
        {
            return new StabilityResult(false, "The stability configuration is invalid.");
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        FileSnapshot? previous = null;
        var stableChecks = 0;

        while (DateTimeOffset.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadSnapshot(path, out var snapshot, out var reason))
            {
                return new StabilityResult(false, reason);
            }

            if (DateTimeOffset.UtcNow - snapshot.LastWriteUtc < minimumAge)
            {
                return new StabilityResult(false, $"File is newer than the configured minimum age of {settings.MinimumAge}.");
            }

            stableChecks = previous is not null && previous == snapshot ? stableChecks + 1 : 1;
            var requiredChecks = requireRepeatedObservations ? settings.RequiredStableChecks : 1;
            if (stableChecks >= requiredChecks)
            {
                return new StabilityResult(true, "File size and last-write time are stable.");
            }

            previous = snapshot;
            await Task.Delay(pollInterval, cancellationToken);
        }

        return new StabilityResult(false, $"File did not reach {settings.RequiredStableChecks} stable observations before the configured timeout.");
    }

    private static bool TryReadSnapshot(string path, out FileSnapshot snapshot, out string reason)
    {
        snapshot = default;
        reason = string.Empty;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                reason = "File no longer exists.";
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            snapshot = new FileSnapshot(info.Length, info.LastWriteTimeUtc);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reason = $"File cannot be opened for reading: {exception.Message}";
            return false;
        }
    }

    private readonly record struct FileSnapshot(long Length, DateTime LastWriteUtc);
}
