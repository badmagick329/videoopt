using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;

namespace VideoOptimiser.Application.Features.Run;

/// <summary>
/// The line runs print before any download or encoding, and <c>doctor</c> reports, saying where encoding will happen. A user
/// once expected local encoding while the config still said <c>remoteSsh</c>, and learned otherwise only when a Hetzner server
/// was being created; this line is meant to be read before anything costs time or money.
/// </summary>
public static class RunBanner
{
    /// <param name="recordedWorker">The managed worker in the local state file, if any; read without a Hetzner API call, so it
    /// can name a server that was since deleted outside VideoOptimiser.</param>
    /// <param name="overrideFlag">The command-line flag that chose the mode over the config, such as <c>--local</c>.</param>
    public static string Describe(AppSettings settings, RemoteWorkerInfo? recordedWorker = null, string? overrideFlag = null)
    {
        var location = DescribeLocation(settings, recordedWorker);
        var source = overrideFlag is null ? string.Empty : $" ({overrideFlag})";
        return $"Encoding on {location}{source}.";
    }

    private static string DescribeLocation(AppSettings settings, RemoteWorkerInfo? recordedWorker)
    {
        if (!settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase)) return "this PC";

        var remote = settings.Processing.RemoteSsh;
        if (!remote.Lifecycle.Equals(RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase))
        {
            // An empty user means the host is an SSH config alias that supplies its own.
            var target = string.IsNullOrWhiteSpace(remote.User) ? remote.Host : $"{remote.User}@{remote.Host}";
            return $"{target} over SSH";
        }

        if (recordedWorker is not null) return DescribeRecordedWorker(recordedWorker);

        var hetzner = remote.Hetzner;
        // No parentheses here or below: the line ends with "(--remote)" when a flag chose the mode, and two in a row misread.
        var cap = hetzner.MaxHourlyPrice is { } price
            ? $"at up to EUR {price.ToString("0.00##", CultureInfo.InvariantCulture)}/h"
            : "with no price cap";
        return $"a Hetzner server created for this run, billed hourly, trying {string.Join(", ", hetzner.ServerTypes)} {cap}";
    }

    // A worker recovered from Hetzner before its type was recorded can lack the type, location or creation time.
    private static string DescribeRecordedWorker(RemoteWorkerInfo worker)
    {
        var server = worker.ServerType.Length == 0 ? $"Hetzner server {worker.ServerId}"
            : worker.Location.Length == 0 ? $"Hetzner {worker.ServerType}"
            : $"Hetzner {worker.ServerType} in {worker.Location}";
        var billing = worker.CreatedUtc is { } created
            ? $"billing since {created.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC"
            : "still billing";
        return $"{server}, kept from an earlier run and {billing}";
    }
}
