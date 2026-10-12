using System.Globalization;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

/// <param name="ServerType">Empty when Hetzner did not report it for a recovered worker.</param>
/// <param name="Location">Empty when unknown, as for <paramref name="ServerType"/>.</param>
public sealed record RemoteWorkerInfo(long ServerId, string Name, string Address, string Status, DateTimeOffset? CreatedUtc, string ServerType, string Location);

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
        return "No Hetzner server could be created, so none is running. Tried:" + Environment.NewLine +
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
        lines.Add("Create the server? [y/N] ");
        return string.Join(Environment.NewLine, lines);
    }

    // "EUR" rather than the euro sign: Windows consoles and redirected output use an OEM code page that prints it as "?".
    private static string FormatPrice(decimal price) => $"EUR {price.ToString("0.000", CultureInfo.InvariantCulture)}/h";
}

/// <summary>The user declined <see cref="RemoteWorkerCreationQuote"/>, so nothing was created and nothing bills.</summary>
public sealed class RemoteWorkerCreationDeclinedException() : Exception("No server was created.");

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
    Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The worker in the local state file, without a Hetzner API call or the lifecycle lock, so a run can say where it will
    /// encode before anything else. Its status is <c>recorded</c>: the server may have been deleted outside VideoOptimiser.
    /// </summary>
    Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
