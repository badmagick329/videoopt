using System.Globalization;
using System.Net;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Hetzner;

namespace VideoOptimiser.Infrastructure.Features.Worker;

/// <summary>
/// The worker's placement job: which server types and locations are worth a create request, what the cheapest and the
/// dearest of them cost for the quote, and how a refused placement reads.
/// </summary>
internal sealed class HetznerPlacement(HetznerCloudClient cloud)
{
    /// <summary>
    /// Lists every server type once and returns the (type, location) pairs worth a create request, type-major in
    /// configuration order. A pair is dropped here, before anything bills, when Hetzner cannot place it, when the
    /// worker doctor would reject the type after it already bills, or when it costs more than the cap.
    /// </summary>
    public async Task<(IReadOnlyList<PlacementCandidate> Candidates, OrderedDictionary<string, TypeReport> Reports)> PreflightCandidatesAsync(
        string token,
        RemoteSshSettings remote,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var hetzner = remote.Hetzner;
        var types = hetzner.ServerTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var locations = hetzner.Locations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var cap = hetzner.MaxHourlyPrice;
        if (!HumanReadableValues.TryParseSize(remote.MinimumAvailableMemory, out var minimumMemory))
            throw new InvalidOperationException("The configured remote minimum available memory is invalid.");
        progress?.Report($"Checking Hetzner capacity for {FormatList(types, "or")} in {FormatList(locations, "or")}" +
                         (cap is null ? "." : $", up to {FormatCap(cap.Value)} incl. VAT."));

        var offers = await cloud.FetchServerTypesAsync(token, types, cancellationToken);
        var candidates = new List<PlacementCandidate>();
        var reports = new OrderedDictionary<string, TypeReport>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            var report = new TypeReport(type);
            reports.Add(type, report);
            var unsuitable = offers.TryGetValue(type, out var offer)
                ? DescribeUnsuitable(offer, remote, minimumMemory)
                : "not a Hetzner server type";
            if (unsuitable is not null)
            {
                report.Notes.Add(unsuitable);
                progress?.Report($"Skipping Hetzner {type}: {unsuitable}.");
                continue;
            }

            report.LocationsTried.AddRange(locations);
            var skipped = new List<(string Kind, string Location)>();
            var kept = new List<string>();
            foreach (var location in locations)
            {
                offer!.HourlyGross.TryGetValue(location, out var listedPrice);
                var price = offer.HourlyGross.ContainsKey(location) ? listedPrice : (decimal?)null;
                var skip = DescribeSkippedLocation(offer, location, price, cap);
                if (skip is not null)
                {
                    skipped.Add(skip.Value);
                    continue;
                }
                // Hetzner's own name, so the create request and the worker state use its spelling.
                candidates.Add(new PlacementCandidate(offer.Name, location, price));
                kept.Add(location);
            }

            var skippedText = DescribeSkipped(skipped);
            if (skippedText.Length > 0) report.Notes.Add(skippedText);
            progress?.Report(kept.Count == 0
                ? $"Skipping Hetzner {type}: {skippedText}."
                : skipped.Count == 0
                    ? $"Hetzner {type} is available in {FormatList(kept, "and")}."
                    : $"Hetzner {type} is available in {FormatList(kept, "and")}; {skippedText}.");
        }

        if (candidates.Count == 0)
            throw new RemoteWorkerCapacityUnavailableException(reports.Values.Select(report => report.ToOutcome()).ToArray());
        return (candidates, reports);
    }

    // An unlisted price ranks above every listed one, since nothing bounds it. MaxBy keeps the first of equal prices, so a
    // list whose first candidate is already the most expensive quotes it alone.
    internal static RemoteWorkerCreationQuote Quote(IReadOnlyList<PlacementCandidate> candidates)
    {
        static RemoteWorkerCreationOption Option(PlacementCandidate candidate) => new(candidate.ServerType, candidate.Location, candidate.HourlyGross);
        return new RemoteWorkerCreationQuote(Option(candidates[0]), Option(candidates.MaxBy(candidate => candidate.HourlyGross ?? decimal.MaxValue)));
    }

    private static string? DescribeUnsuitable(ServerTypeOffer offer, RemoteSshSettings remote, long minimumMemory)
    {
        var reasons = new List<string>();
        // The bootstrap script installs x86_64 builds and exits on any other architecture.
        if (!offer.Architecture.Equals("x86", StringComparison.OrdinalIgnoreCase)) reasons.Add($"{offer.Architecture} architecture, but the worker needs x86");
        if (offer.Deprecated) reasons.Add("deprecated by Hetzner");
        if (offer.Cores < remote.MinimumCpuCount) reasons.Add($"{offer.Cores} cores, below minimumCpuCount {remote.MinimumCpuCount}");
        // Hetzner's "GB" are GiB. The worker doctor checks available memory, which is a little lower than this total.
        if (offer.MemoryGiB * 1024m * 1024m * 1024m < minimumMemory) reasons.Add($"{offer.MemoryGiB.ToString("0.##", CultureInfo.InvariantCulture)} GB memory, below minimumAvailableMemory {remote.MinimumAvailableMemory}");
        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    private static (string Kind, string Location)? DescribeSkippedLocation(ServerTypeOffer offer, string location, decimal? price, decimal? cap)
    {
        if (!offer.Locations.TryGetValue(location, out var status)) return ("not offered in", location);
        if (status.Deprecated) return ("deprecated in", location);
        if (!status.Available) return ("unavailable in", location);
        if (cap is null) return null;
        if (price is null) return ("no price listed in", location);
        return price > cap ? ($"over the {FormatCap(cap.Value)} cap in", $"{location} ({FormatPrice(price.Value)})") : null;
    }

    private static string DescribeSkipped(List<(string Kind, string Location)> skipped) => string.Join("; ", skipped
        .GroupBy(item => item.Kind)
        .Select(group => $"{group.Key} {FormatList(group.Select(item => item.Location).ToArray(), "and")}"));

    private static string FormatList(IReadOnlyList<string> items, string conjunction) => items.Count switch
    {
        0 => "nothing",
        1 => items[0],
        2 => $"{items[0]} {conjunction} {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))}, {conjunction} {items[^1]}"
    };

    internal static string FormatPrice(decimal price) => HourlyPrice.Format(price);

    private static string FormatCap(decimal cap) => $"EUR {cap.ToString("0.00##", CultureInfo.InvariantCulture)}/h";

    internal static bool IsPlacementUnavailable(HetznerApiException exception)
    {
        if (exception.StatusCode != HttpStatusCode.PreconditionFailed) return false;
        var code = exception.Code?.Replace('-', '_');
        return code is "resource_unavailable" or "placement_unavailable";
    }

    internal static bool IsPlacementUnavailable(HetznerActionException exception) =>
        exception.Code?.Replace('-', '_') is "resource_unavailable" or "placement_unavailable";

    internal static string DescribeApiFailure(HetznerApiException exception) =>
        $"HTTP {(int)exception.StatusCode} {exception.StatusCode} ({exception.Code ?? "unknown"}): {exception.ProviderMessage}";
}

internal readonly record struct PlacementCandidate(string ServerType, string Location, decimal? HourlyGross)
{
    public override string ToString() => $"{ServerType} in {Location}";
}

internal sealed class TypeReport(string serverType)
{
    public List<string> LocationsTried { get; } = [];
    public List<string> Notes { get; } = [];
    public RemoteWorkerTypeOutcome ToOutcome() => new(serverType, LocationsTried, string.Join("; ", Notes));
}
