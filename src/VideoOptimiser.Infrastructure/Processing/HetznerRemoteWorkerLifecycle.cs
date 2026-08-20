using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;

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

    public async Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
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
            progress?.Report("Creating managed Hetzner worker.");
            var script = await File.ReadAllTextAsync(settings.Processing.RemoteSsh.Hetzner.BootstrapScriptPath, cancellationToken);
            state = await CreateAsync(token, settings, controller, statePath, script, progress, cancellationToken);
            progress?.Report($"Created Hetzner server {state.ServerId} at {state.Address}; bootstrap is running.");
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
                    CreatedUtc = server.Value.CreatedUtc
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
            string.Empty,
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
        CancellationToken cancellationToken)
    {
        var hetzner = settings.Processing.RemoteSsh.Hetzner;
        if (Encoding.UTF8.GetByteCount(bootstrapScript) > 32 * 1024) throw new InvalidOperationException("The Hetzner bootstrap script exceeds the 32 KiB user-data limit.");
        var name = $"{hetzner.ServerNamePrefix}-{controller[..12]}".ToLowerInvariant();
        var candidates = await PreflightLocationsAsync(token, hetzner, progress, cancellationToken);
        var failures = new List<string>();
        Exception? lastPlacementFailure = null;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            progress?.Report($"Attempting Hetzner server creation in {candidate.Name} (preflight available; recommended={candidate.Recommended}).");
            JsonDocument document;
            try
            {
                document = await SendJsonAsync(token, HttpMethod.Post, "servers", CreateServerPayload(name, hetzner, candidate.Name, controller, bootstrapScript), cancellationToken);
            }
            catch (HetznerApiException exception) when (IsPlacementUnavailable(exception))
            {
                lastPlacementFailure = exception;
                var reason = DescribeApiFailure(exception);
                failures.Add($"{candidate.Name} ({reason})");
                if (candidateIndex + 1 < candidates.Count)
                {
                    progress?.Report($"Hetzner location {candidate.Name} placement unavailable ({reason}); falling back to {candidates[candidateIndex + 1].Name}.");
                    continue;
                }
                break;
            }

            using (document)
            {
                var serverElement = document.RootElement.GetProperty("server");
                var server = ParseServer(serverElement);
                var knownHostsFile = KnownHostsPath(statePath[..^".hetzner-worker.json".Length], server.Id);
                var state = new WorkerState(server.Id, server.PrimaryIpv4Id, server.Name, server.Address, candidate.Name, knownHostsFile, controller, server.CreatedUtc);

                // A failed POST never reaches this point, so no local state is left for a failed placement attempt.
                await WriteStateAsync(statePath, state, cancellationToken);

                try
                {
                    if (server.PrimaryIpv4Id > 0) await SetPrimaryIpAutoDeleteAsync(token, server.PrimaryIpv4Id, controller, cancellationToken);
                    if (document.RootElement.TryGetProperty("action", out var action) && action.TryGetProperty("id", out var actionId))
                    {
                        progress?.Report($"Hetzner server {server.Id} allocated in {candidate.Name}; waiting for server provisioning to finish.");
                        await WaitForActionAsync(token, actionId.GetInt64(), cancellationToken);
                    }
                }
                catch (HetznerActionException exception) when (IsPlacementUnavailable(exception))
                {
                    var reason = $"Hetzner action failed ({exception.Code ?? "unknown"}): {exception.Message}";
                    failures.Add($"{candidate.Name} ({reason})");
                    lastPlacementFailure = exception;
                    progress?.Report($"Hetzner location {candidate.Name} provisioning failed ({reason}); cleaning up before fallback.");
                    await CleanupFailedCreateAsync(token, statePath, state, cancellationToken);
                    if (candidateIndex + 1 < candidates.Count)
                    {
                        progress?.Report($"Hetzner location {candidate.Name} cleaned up; falling back to {candidates[candidateIndex + 1].Name}.");
                        continue;
                    }
                    break;
                }
                return state;
            }
        }

        throw new InvalidOperationException($"Hetzner server creation exhausted all candidate locations: {string.Join("; ", failures)}.", lastPlacementFailure);
    }

    private async Task<IReadOnlyList<LocationCandidate>> PreflightLocationsAsync(
        string token,
        HetznerSettings hetzner,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"Checking Hetzner {hetzner.ServerType} location availability before creation.");
        JsonDocument document;
        try
        {
            document = await SendJsonAsync(token, HttpMethod.Get, $"server_types?name={Uri.EscapeDataString(hetzner.ServerType)}", null, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw PreflightFailure(hetzner.ServerType, $"the response was not valid JSON: {exception.Message}", exception);
        }
        using (document)
        {
            return ParsePreflightLocations(document, hetzner, progress);
        }
    }

    private static List<LocationCandidate> ParsePreflightLocations(
        JsonDocument document,
        HetznerSettings hetzner,
        IProgress<string>? progress)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("server_types", out var serverTypes) || serverTypes.ValueKind != JsonValueKind.Array)
            throw PreflightFailure(hetzner.ServerType, "the response did not contain a server_types array");

        var matches = new List<JsonElement>();
        foreach (var serverType in serverTypes.EnumerateArray())
        {
            if (serverType.ValueKind != JsonValueKind.Object || !serverType.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                throw PreflightFailure(hetzner.ServerType, "the server_types array contained an entry without a string name");
            if (string.Equals(name.GetString(), hetzner.ServerType, StringComparison.OrdinalIgnoreCase)) matches.Add(serverType);
        }
        if (matches.Count == 0) throw new InvalidOperationException($"Hetzner server type '{hetzner.ServerType}' was not found during location preflight; no server creation was attempted.");
        if (matches.Count > 1) throw new InvalidOperationException($"Hetzner server type lookup for '{hetzner.ServerType}' returned multiple matches during location preflight; no server creation was attempted.");

        if (!matches[0].TryGetProperty("locations", out var locationArray) || locationArray.ValueKind != JsonValueKind.Array)
            throw PreflightFailure(hetzner.ServerType, "the matching server type did not contain a locations array");

        var locations = new Dictionary<string, LocationStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locationArray.EnumerateArray())
        {
            if (location.ValueKind != JsonValueKind.Object ||
                !location.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                throw PreflightFailure(hetzner.ServerType, "the locations array contained an entry without a non-empty string name");
            if (!location.TryGetProperty("available", out var available) || available.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw PreflightFailure(hetzner.ServerType, $"location '{name.GetString()}' did not contain a boolean available field");
            if (location.TryGetProperty("recommended", out var recommendedField) && recommendedField.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw PreflightFailure(hetzner.ServerType, $"location '{name.GetString()}' did not contain a boolean recommended field");
            var locationName = name.GetString()!;
            if (!locations.TryAdd(locationName, new LocationStatus(
                    available.GetBoolean(),
                    location.TryGetProperty("recommended", out var recommended) && recommended.GetBoolean())))
                throw PreflightFailure(hetzner.ServerType, $"the locations array contained duplicate location '{locationName}'");
        }
        var statuses = new List<string>();
        var candidates = new List<LocationCandidate>();
        var seenConfiguredLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredLocation in hetzner.Locations)
        {
            if (!seenConfiguredLocations.Add(configuredLocation)) continue;
            if (!locations.TryGetValue(configuredLocation, out var status))
            {
                statuses.Add($"{configuredLocation}=unsupported");
                progress?.Report($"Hetzner location {configuredLocation}: unsupported for {hetzner.ServerType}.");
                continue;
            }

            statuses.Add($"{configuredLocation}=available:{status.Available},recommended:{status.Recommended}");
            progress?.Report($"Hetzner location {configuredLocation}: available={status.Available}, recommended={status.Recommended}.");
            if (status.Available)
            {
                // Configuration order is the explicit preference. Recommendations are advisory and are
                // reported, but never reorder an explicitly ordered locations list.
                candidates.Add(new LocationCandidate(configuredLocation, status.Recommended));
            }
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException($"No configured Hetzner locations are currently indicated available for {hetzner.ServerType}; no server creation was attempted. Status: {string.Join(", ", statuses)}.");

        progress?.Report($"Hetzner location preflight selected candidates in configured order: {string.Join(", ", candidates.Select(candidate => candidate.Name))}.");
        return candidates;
    }

    private static InvalidOperationException PreflightFailure(string serverType, string reason, Exception? innerException = null) =>
        new($"Hetzner {serverType} location preflight failed: {reason}; no server creation was attempted.", innerException);

    private static byte[] CreateServerPayload(string name, HetznerSettings hetzner, string location, string controller, string bootstrapScript)
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("server_type", hetzner.ServerType);
            writer.WriteString("image", hetzner.Image);
            writer.WriteString("location", location);
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
        $"HTTP {(int)exception.StatusCode} {exception.StatusCode} ({exception.Code ?? "unknown"}): {exception.Message}";

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

    private async Task WaitUntilReadyAsync(AppSettings settings, WorkerState state, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!HumanReadableValues.TryParseDuration(settings.Processing.RemoteSsh.Hetzner.BootstrapTimeout, out var timeout))
            throw new InvalidOperationException("The configured Hetzner bootstrap timeout is invalid.");
        var deadline = _utcNow() + timeout;
        var reportedConnectionWait = false;
        string? lastFailure = null;
        (int Percent, string Stage)? lastProgress = null;
        DateTimeOffset? phaseStartedAt = null;
        DateTimeOffset? lastHeartbeatAt = null;
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
                    lastFailure = $"status check exceeded {_readinessProbeTimeout.TotalSeconds:0}s";
                    progress?.Report($"Worker SSH check {lastFailure}; retrying.");
                }
                else if (result.ExitCode == 0 && TryParseBootstrapProgress(result.StandardOutput, out var currentProgress))
                {
                    if (currentProgress.Percent >= 100)
                    {
                        progress?.Report("Managed worker bootstrap completed; verifying videoopt SSH access.");
                        var processingArguments = OpenSshConnectionArguments.Build(settings.Processing.RemoteSsh, acceptNewHostKey: true);
                        processingArguments.Add(state.Address);
                        processingArguments.Add("true");
                        var processingResult = await RunReadinessProbeAsync(settings.Tools.SshPath, processingArguments, cancellationToken);
                        if (processingResult?.ExitCode == 0)
                        {
                            progress?.Report("Managed worker bootstrap completed and SSH is ready.");
                            return;
                        }

                        lastFailure = processingResult is null
                            ? $"videoopt SSH check exceeded {_readinessProbeTimeout.TotalSeconds:0}s"
                            : FirstNonEmpty(processingResult.StandardError, processingResult.StandardOutput, $"videoopt SSH exited with code {processingResult.ExitCode}");
                        progress?.Report($"Bootstrap is complete, but videoopt SSH is not ready: {lastFailure}; retrying.");
                    }

                    var observedAt = _utcNow();
                    if (lastProgress != currentProgress)
                    {
                        progress?.Report($"Worker bootstrap {currentProgress.Percent}% — {currentProgress.Stage}.");
                        lastProgress = currentProgress;
                        phaseStartedAt = observedAt;
                        lastHeartbeatAt = observedAt;
                    }
                    else if (phaseStartedAt is not null && lastHeartbeatAt is not null && observedAt - lastHeartbeatAt >= _progressHeartbeatInterval)
                    {
                        progress?.Report($"Worker bootstrap {currentProgress.Percent}% — {currentProgress.Stage} (elapsed {FormatElapsed(observedAt - phaseStartedAt.Value)}).");
                        lastHeartbeatAt = observedAt;
                    }
                }
                else if (result is not null)
                {
                    lastFailure = FirstNonEmpty(result.StandardError, result.StandardOutput, $"exited with code {result.ExitCode}");
                    progress?.Report($"Worker SSH check failed: {lastFailure}; retrying.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                lastFailure = exception.Message;
                progress?.Report($"Worker SSH check failed: {lastFailure}; retrying.");
            }
            if (!reportedConnectionWait)
            {
                var detail = string.IsNullOrWhiteSpace(lastFailure) ? string.Empty : $" Last check: {lastFailure}.";
                progress?.Report($"Waiting for the worker SSH service and bootstrap progress; the first build can take a while.{detail}");
                reportedConnectionWait = true;
            }
            await _delay(TimeSpan.FromSeconds(15), cancellationToken);
        }
        throw new TimeoutException($"Hetzner worker {state.ServerId} did not finish bootstrap within {settings.Processing.RemoteSsh.Hetzner.BootstrapTimeout}. It was retained for inspection.");
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        if (elapsed.TotalMinutes >= 1) return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        return $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";
    }

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
        return new ServerSnapshot(
            server.GetProperty("id").GetInt64(),
            server.GetProperty("name").GetString() ?? string.Empty,
            server.GetProperty("status").GetString() ?? "unknown",
            ipv4.GetProperty("ip").GetString() ?? throw new InvalidDataException("Hetzner server has no public IPv4 address."),
            ipv4.TryGetProperty("id", out var primaryId) && primaryId.ValueKind == JsonValueKind.Number ? primaryId.GetInt64() : 0,
            labels,
            created);
    }

    private static void VerifyOwnership(ServerSnapshot server, string controller)
    {
        if (server.Labels.GetValueOrDefault(ManagedLabel) != "true" || server.Labels.GetValueOrDefault(ControllerLabel) != controller)
            throw new InvalidOperationException($"Hetzner server {server.Id} does not have the expected ownership labels; refusing to reuse or delete it.");
    }

    private static string GetToken(AppSettings settings)
    {
        var variable = settings.Processing.RemoteSsh.Hetzner.ApiTokenEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(variable) && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } token) return token;
        var tokenFile = settings.Processing.RemoteSsh.Hetzner.ApiTokenFile;
        if (!string.IsNullOrWhiteSpace(tokenFile) && File.Exists(tokenFile) && ReadDotEnvValue(tokenFile, variable) is { Length: > 0 } fileToken) return fileToken;
        throw new InvalidOperationException($"Hetzner API token '{variable}' was not found in the environment or '{tokenFile}'. Add it to the ignored token file or set the environment variable.");
    }

    internal static string? ReadDotEnvValue(string path, string variable)
    {
        string? result = null;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var separator = line.IndexOf('=');
            if (separator < 1 || !string.Equals(line[..separator].Trim(), variable, StringComparison.Ordinal)) continue;
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
            result = value;
        }
        return result;
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

    private readonly record struct ServerSnapshot(long Id, string Name, string Status, string Address, long PrimaryIpv4Id, IReadOnlyDictionary<string, string> Labels, DateTimeOffset? CreatedUtc);
    private readonly record struct LocationStatus(bool Available, bool Recommended);
    private readonly record struct LocationCandidate(string Name, bool Recommended);
    private sealed class HetznerApiException(HttpStatusCode statusCode, string? code, string message) : InvalidOperationException($"Hetzner API returned {(int)statusCode} ({statusCode}): {message}")
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public string? Code { get; } = code;
    }
    private sealed class HetznerActionException(string? code, string message) : InvalidOperationException(message)
    {
        public string? Code { get; } = code;
    }
    private sealed record WorkerState(long ServerId, long PrimaryIpv4Id, string Name, string Address, string Location, string KnownHostsFile, string Controller, DateTimeOffset? CreatedUtc)
    {
        public RemoteWorkerInfo ToInfo(string status) => new(ServerId, Name, Address, status, CreatedUtc);
    }
}
