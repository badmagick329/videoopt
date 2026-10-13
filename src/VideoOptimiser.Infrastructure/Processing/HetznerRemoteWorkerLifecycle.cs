using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Configuration;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed class HetznerRemoteWorkerLifecycle : IRemoteWorkerLifecycle, IDisposable
{
    private const string ApiBase = "https://api.hetzner.cloud/v1/";
    private const string ManagedLabel = "video-optimiser-managed";
    private const string ControllerLabel = "video-optimiser-controller";
    private const string BootstrapProgressPath = "/opt/video-optimiser/.bootstrap-progress";
    private const string BootstrapCompletePath = "/opt/video-optimiser/.bootstrap-complete";
    private static readonly TimeSpan DefaultReadinessProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessTerminationGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultProgressHeartbeatInterval = TimeSpan.FromSeconds(60);
    private const int MaxServerTypePages = 10;
    private readonly IExternalProcessRunner _processes;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _readinessProbeTimeout;
    private readonly TimeSpan _progressHeartbeatInterval;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly bool _ownsHttpClient;

    public HetznerRemoteWorkerLifecycle(IExternalProcessRunner processes)
        : this(processes, new HttpClient { BaseAddress = new Uri(ApiBase) }, Task.Delay, ownsHttpClient: true)
    {
    }

    internal HetznerRemoteWorkerLifecycle(
        IExternalProcessRunner processes,
        HttpClient http,
        Func<TimeSpan, CancellationToken, Task> delay,
        bool ownsHttpClient = false,
        TimeSpan? readinessProbeTimeout = null,
        TimeSpan? progressHeartbeatInterval = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _processes = processes;
        _http = http;
        _http.BaseAddress ??= new Uri(ApiBase);
        _delay = delay;
        _readinessProbeTimeout = readinessProbeTimeout ?? DefaultReadinessProbeTimeout;
        if (_readinessProbeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(readinessProbeTimeout));
        _progressHeartbeatInterval = progressHeartbeatInterval ?? DefaultProgressHeartbeatInterval;
        if (_progressHeartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(progressHeartbeatInterval));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _ownsHttpClient = ownsHttpClient;
    }

    public bool IsManaged(AppSettings settings) =>
        settings.Processing.Mode.Equals(ProcessingModes.RemoteSsh, StringComparison.OrdinalIgnoreCase) &&
        settings.Processing.RemoteSsh.Lifecycle.Equals(RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase);

    public async Task<RemoteWorkerInfo> EnsureReadyAsync(
        AppSettings settings,
        string databasePath,
        IProgress<string>? progress = null,
        Func<RemoteWorkerCreationQuote, CancellationToken, Task<bool>>? confirmCreation = null,
        CancellationToken cancellationToken = default)
    {
        RequireManaged(settings);
        await using var lifecycleLock = await AcquireLockAsync(databasePath, cancellationToken);
        var token = GetToken(settings);
        var controller = ControllerId(databasePath);
        var statePath = StatePath(databasePath);
        var state = await ResolveStateAsync(token, controller, statePath, cancellationToken);
        if (state is null)
        {
            _ = await DeleteOrphanedPrimaryIpsAsync(token, controller, cancellationToken);
            progress?.Report("No managed Hetzner worker is running.");
            // cloud-init runs the script with bash, which fails on CRLF; a Windows checkout with core.autocrlf has CRLF.
            var script = (await File.ReadAllTextAsync(settings.Processing.RemoteSsh.Hetzner.BootstrapScriptPath, cancellationToken)).ReplaceLineEndings("\n");
            state = await CreateAsync(token, settings, controller, statePath, script, progress, confirmCreation, cancellationToken);
            progress?.Report($"Created Hetzner server {state.ServerId} at {state.Address}; bootstrap is running.");
            progress?.Report("Setting up the worker: it builds libvmaf, SVT-AV1 and FFmpeg from source and installs ab-av1, which takes several minutes. Progress follows.");
        }
        else
        {
            progress?.Report($"Reusing Hetzner server {state.ServerId} at {state.Address}; checking bootstrap progress.");
        }

        ApplyConnection(settings, state);
        await WaitUntilReadyAsync(settings, state, progress, cancellationToken);
        return state.ToInfo("ready");
    }

    public async Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default)
    {
        RequireManaged(settings);
        await using var lifecycleLock = await AcquireLockAsync(databasePath, cancellationToken);
        var token = GetToken(settings);
        var state = await ResolveStateAsync(token, ControllerId(databasePath), StatePath(databasePath), cancellationToken, retainStateWhenServerMissing: true);
        if (state is null) return null;
        var server = await GetServerAsync(token, state.ServerId, cancellationToken);
        if (server is null)
        {
            var status = state.PrimaryIpv4Id > 0 && await PrimaryIpExistsAsync(token, state.PrimaryIpv4Id, cancellationToken)
                ? "server-missing-primary-ip-retained"
                : "server-missing";
            return state.ToInfo(status);
        }
        ApplyConnection(settings, state);
        return state.ToInfo(server.Value.Status);
    }

    public async Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await ReadStateAsync(StatePath(databasePath), cancellationToken))?.ToInfo("recorded");
        }
        catch (FileNotFoundException)
        {
            // Without the lock, a concurrent worker delete can remove the file between the existence check and the read.
            return null;
        }
    }

    public async Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireManaged(settings);
        await using var lifecycleLock = await AcquireLockAsync(databasePath, cancellationToken);
        var token = GetToken(settings);
        var controller = ControllerId(databasePath);
        var statePath = StatePath(databasePath);
        var state = await ResolveStateAsync(token, controller, statePath, cancellationToken, retainStateWhenServerMissing: true);
        if (state is null)
        {
            var deletedOrphans = await DeleteOrphanedPrimaryIpsAsync(token, controller, cancellationToken);
            if (deletedOrphans) progress?.Report("Deleted an orphaned managed Primary IPv4.");
            return deletedOrphans;
        }

        var server = await GetServerAsync(token, state.ServerId, cancellationToken);
        if (server is not null) VerifyOwnership(server.Value, controller);
        progress?.Report($"Deleting Hetzner server {state.ServerId}.");
        if (server is not null)
        {
            var actionId = await DeleteServerAsync(token, state.ServerId, cancellationToken);
            if (actionId is not null) await WaitForActionAsync(token, actionId.Value, cancellationToken);
            await WaitForServerDeletionAsync(token, state.ServerId, cancellationToken);
        }

        if (state.PrimaryIpv4Id > 0 && await PrimaryIpExistsAsync(token, state.PrimaryIpv4Id, cancellationToken))
        {
            progress?.Report($"Deleting retained Primary IPv4 {state.PrimaryIpv4Id}.");
            await DeletePrimaryIpAsync(token, state.PrimaryIpv4Id, cancellationToken);
        }
        _ = await DeleteOrphanedPrimaryIpsAsync(token, controller, cancellationToken);

        DeleteLocalState(statePath, state.KnownHostsFile);
        progress?.Report("Managed Hetzner worker deleted; billing for its server and Primary IPv4 has stopped.");
        return true;
    }

    private async Task<WorkerState?> ResolveStateAsync(string token, string controller, string statePath, CancellationToken cancellationToken, bool retainStateWhenServerMissing = false)
    {
        var state = await ReadStateAsync(statePath, cancellationToken);
        if (state is not null)
        {
            var server = await GetServerAsync(token, state.ServerId, cancellationToken);
            if (server is not null)
            {
                VerifyOwnership(server.Value, controller);
                var refreshed = state with
                {
                    Address = server.Value.Address,
                    PrimaryIpv4Id = server.Value.PrimaryIpv4Id,
                    CreatedUtc = server.Value.CreatedUtc,
                    Location = server.Value.Location.Length > 0 ? server.Value.Location : state.Location,
                    ServerType = server.Value.ServerType.Length > 0 ? server.Value.ServerType : state.ServerType
                };
                await WriteStateAsync(statePath, refreshed, cancellationToken);
                return refreshed;
            }
            if (retainStateWhenServerMissing) return state;
            if (state.PrimaryIpv4Id > 0 && await PrimaryIpExistsAsync(token, state.PrimaryIpv4Id, cancellationToken))
                await DeletePrimaryIpAsync(token, state.PrimaryIpv4Id, cancellationToken);
            DeleteLocalState(statePath, state.KnownHostsFile);
        }

        var servers = await ListManagedServersAsync(token, controller, cancellationToken);
        if (servers.Count > 1) throw new InvalidOperationException("More than one managed Hetzner server has this controller label; refusing to choose or delete either one.");
        if (servers.Count == 0) return null;
        VerifyOwnership(servers[0], controller);
        var recovered = new WorkerState(
            servers[0].Id,
            servers[0].PrimaryIpv4Id,
            servers[0].Name,
            servers[0].Address,
            servers[0].Location,
            servers[0].ServerType,
            KnownHostsPath(databasePath: Path.GetFullPath(statePath[..^".hetzner-worker.json".Length]), servers[0].Id),
            controller,
            servers[0].CreatedUtc);
        await WriteStateAsync(statePath, recovered, cancellationToken);
        return recovered;
    }

    private async Task<WorkerState> CreateAsync(
        string token,
        AppSettings settings,
        string controller,
        string statePath,
        string bootstrapScript,
        IProgress<string>? progress,
        Func<RemoteWorkerCreationQuote, CancellationToken, Task<bool>>? confirmCreation,
        CancellationToken cancellationToken)
    {
        var hetzner = settings.Processing.RemoteSsh.Hetzner;
        if (Encoding.UTF8.GetByteCount(bootstrapScript) > 32 * 1024) throw new InvalidOperationException("The Hetzner bootstrap script exceeds the 32 KiB user-data limit.");
        var name = $"{hetzner.ServerNamePrefix}-{controller[..12]}".ToLowerInvariant();
        var (candidates, reports) = await PreflightCandidatesAsync(token, settings.Processing.RemoteSsh, progress, cancellationToken);
        // Asked once, inside the lifecycle lock, so the approved candidates are the ones tried. The fallback loop below never
        // asks again: the quote already named the most it can cost.
        if (confirmCreation is not null && !await confirmCreation(Quote(candidates), cancellationToken))
            throw new RemoteWorkerCreationDeclinedException();
        var failures = new List<string>();
        Exception? lastPlacementFailure = null;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            progress?.Report(candidate.HourlyGross is { } price
                ? $"Creating Hetzner {candidate} ({FormatPrice(price)} incl. VAT)."
                : $"Creating Hetzner {candidate}.");
            JsonDocument document;
            try
            {
                document = await SendJsonAsync(token, HttpMethod.Post, "servers", CreateServerPayload(name, hetzner, candidate, controller, bootstrapScript), cancellationToken);
            }
            catch (HetznerApiException exception) when (IsPlacementUnavailable(exception))
            {
                lastPlacementFailure = exception;
                var reason = DescribeApiFailure(exception);
                failures.Add($"{candidate} ({reason})");
                reports[candidate.ServerType].Notes.Add($"placement failed in {candidate.Location} ({reason})");
                if (candidateIndex + 1 < candidates.Count)
                {
                    progress?.Report($"Hetzner {candidate} placement unavailable ({reason}); falling back to {candidates[candidateIndex + 1]}.");
                    continue;
                }
                break;
            }

            using (document)
            {
                var serverElement = document.RootElement.GetProperty("server");
                var server = ParseServer(serverElement);
                var knownHostsFile = KnownHostsPath(statePath[..^".hetzner-worker.json".Length], server.Id);
                var state = new WorkerState(server.Id, server.PrimaryIpv4Id, server.Name, server.Address, candidate.Location, candidate.ServerType, knownHostsFile, controller, server.CreatedUtc);

                // A failed POST never reaches this point, so no local state is left for a failed placement attempt.
                await WriteStateAsync(statePath, state, cancellationToken);

                try
                {
                    if (server.PrimaryIpv4Id > 0) await SetPrimaryIpAutoDeleteAsync(token, server.PrimaryIpv4Id, controller, cancellationToken);
                    if (document.RootElement.TryGetProperty("action", out var action) && action.TryGetProperty("id", out var actionId))
                    {
                        progress?.Report($"Hetzner server {server.Id} allocated as {candidate}; waiting for server provisioning to finish.");
                        await WaitForActionAsync(token, actionId.GetInt64(), cancellationToken);
                    }
                }
                catch (HetznerActionException exception) when (IsPlacementUnavailable(exception))
                {
                    var reason = $"Hetzner action failed ({exception.Code ?? "unknown"}): {exception.Message}";
                    failures.Add($"{candidate} ({reason})");
                    reports[candidate.ServerType].Notes.Add($"provisioning failed in {candidate.Location} ({reason})");
                    lastPlacementFailure = exception;
                    progress?.Report($"Hetzner {candidate} provisioning failed ({reason}); cleaning up before fallback.");
                    await CleanupFailedCreateAsync(token, statePath, state, cancellationToken);
                    if (candidateIndex + 1 < candidates.Count)
                    {
                        progress?.Report($"Hetzner {candidate} cleaned up; falling back to {candidates[candidateIndex + 1]}.");
                        continue;
                    }
                    break;
                }
                return state;
            }
        }

        throw new RemoteWorkerCapacityUnavailableException(
            reports.Values.Select(report => report.ToOutcome()).ToArray(),
            failures,
            lastPlacementFailure);
    }

    /// <summary>
    /// Lists every server type once and returns the (type, location) pairs worth a create request, type-major in
    /// configuration order. A pair is dropped here, before anything bills, when Hetzner cannot place it, when the
    /// worker doctor would reject the type after it already bills, or when it costs more than the cap.
    /// </summary>
    private async Task<(IReadOnlyList<PlacementCandidate> Candidates, OrderedDictionary<string, TypeReport> Reports)> PreflightCandidatesAsync(
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

        var offers = await FetchServerTypesAsync(token, types, cancellationToken);
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
    private static RemoteWorkerCreationQuote Quote(IReadOnlyList<PlacementCandidate> candidates)
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

    private async Task<Dictionary<string, ServerTypeOffer>> FetchServerTypesAsync(string token, IReadOnlyCollection<string> wanted, CancellationToken cancellationToken)
    {
        // One listing covers every configured type; Hetzner's name filter takes a single name.
        var offers = new Dictionary<string, ServerTypeOffer>(StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? page = 1;
        for (var requests = 0; page is not null; requests++)
        {
            if (requests == MaxServerTypePages) throw PreflightFailure($"the server type listing did not end within {MaxServerTypePages} pages");
            JsonDocument document;
            try
            {
                document = await SendJsonAsync(token, HttpMethod.Get, $"server_types?per_page=50&page={page}", null, cancellationToken);
            }
            catch (JsonException exception)
            {
                throw PreflightFailure($"the response was not valid JSON: {exception.Message}", exception);
            }
            using (document)
            {
                page = ParseServerTypesPage(document.RootElement, wanted, listed, offers);
            }
        }
        return offers;
    }

    private static int? ParseServerTypesPage(JsonElement root, IReadOnlyCollection<string> wanted, HashSet<string> listed, Dictionary<string, ServerTypeOffer> offers)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("server_types", out var serverTypes) || serverTypes.ValueKind != JsonValueKind.Array)
            throw PreflightFailure("the response did not contain a server_types array");
        foreach (var serverType in serverTypes.EnumerateArray())
        {
            if (serverType.ValueKind != JsonValueKind.Object || !serverType.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                throw PreflightFailure("the server_types array contained an entry without a string name");
            var name = nameElement.GetString()!;
            if (!listed.Add(name)) throw PreflightFailure($"server type '{name}' was listed more than once");
            if (wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) offers.Add(name, ParseOffer(serverType, name));
        }

        return root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
               meta.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object &&
               pagination.TryGetProperty("next_page", out var next) && next.ValueKind == JsonValueKind.Number
            ? next.GetInt32()
            : null;
    }

    private static ServerTypeOffer ParseOffer(JsonElement serverType, string name)
    {
        if (!serverType.TryGetProperty("architecture", out var architecture) || architecture.ValueKind != JsonValueKind.String)
            throw PreflightFailure($"server type '{name}' did not contain a string architecture");
        if (!serverType.TryGetProperty("cores", out var cores) || !cores.TryGetInt32(out var coreCount))
            throw PreflightFailure($"server type '{name}' did not contain an integer cores field");
        if (!serverType.TryGetProperty("memory", out var memory) || !memory.TryGetDecimal(out var memoryGiB))
            throw PreflightFailure($"server type '{name}' did not contain a numeric memory field");
        var deprecated = serverType.TryGetProperty("deprecated", out var deprecatedField) && deprecatedField.ValueKind == JsonValueKind.True ||
                         serverType.TryGetProperty("deprecation", out var deprecation) && deprecation.ValueKind == JsonValueKind.Object;

        if (!serverType.TryGetProperty("locations", out var locationArray) || locationArray.ValueKind != JsonValueKind.Array)
            throw PreflightFailure($"server type '{name}' did not contain a locations array");
        var locations = new Dictionary<string, LocationStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locationArray.EnumerateArray())
        {
            if (location.ValueKind != JsonValueKind.Object ||
                !location.TryGetProperty("name", out var locationName) || locationName.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(locationName.GetString()))
                throw PreflightFailure($"the locations array of '{name}' contained an entry without a non-empty string name");
            if (!location.TryGetProperty("available", out var available) || available.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw PreflightFailure($"location '{locationName.GetString()}' of '{name}' did not contain a boolean available field");
            var locationDeprecated = location.TryGetProperty("deprecation", out var locationDeprecation) && locationDeprecation.ValueKind == JsonValueKind.Object;
            if (!locations.TryAdd(locationName.GetString()!, new LocationStatus(available.GetBoolean(), locationDeprecated)))
                throw PreflightFailure($"the locations array of '{name}' contained duplicate location '{locationName.GetString()}'");
        }

        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (serverType.TryGetProperty("prices", out var priceArray) && priceArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var price in priceArray.EnumerateArray())
            {
                if (price.ValueKind != JsonValueKind.Object ||
                    !price.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.String ||
                    !price.TryGetProperty("price_hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Object ||
                    !hourly.TryGetProperty("gross", out var gross) || gross.ValueKind != JsonValueKind.String ||
                    !decimal.TryParse(gross.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var grossValue))
                    throw PreflightFailure($"the prices array of '{name}' contained an entry without a location and a decimal price_hourly.gross");
                prices[location.GetString()!] = grossValue;
            }
        }

        return new ServerTypeOffer(name, architecture.GetString()!, coreCount, memoryGiB, deprecated, locations, prices);
    }

    private static string FormatList(IReadOnlyList<string> items, string conjunction) => items.Count switch
    {
        0 => "nothing",
        1 => items[0],
        2 => $"{items[0]} {conjunction} {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))}, {conjunction} {items[^1]}"
    };

    // "EUR", not the euro sign: Windows consoles and redirected output default to an OEM code page that prints it as "?".
    private static string FormatPrice(decimal price) => $"EUR {price.ToString("0.000", CultureInfo.InvariantCulture)}/h";

    private static string FormatCap(decimal cap) => $"EUR {cap.ToString("0.00##", CultureInfo.InvariantCulture)}/h";

    private static InvalidOperationException PreflightFailure(string reason, Exception? innerException = null) =>
        new($"Hetzner server type preflight failed: {reason}; no server creation was attempted.", innerException);

    private static byte[] CreateServerPayload(string name, HetznerSettings hetzner, PlacementCandidate candidate, string controller, string bootstrapScript)
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("server_type", candidate.ServerType);
            writer.WriteString("image", hetzner.Image);
            writer.WriteString("location", candidate.Location);
            writer.WritePropertyName("ssh_keys");
            writer.WriteStartArray();
            writer.WriteStringValue(hetzner.SshKeyName);
            writer.WriteEndArray();
            writer.WriteString("user_data", bootstrapScript);
            writer.WritePropertyName("labels");
            writer.WriteStartObject();
            writer.WriteString(ManagedLabel, "true");
            writer.WriteString(ControllerLabel, controller);
            writer.WriteEndObject();
            writer.WritePropertyName("public_net");
            writer.WriteStartObject();
            writer.WriteBoolean("enable_ipv4", true);
            writer.WriteBoolean("enable_ipv6", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return payload.ToArray();
    }

    private static bool IsPlacementUnavailable(HetznerApiException exception)
    {
        if (exception.StatusCode != HttpStatusCode.PreconditionFailed) return false;
        var code = exception.Code?.Replace('-', '_');
        return code is "resource_unavailable" or "placement_unavailable";
    }

    private static bool IsPlacementUnavailable(HetznerActionException exception) =>
        exception.Code?.Replace('-', '_') is "resource_unavailable" or "placement_unavailable";

    private static string DescribeApiFailure(HetznerApiException exception) =>
        $"HTTP {(int)exception.StatusCode} {exception.StatusCode} ({exception.Code ?? "unknown"}): {exception.ProviderMessage}";

    private async Task CleanupFailedCreateAsync(string token, string statePath, WorkerState state, CancellationToken cancellationToken)
    {
        // A failed placement action can race with Hetzner removing the server. Check first so
        // cleanup remains safe when the server has already disappeared.
        var server = await GetServerAsync(token, state.ServerId, cancellationToken);
        if (server is not null)
        {
            VerifyOwnership(server.Value, state.Controller);
            var actionId = await DeleteServerAsync(token, state.ServerId, cancellationToken);
            if (actionId is not null) await WaitForActionAsync(token, actionId.Value, cancellationToken);
            await WaitForServerDeletionAsync(token, state.ServerId, cancellationToken);
        }
        if (state.PrimaryIpv4Id > 0 && await PrimaryIpExistsAsync(token, state.PrimaryIpv4Id, cancellationToken))
            await DeletePrimaryIpAsync(token, state.PrimaryIpv4Id, cancellationToken);
        DeleteLocalState(statePath, state.KnownHostsFile);
    }

    /// <summary>
    /// SSH is refused or times out for the first minutes of every new server while it boots, so a failure is expected and
    /// stays silent until it has lasted this long.
    /// </summary>
    private static readonly TimeSpan SshFailureGracePeriod = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan SshFailureReportInterval = TimeSpan.FromMinutes(1);

    private async Task WaitUntilReadyAsync(AppSettings settings, WorkerState state, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!HumanReadableValues.TryParseDuration(settings.Processing.RemoteSsh.Hetzner.BootstrapTimeout, out var timeout))
            throw new InvalidOperationException("The configured Hetzner bootstrap timeout is invalid.");
        var waitStartedAt = _utcNow();
        var deadline = waitStartedAt + timeout;
        (int Percent, string Stage)? lastProgress = null;
        DateTimeOffset? phaseStartedAt = null;
        DateTimeOffset? lastHeartbeatAt = null;
        // The current run of failed checks: when it began, and when it was last reported.
        DateTimeOffset? failingSince = null;
        DateTimeOffset? lastFailureReportAt = null;

        void Failed(string reason)
        {
            var now = _utcNow();
            failingSince ??= now;
            if (now - failingSince < SshFailureGracePeriod) return;
            if (lastFailureReportAt is not null && now - lastFailureReportAt < SshFailureReportInterval) return;
            lastFailureReportAt = now;
            progress?.Report($"Still waiting for the worker's SSH ({HumanReadableValues.FormatDuration(now - failingSince.Value)}): {reason}");
        }

        const string probeCommand = $"if test -f {BootstrapCompletePath}; then printf 'percent=100\\nstage=Ready\\n'; elif test -f {BootstrapProgressPath}; then cat {BootstrapProgressPath}; else printf 'percent=0\\nstage=Starting\\n'; fi";
        while (_utcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = OpenSshConnectionArguments.Build(settings.Processing.RemoteSsh, acceptNewHostKey: true, userOverride: "root");
            arguments.Add(state.Address);
            arguments.Add(probeCommand);
            try
            {
                var result = await RunReadinessProbeAsync(settings.Tools.SshPath, arguments, cancellationToken);
                if (result is null)
                {
                    Failed($"status check exceeded {_readinessProbeTimeout.TotalSeconds:0}s");
                }
                else if (result.ExitCode == 0 && TryParseBootstrapProgress(result.StandardOutput, out var currentProgress))
                {
                    failingSince = null;
                    if (currentProgress.Percent >= 100)
                    {
                        var processingArguments = OpenSshConnectionArguments.Build(settings.Processing.RemoteSsh, acceptNewHostKey: true);
                        processingArguments.Add(state.Address);
                        processingArguments.Add("true");
                        var processingResult = await RunReadinessProbeAsync(settings.Tools.SshPath, processingArguments, cancellationToken);
                        if (processingResult?.ExitCode == 0)
                        {
                            progress?.Report($"Worker ready after {HumanReadableValues.FormatDuration(_utcNow() - waitStartedAt)}.");
                            return;
                        }

                        Failed(processingResult is null
                            ? $"videoopt SSH check exceeded {_readinessProbeTimeout.TotalSeconds:0}s"
                            : FirstNonEmpty(processingResult.StandardError, processingResult.StandardOutput, $"videoopt SSH exited with code {processingResult.ExitCode}"));
                    }

                    var observedAt = _utcNow();
                    if (lastProgress != currentProgress)
                    {
                        progress?.Report($"Worker setup: {currentProgress.Percent}%, {DescribeStage(currentProgress.Stage)}...");
                        lastProgress = currentProgress;
                        phaseStartedAt = observedAt;
                        lastHeartbeatAt = observedAt;
                    }
                    else if (phaseStartedAt is not null && lastHeartbeatAt is not null && observedAt - lastHeartbeatAt >= _progressHeartbeatInterval)
                    {
                        progress?.Report($"Worker setup: {currentProgress.Percent}%, {DescribeStage(currentProgress.Stage)} ({HumanReadableValues.FormatDuration(observedAt - phaseStartedAt.Value)} in this step)...");
                        lastHeartbeatAt = observedAt;
                    }
                }
                else if (result is not null)
                {
                    Failed(FirstNonEmpty(result.StandardError, result.StandardOutput, $"exited with code {result.ExitCode}"));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                Failed(exception.Message);
            }
            await _delay(TimeSpan.FromSeconds(15), cancellationToken);
        }
        throw new TimeoutException($"Hetzner worker {state.ServerId} did not finish bootstrap within {settings.Processing.RemoteSsh.Hetzner.BootstrapTimeout}. It was retained for inspection.");
    }

    // The script's stage names ("Building FFmpeg") start a sentence; here they continue one.
    private static string DescribeStage(string stage) => stage.Length == 0 ? stage : char.ToLowerInvariant(stage[0]) + stage[1..];

    private async Task<ExternalProcessResult?> RunReadinessProbeAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = _processes.RunAsync(new ExternalProcessRequest(executable, arguments), probeCancellation.Token);
        try
        {
            return await runTask.WaitAsync(_readinessProbeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            probeCancellation.Cancel();
            await AwaitProcessTerminationAsync(runTask);
            probeCancellation.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            probeCancellation.Cancel();
            await AwaitProcessTerminationAsync(runTask);
            probeCancellation.Dispose();
            return null;
        }
        catch (OperationCanceledException)
        {
            probeCancellation.Cancel();
            _ = ObserveProcessAsync(runTask, probeCancellation);
            throw;
        }
        finally
        {
            if (runTask.IsCompleted) probeCancellation.Dispose();
        }
    }

    private static async Task AwaitProcessTerminationAsync(Task<ExternalProcessResult> processTask)
    {
        try
        {
            await processTask.WaitAsync(ProcessTerminationGracePeriod).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
        }
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "unknown SSH failure";

    private static async Task ObserveProcessAsync(Task<ExternalProcessResult> processTask, CancellationTokenSource cancellation)
    {
        try
        {
            await processTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static bool TryParseBootstrapProgress(string output, out (int Percent, string Stage) progress)
    {
        var percent = -1;
        var stage = string.Empty;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (key.Equals("percent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, CultureInfo.InvariantCulture, out var parsedPercent)) percent = parsedPercent;
            else if (key.Equals("stage", StringComparison.OrdinalIgnoreCase)) stage = value;
        }

        if (percent is < 0 or > 100 || string.IsNullOrWhiteSpace(stage))
        {
            progress = default;
            return false;
        }

        progress = (percent, stage);
        return true;
    }

    private static void ApplyConnection(AppSettings settings, WorkerState state)
    {
        settings.Processing.RemoteSsh.Host = state.Address;
        settings.Processing.RemoteSsh.User = "videoopt";
        settings.Processing.RemoteSsh.KnownHostsFile = state.KnownHostsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(state.KnownHostsFile)!);
    }

    private async Task<List<ServerSnapshot>> ListManagedServersAsync(string token, string controller, CancellationToken cancellationToken)
    {
        var selector = Uri.EscapeDataString($"{ManagedLabel}=true,{ControllerLabel}={controller}");
        using var document = await SendJsonAsync(token, HttpMethod.Get, $"servers?label_selector={selector}", null, cancellationToken);
        return document.RootElement.GetProperty("servers").EnumerateArray().Select(ParseServer).ToList();
    }

    private async Task<ServerSnapshot?> GetServerAsync(string token, long id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, HttpMethod.Get, $"servers/{id.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var document = await ReadSuccessJsonAsync(response, cancellationToken);
        return ParseServer(document.RootElement.GetProperty("server"));
    }

    private async Task<long?> DeleteServerAsync(string token, long id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, HttpMethod.Delete, $"servers/{id.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var document = await ReadSuccessJsonAsync(response, cancellationToken);
        return document.RootElement.TryGetProperty("action", out var action) ? action.GetProperty("id").GetInt64() : null;
    }

    private async Task WaitForActionAsync(string token, long actionId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var document = await SendJsonAsync(token, HttpMethod.Get, $"actions/{actionId.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
            var action = document.RootElement.GetProperty("action");
            var status = action.GetProperty("status").GetString();
            if (status == "success") return;
            if (status == "error")
            {
                var error = action.TryGetProperty("error", out var errorElement) ? errorElement : default;
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString() ?? "unknown action error"
                    : "unknown action error";
                var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeElement)
                    ? codeElement.GetString()
                    : null;
                throw new HetznerActionException(code, message);
            }
            await _delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException($"Hetzner action {actionId} did not complete in time.");
    }

    private async Task WaitForServerDeletionAsync(string token, long serverId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await GetServerAsync(token, serverId, cancellationToken) is null) return;
            await _delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException($"Hetzner server {serverId} still exists after its delete action completed.");
    }

    private async Task SetPrimaryIpAutoDeleteAsync(string token, long id, string controller, CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("auto_delete", true);
            writer.WritePropertyName("labels");
            writer.WriteStartObject();
            writer.WriteString(ManagedLabel, "true");
            writer.WriteString(ControllerLabel, controller);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var document = await SendJsonAsync(token, HttpMethod.Put, $"primary_ips/{id.ToString(CultureInfo.InvariantCulture)}", payload.ToArray(), cancellationToken);
    }

    private async Task<bool> DeleteOrphanedPrimaryIpsAsync(string token, string controller, CancellationToken cancellationToken)
    {
        var selector = Uri.EscapeDataString($"{ManagedLabel}=true,{ControllerLabel}={controller}");
        using var document = await SendJsonAsync(token, HttpMethod.Get, $"primary_ips?label_selector={selector}", null, cancellationToken);
        var ids = document.RootElement.GetProperty("primary_ips").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ToArray();
        if (ids.Length > 1) throw new InvalidOperationException("More than one managed Primary IPv4 has this controller label; refusing automatic deletion.");
        if (ids.Length == 0) return false;
        await DeletePrimaryIpAsync(token, ids[0], cancellationToken);
        return true;
    }

    private async Task<bool> PrimaryIpExistsAsync(string token, long id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, HttpMethod.Get, $"primary_ips/{id.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        using var document = await ReadSuccessJsonAsync(response, cancellationToken);
        return document.RootElement.TryGetProperty("primary_ip", out _);
    }

    private async Task DeletePrimaryIpAsync(string token, long id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, HttpMethod.Delete, $"primary_ips/{id.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode) throw await ApiExceptionAsync(response, cancellationToken);
    }

    private async Task<JsonDocument> SendJsonAsync(string token, HttpMethod method, string path, byte[]? payload, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, method, path, payload, cancellationToken);
        return await ReadSuccessJsonAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, byte[]? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("video-optimiser/managed-worker");
        if (payload is not null) request.Content = new ByteArrayContent(payload) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<JsonDocument> ReadSuccessJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) throw await ApiExceptionAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task<HetznerApiException> ApiExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = body;
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var codeElement)) code = codeElement.GetString();
                if (error.TryGetProperty("message", out var value)) message = value.GetString() ?? body;
            }
        }
        catch (JsonException) { }
        return new HetznerApiException(response.StatusCode, code, message);
    }

    private static ServerSnapshot ParseServer(JsonElement server)
    {
        var publicNet = server.GetProperty("public_net");
        var ipv4 = publicNet.GetProperty("ipv4");
        var labels = server.GetProperty("labels").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
        DateTimeOffset? created = server.TryGetProperty("created", out var createdElement) && DateTimeOffset.TryParse(createdElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
        // Informational only: shown by worker status, never used to decide anything.
        var serverType = server.TryGetProperty("server_type", out var typeElement) && typeElement.ValueKind == JsonValueKind.Object &&
                         typeElement.TryGetProperty("name", out var typeName) && typeName.ValueKind == JsonValueKind.String
            ? typeName.GetString()!
            : string.Empty;
        var location = server.TryGetProperty("datacenter", out var datacenter) && datacenter.ValueKind == JsonValueKind.Object &&
                       datacenter.TryGetProperty("location", out var locationElement) && locationElement.ValueKind == JsonValueKind.Object &&
                       locationElement.TryGetProperty("name", out var locationName) && locationName.ValueKind == JsonValueKind.String
            ? locationName.GetString()!
            : string.Empty;
        return new ServerSnapshot(
            server.GetProperty("id").GetInt64(),
            server.GetProperty("name").GetString() ?? string.Empty,
            server.GetProperty("status").GetString() ?? "unknown",
            ipv4.GetProperty("ip").GetString() ?? throw new InvalidOperationException("Hetzner server has no public IPv4 address."),
            ipv4.TryGetProperty("id", out var primaryId) && primaryId.ValueKind == JsonValueKind.Number ? primaryId.GetInt64() : 0,
            labels,
            created,
            serverType,
            location);
    }

    private static void VerifyOwnership(ServerSnapshot server, string controller)
    {
        if (server.Labels.GetValueOrDefault(ManagedLabel) != "true" || server.Labels.GetValueOrDefault(ControllerLabel) != controller)
            throw new InvalidOperationException($"Hetzner server {server.Id} does not have the expected ownership labels; refusing to reuse or delete it.");
    }

    private static string GetToken(AppSettings settings)
    {
        var hetzner = settings.Processing.RemoteSsh.Hetzner;
        return SecretLoader.Load(hetzner.ApiTokenEnvironmentVariable, hetzner.ApiTokenFile)
            ?? throw new InvalidOperationException($"Hetzner API token '{hetzner.ApiTokenEnvironmentVariable}' was not found in the environment or '{hetzner.ApiTokenFile}'. Add it to the ignored token file or set the environment variable.");
    }

    private static string ControllerId(string databasePath)
    {
        var normalized = Path.GetFullPath(databasePath).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..24];
    }

    private static string StatePath(string databasePath) => Path.GetFullPath(databasePath) + ".hetzner-worker.json";
    private static string KnownHostsPath(string databasePath, long serverId) => Path.GetFullPath(databasePath) + $".hetzner-{serverId.ToString(CultureInfo.InvariantCulture)}.known_hosts";

    private static async Task<FileStream> AcquireLockAsync(string databasePath, CancellationToken cancellationToken)
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

    private static async Task<WorkerState?> ReadStateAsync(string path, CancellationToken cancellationToken)
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

    private static async Task WriteStateAsync(string path, WorkerState state, CancellationToken cancellationToken)
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

    private static void DeleteLocalState(string statePath, string knownHostsFile)
    {
        if (File.Exists(statePath)) File.Delete(statePath);
        if (!string.IsNullOrWhiteSpace(knownHostsFile) && File.Exists(knownHostsFile)) File.Delete(knownHostsFile);
    }

    private void RequireManaged(AppSettings settings)
    {
        if (!IsManaged(settings)) throw new InvalidOperationException("The remote SSH lifecycle is not configured for Hetzner.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    private readonly record struct ServerSnapshot(long Id, string Name, string Status, string Address, long PrimaryIpv4Id, IReadOnlyDictionary<string, string> Labels, DateTimeOffset? CreatedUtc, string ServerType, string Location);
    private readonly record struct LocationStatus(bool Available, bool Deprecated);
    private sealed record ServerTypeOffer(string Name, string Architecture, int Cores, decimal MemoryGiB, bool Deprecated, IReadOnlyDictionary<string, LocationStatus> Locations, IReadOnlyDictionary<string, decimal> HourlyGross);
    private readonly record struct PlacementCandidate(string ServerType, string Location, decimal? HourlyGross)
    {
        public override string ToString() => $"{ServerType} in {Location}";
    }
    private sealed class TypeReport(string serverType)
    {
        public List<string> LocationsTried { get; } = [];
        public List<string> Notes { get; } = [];
        public RemoteWorkerTypeOutcome ToOutcome() => new(serverType, LocationsTried, string.Join("; ", Notes));
    }
    private sealed class HetznerApiException(HttpStatusCode statusCode, string? code, string message) : InvalidOperationException($"Hetzner API returned {(int)statusCode} ({statusCode}): {message}")
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public string? Code { get; } = code;
        public string ProviderMessage { get; } = message;
    }
    private sealed class HetznerActionException(string? code, string message) : InvalidOperationException(message)
    {
        public string? Code { get; } = code;
    }
    private sealed record WorkerState(long ServerId, long PrimaryIpv4Id, string Name, string Address, string Location, string ServerType, string KnownHostsFile, string Controller, DateTimeOffset? CreatedUtc)
    {
        public RemoteWorkerInfo ToInfo(string status) => new(ServerId, Name, Address, status, CreatedUtc, ServerType, Location);
    }
}
