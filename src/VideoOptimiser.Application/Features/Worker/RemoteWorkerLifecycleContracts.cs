using System.Globalization;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Features.Worker;

/// <param name="ServerType">Empty when Hetzner did not report it for a recovered worker.</param>
/// <param name="Location">Empty when unknown, as for <paramref name="ServerType"/>.</param>
public sealed record RemoteWorkerInfo(long ServerId, string Name, string Address, string Status, DateTimeOffset? CreatedUtc, string ServerType, string Location);

/// <summary>The <see cref="RemoteWorkerInfo.Status"/> values that are not Hetzner's own server statuses.</summary>
public static class RemoteWorkerStatuses
{
    /// <summary>Read from the local state file only; Hetzner wasn't asked.</summary>
    public const string Recorded = "recorded";

    /// <summary>The server is gone but its Primary IPv4 still exists, and an address that exists bills.</summary>
    public const string PrimaryIpRetained = "server-missing-primary-ip-retained";

    /// <summary>The status as a user reads it: the two internal values become words, and Hetzner's own (<c>running</c>, <c>off</c>) are already words.</summary>
    public static string Describe(string status) => status switch
    {
        Recorded => "as recorded on this PC",
        PrimaryIpRetained => "gone from Hetzner, its IPv4 address still bills",
        _ => status
    };
}

/// <summary>Why one configured server type produced no server.</summary>
/// <param name="LocationsTried">The configured locations checked for this type; empty when the type itself was unsuitable.</param>
public sealed record RemoteWorkerTypeOutcome(string ServerType, IReadOnlyList<string> LocationsTried, string Reason);

/// <summary>
/// Indicates that no configured server type could be created in any configured location,
/// either at preflight or after placement attempts. No server is left running.
/// </summary>
public sealed class RemoteWorkerCapacityUnavailableException : InvalidOperationException
{
    public RemoteWorkerCapacityUnavailableException(
        IReadOnlyList<RemoteWorkerTypeOutcome> outcomes,
        IReadOnlyList<string>? attemptedFailures = null,
        Exception? innerException = null)
        : base(FormatMessage(outcomes), innerException)
    {
        Outcomes = outcomes;
        AttemptedFailures = attemptedFailures ?? [];
    }

    public IReadOnlyList<RemoteWorkerTypeOutcome> Outcomes { get; }

    /// <summary>Each failed create request, as "type in location (reason)".</summary>
    public IReadOnlyList<string> AttemptedFailures { get; }

    private static string FormatMessage(IReadOnlyList<RemoteWorkerTypeOutcome> outcomes)
    {
        var lines = outcomes.Select(outcome => outcome.LocationsTried.Count == 0
            ? $"  {outcome.ServerType}: {outcome.Reason}"
            : $"  {outcome.ServerType} in {string.Join(", ", outcome.LocationsTried)}: {outcome.Reason}");
        return "No Hetzner worker could be created, so none is running. Tried:" + Environment.NewLine +
               string.Join(Environment.NewLine, lines) + Environment.NewLine +
               "Try again later, or add server types or locations.";
    }
}

/// <summary>One server type and location a managed worker may be created in.</summary>
/// <param name="HourlyGross">EUR per hour including VAT; null when Hetzner lists no price there, which only an uncapped config can reach.</param>
public sealed record RemoteWorkerCreationOption(string ServerType, string Location, decimal? HourlyGross);

/// <summary>
/// What creating a managed worker may cost, asked about once before the first create request starts billing. A placement can
/// still fail when it is requested and fall back to a later candidate, so one approval covers every candidate up to
/// <see cref="MostExpensive"/>.
/// </summary>
/// <param name="MostExpensive">The highest-priced candidate, with an unlisted price ranking highest; equal to
/// <paramref name="First"/> when no fallback costs more.</param>
public sealed record RemoteWorkerCreationQuote(RemoteWorkerCreationOption First, RemoteWorkerCreationOption MostExpensive)
{
    public string FormatPrompt()
    {
        var lines = new List<string>
        {
            First.HourlyGross is { } price
                ? $"Will create {First.ServerType} in {First.Location} at {FormatPrice(price)} incl. VAT."
                : $"Will create {First.ServerType} in {First.Location} (price not listed)."
        };
        if (MostExpensive != First)
        {
            lines.Add(MostExpensive.HourlyGross is { } highest
                ? $"If that placement fails, a fallback may cost up to {FormatPrice(highest)} ({MostExpensive.ServerType} in {MostExpensive.Location})."
                : $"If that placement fails, a fallback may cost more ({MostExpensive.ServerType} in {MostExpensive.Location}, price not listed).");
        }
        lines.Add("Create the worker? [y/N] ");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatPrice(decimal price) => HourlyPrice.Format(price);
}

/// <summary>The one way a Hetzner hourly price is printed, so a quote, a placement line and the status card agree.</summary>
public static class HourlyPrice
{
    // "EUR" rather than the euro sign: Windows consoles and redirected output use an OEM code page that prints it as "?".
    public static string Format(decimal price) => $"EUR {price.ToString("0.000", CultureInfo.InvariantCulture)}/h";
}

/// <summary>The user declined <see cref="RemoteWorkerCreationQuote"/>, so nothing was created and nothing bills.</summary>
public sealed class RemoteWorkerCreationDeclinedException() : Exception("No worker was created.");

/// <summary>What <see cref="IRemoteWorkerLifecycle.CheckAsync"/> found.</summary>
/// <param name="Worker">Null when Hetzner has no managed server for this database (or, when it couldn't be asked, when the state file records none).</param>
/// <param name="HourlyGross">EUR per hour incl. VAT for its type in its location, from Hetzner; null when unknown or not asked.</param>
/// <param name="NotCheckedReason">Why Hetzner wasn't asked or didn't answer; null when it answered. Worker then comes from the state file, status "recorded".</param>
public sealed record WorkerCheck(RemoteWorkerInfo? Worker, decimal? HourlyGross, string? NotCheckedReason);

public interface IRemoteWorkerLifecycle
{
    bool IsManaged(AppSettings settings);
    /// <summary>Reuses the managed worker, or creates one, then waits until it is ready.</summary>
    /// <param name="confirmCreation">Asked once, only when a server would be created, before anything bills; <c>false</c>
    /// throws <see cref="RemoteWorkerCreationDeclinedException"/>. Null approves, for tests and callers that already have consent.</param>
    Task<RemoteWorkerInfo> EnsureReadyAsync(
        AppSettings settings,
        string databasePath,
        IProgress<string>? progress = null,
        Func<RemoteWorkerCreationQuote, CancellationToken, Task<bool>>? confirmCreation = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The worker in the local state file, without a Hetzner API call or the lifecycle lock, so a run can say where it will
    /// encode before anything else. Its status is <c>recorded</c>: the server may have been deleted outside VideoOptimiser.
    /// </summary>
    Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// What is billing for this database, asked of Hetzner for at most 15 seconds, without the lifecycle lock and without
    /// writing or deleting anything, in any <c>processing.mode</c>. Taking the lock would make a read wait for the whole
    /// bootstrap of a run that is creating a worker; <c>worker status</c>, the status card and <c>doctor</c> must answer at
    /// once. A server it finds also becomes <c>processing.remoteSsh</c>'s host, user and known-hosts file in
    /// <paramref name="settings"/>, in memory only, so a host check can reach it. Never throws for a Hetzner failure: that is
    /// <see cref="WorkerCheck.NotCheckedReason"/>.
    /// </summary>
    Task<WorkerCheck> CheckAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default);
}
