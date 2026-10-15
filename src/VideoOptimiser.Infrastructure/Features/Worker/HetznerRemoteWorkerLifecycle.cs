using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Hetzner;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.Infrastructure.Features.Worker;

/// <summary>
/// The worker's lifecycle: finds, creates, waits for and deletes the one managed Hetzner server, and decides in what order.
/// What each step does lives in its own class: <see cref="HetznerCloudClient"/> talks to Hetzner,
/// <see cref="HetznerPlacement"/> chooses where, <see cref="WorkerReadiness"/> waits for bootstrap and
/// <see cref="WorkerStateStore"/> keeps the local record.
/// </summary>
public sealed class HetznerRemoteWorkerLifecycle : IRemoteWorkerLifecycle, IDisposable
{
    private readonly HetznerCloudClient _cloud;
    private readonly HetznerPlacement _placement;
    private readonly WorkerReadiness _readiness;
    private readonly TimeSpan _checkTimeout;
    private const string MoreThanOneServerMessage = "More than one Hetzner worker has this controller label; refusing to choose or delete either one.";

    public HetznerRemoteWorkerLifecycle(IExternalProcessRunner processes)
        : this(processes, new HttpClient { BaseAddress = new Uri(HetznerHttp.ApiBase) }, Task.Delay, ownsHttpClient: true)
    {
    }

    internal HetznerRemoteWorkerLifecycle(
        IExternalProcessRunner processes,
        HttpClient http,
        Func<TimeSpan, CancellationToken, Task> delay,
        bool ownsHttpClient = false,
        TimeSpan? readinessProbeTimeout = null,
        TimeSpan? progressHeartbeatInterval = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? checkTimeout = null)
    {
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(15);
        _cloud = new HetznerCloudClient(http, ownsHttpClient, delay);
        _placement = new HetznerPlacement(_cloud);
        _readiness = new WorkerReadiness(processes, delay, readinessProbeTimeout, progressHeartbeatInterval, utcNow);
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
        await using var lifecycleLock = await WorkerStateStore.AcquireLockAsync(databasePath, progress, cancellationToken);
        var token = GetToken(settings);
        var controller = ControllerId(databasePath);
        var statePath = WorkerStateStore.StatePath(databasePath);
        var state = await ResolveStateAsync(token, controller, statePath, cancellationToken);
        if (state is null)
        {
            _ = await _cloud.DeleteOrphanedPrimaryIpsAsync(token, controller, cancellationToken);
            progress?.Report("No Hetzner worker exists yet.");
            // cloud-init runs the script with bash, which fails on CRLF; a Windows checkout with core.autocrlf has CRLF.
            var script = (await File.ReadAllTextAsync(settings.Processing.RemoteSsh.Hetzner.BootstrapScriptPath, cancellationToken)).ReplaceLineEndings("\n");
            state = await CreateAsync(token, settings, controller, statePath, script, progress, confirmCreation, cancellationToken);
            progress?.Report($"Created the Hetzner worker {state.Name}; it is bootstrapping.");
            progress?.Report("Bootstrapping the worker: it builds libvmaf, SVT-AV1 and FFmpeg from source and installs ab-av1, which takes several minutes. Progress follows.");
        }
        else
        {
            progress?.Report($"Reusing the Hetzner worker {state.Name}; checking its bootstrap progress.");
        }

        ApplyConnection(settings, state);
        await _readiness.WaitUntilReadyAsync(settings, state, progress, cancellationToken);
        return state.ToInfo("ready");
    }

    public async Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await WorkerStateStore.ReadStateAsync(WorkerStateStore.StatePath(databasePath), cancellationToken))?.ToInfo(RemoteWorkerStatuses.Recorded);
        }
        catch (FileNotFoundException)
        {
            // Without the lock, a concurrent worker delete can remove the file between the existence check and the read.
            return null;
        }
    }

    public async Task<WorkerCheck> CheckAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default)
    {
        WorkerState? state;
        try
        {
            state = await WorkerStateStore.ReadStateAsync(WorkerStateStore.StatePath(databasePath), cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // Without the lock, a concurrent worker delete can remove the file between the existence check and the read.
            state = null;
        }

        var hetzner = settings.Processing.RemoteSsh.Hetzner;
        var token = SecretLoader.Load(hetzner.ApiTokenEnvironmentVariable, hetzner.ApiTokenFile);
        if (token is null) return new WorkerCheck(state?.ToInfo(RemoteWorkerStatuses.Recorded), null, "no API token is set");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_checkTimeout);
        try
        {
            return await LookUpAsync(settings, databasePath, token, ControllerId(databasePath), state, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WorkerCheck(state?.ToInfo(RemoteWorkerStatuses.Recorded), null, "Hetzner didn't answer within 15 seconds");
        }
        // A status read never fails for what Hetzner said or didn't: the card shows the record and the reason. KeyNotFound is
        // an answer missing a field the parser needs.
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            return new WorkerCheck(state?.ToInfo(RemoteWorkerStatuses.Recorded), null, exception.Message);
        }
    }

    /// <summary>
    /// The read half of <see cref="ResolveStateAsync"/> with every write left out. A recorded server that is gone is not
    /// cleaned up here: whether its IPv4 still bills is the answer, and <see cref="DeleteAsync"/> removes it. A server it finds
    /// also becomes the connection in <paramref name="settings"/> (in memory only), so doctor's host checks reach the worker.
    /// </summary>
    private async Task<WorkerCheck> LookUpAsync(AppSettings settings, string databasePath, string token, string controller, WorkerState? state, CancellationToken cancellationToken)
    {
        if (state is not null)
        {
            if (await _cloud.GetServerAsync(token, state.ServerId, cancellationToken) is { } server)
            {
                HetznerCloudClient.VerifyOwnership(server, controller);
                var refreshed = Refreshed(state, server);
                ApplyConnection(settings, refreshed);
                return new WorkerCheck(refreshed.ToInfo(server.Status), server.HourlyGross, null);
            }
            if (state.PrimaryIpv4Id > 0 && await _cloud.PrimaryIpExistsAsync(token, state.PrimaryIpv4Id, cancellationToken))
                return new WorkerCheck(state.ToInfo(RemoteWorkerStatuses.PrimaryIpRetained), null, null);
            // Otherwise the record is stale; the label lookup says whether Hetzner has a server for this database anyway.
        }

        var servers = await _cloud.ListManagedServersAsync(token, controller, cancellationToken);
        if (servers.Count > 1) throw new InvalidOperationException(MoreThanOneServerMessage);
        if (servers.Count == 0) return new WorkerCheck(null, null, null);
        HetznerCloudClient.VerifyOwnership(servers[0], controller);
        var found = servers[0];
        // The record that ResolveStateAsync would recover; here it only names the known-hosts file and is never written.
        ApplyConnection(settings, new WorkerState(found.Id, found.PrimaryIpv4Id, found.Name, found.Address, found.Location, found.ServerType, WorkerStateStore.KnownHostsPath(databasePath, found.Id), controller, found.CreatedUtc));
        return new WorkerCheck(new RemoteWorkerInfo(found.Id, found.Name, found.Address, found.Status, found.CreatedUtc, found.ServerType, found.Location), found.HourlyGross, null);
    }

    public async Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireManaged(settings);
        await using var lifecycleLock = await WorkerStateStore.AcquireLockAsync(databasePath, progress, cancellationToken);
        var token = GetToken(settings);
        var controller = ControllerId(databasePath);
        var statePath = WorkerStateStore.StatePath(databasePath);
        var state = await ResolveStateAsync(token, controller, statePath, cancellationToken, retainStateWhenServerMissing: true);
        if (state is null)
        {
            var deletedOrphans = await _cloud.DeleteOrphanedPrimaryIpsAsync(token, controller, cancellationToken);
            if (deletedOrphans) progress?.Report("Deleted an IPv4 address an earlier worker left behind.");
            return deletedOrphans;
        }

        var server = await _cloud.GetServerAsync(token, state.ServerId, cancellationToken);
        if (server is not null) HetznerCloudClient.VerifyOwnership(server.Value, controller);
        // A server deleted in the Hetzner console leaves only a record here (and maybe its IPv4); claiming to delete it would
        // be wrong, and the user needs to know the record was a leftover.
        progress?.Report(server is null
            ? $"The worker {state.Name} is already gone from Hetzner; its record on this PC is removed."
            : $"Deleting the worker {state.Name}.");
        if (server is not null)
        {
            var actionId = await _cloud.DeleteServerAsync(token, state.ServerId, cancellationToken);
            if (actionId is not null) await _cloud.WaitForActionAsync(token, actionId.Value, cancellationToken);
            await _cloud.WaitForServerDeletionAsync(token, state.ServerId, cancellationToken);
        }

        if (state.PrimaryIpv4Id > 0 && await _cloud.PrimaryIpExistsAsync(token, state.PrimaryIpv4Id, cancellationToken))
        {
            progress?.Report("Deleting its IPv4 address.");
            await _cloud.DeletePrimaryIpAsync(token, state.PrimaryIpv4Id, cancellationToken);
        }
        _ = await _cloud.DeleteOrphanedPrimaryIpsAsync(token, controller, cancellationToken);

        WorkerStateStore.DeleteLocalState(statePath, state.KnownHostsFile);
        progress?.Report("Worker deleted; billing for it and its IPv4 address has stopped.");
        return true;
    }

    private async Task<WorkerState?> ResolveStateAsync(string token, string controller, string statePath, CancellationToken cancellationToken, bool retainStateWhenServerMissing = false)
    {
        var state = await WorkerStateStore.ReadStateAsync(statePath, cancellationToken);
        if (state is not null)
        {
            var server = await _cloud.GetServerAsync(token, state.ServerId, cancellationToken);
            if (server is not null)
            {
                HetznerCloudClient.VerifyOwnership(server.Value, controller);
                var refreshed = Refreshed(state, server.Value);
                await WorkerStateStore.WriteStateAsync(statePath, refreshed, cancellationToken);
                return refreshed;
            }
            if (retainStateWhenServerMissing) return state;
            if (state.PrimaryIpv4Id > 0 && await _cloud.PrimaryIpExistsAsync(token, state.PrimaryIpv4Id, cancellationToken))
                await _cloud.DeletePrimaryIpAsync(token, state.PrimaryIpv4Id, cancellationToken);
            WorkerStateStore.DeleteLocalState(statePath, state.KnownHostsFile);
        }

        var servers = await _cloud.ListManagedServersAsync(token, controller, cancellationToken);
        if (servers.Count > 1) throw new InvalidOperationException(MoreThanOneServerMessage);
        if (servers.Count == 0) return null;
        HetznerCloudClient.VerifyOwnership(servers[0], controller);
        var recovered = new WorkerState(
            servers[0].Id,
            servers[0].PrimaryIpv4Id,
            servers[0].Name,
            servers[0].Address,
            servers[0].Location,
            servers[0].ServerType,
            WorkerStateStore.KnownHostsPath(databasePath: Path.GetFullPath(statePath[..^".hetzner-worker.json".Length]), servers[0].Id),
            controller,
            servers[0].CreatedUtc);
        await WorkerStateStore.WriteStateAsync(statePath, recovered, cancellationToken);
        return recovered;
    }

    /// <summary>The record with what Hetzner says now; a type or location it didn't report keeps the recorded one.</summary>
    private static WorkerState Refreshed(WorkerState state, ServerSnapshot server) => state with
    {
        Address = server.Address,
        PrimaryIpv4Id = server.PrimaryIpv4Id,
        CreatedUtc = server.CreatedUtc,
        Location = server.Location.Length > 0 ? server.Location : state.Location,
        ServerType = server.ServerType.Length > 0 ? server.ServerType : state.ServerType
    };

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
        var (candidates, reports) = await _placement.PreflightCandidatesAsync(token, settings.Processing.RemoteSsh, progress, cancellationToken);
        // Asked once, inside the lifecycle lock, so the approved candidates are the ones tried. The fallback loop below never
        // asks again: the quote already named the most it can cost.
        if (confirmCreation is not null && !await confirmCreation(HetznerPlacement.Quote(candidates), cancellationToken))
            throw new RemoteWorkerCreationDeclinedException();
        var failures = new List<string>();
        Exception? lastPlacementFailure = null;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            progress?.Report(candidate.HourlyGross is { } price
                ? $"Creating Hetzner {candidate} ({HetznerPlacement.FormatPrice(price)} incl. VAT)."
                : $"Creating Hetzner {candidate}.");
            CreatedServer created;
            try
            {
                created = await _cloud.CreateServerAsync(token, name, hetzner, candidate.ServerType, candidate.Location, controller, bootstrapScript, cancellationToken);
            }
            catch (HetznerApiException exception) when (HetznerPlacement.IsPlacementUnavailable(exception))
            {
                lastPlacementFailure = exception;
                var reason = HetznerPlacement.DescribeApiFailure(exception);
                failures.Add($"{candidate} ({reason})");
                reports[candidate.ServerType].Notes.Add($"placement failed in {candidate.Location} ({reason})");
                if (candidateIndex + 1 < candidates.Count)
                {
                    progress?.Report($"Hetzner {candidate} placement unavailable ({reason}); falling back to {candidates[candidateIndex + 1]}.");
                    continue;
                }
                break;
            }

            using (created)
            {
                var server = created.Server;
                var knownHostsFile = WorkerStateStore.KnownHostsPath(statePath[..^".hetzner-worker.json".Length], server.Id);
                var state = new WorkerState(server.Id, server.PrimaryIpv4Id, server.Name, server.Address, candidate.Location, candidate.ServerType, knownHostsFile, controller, server.CreatedUtc);

                // A failed POST never reaches this point, so no local state is left for a failed placement attempt.
                await WorkerStateStore.WriteStateAsync(statePath, state, cancellationToken);

                try
                {
                    if (server.PrimaryIpv4Id > 0) await _cloud.SetPrimaryIpAutoDeleteAsync(token, server.PrimaryIpv4Id, controller, cancellationToken);
                    if (created.ReadActionId() is { } actionId)
                    {
                        progress?.Report($"Hetzner accepted {candidate} for the worker {server.Name}; waiting for it to start.");
                        await _cloud.WaitForActionAsync(token, actionId, cancellationToken);
                    }
                }
                catch (HetznerActionException exception) when (HetznerPlacement.IsPlacementUnavailable(exception))
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

    private async Task CleanupFailedCreateAsync(string token, string statePath, WorkerState state, CancellationToken cancellationToken)
    {
        // A failed placement action can race with Hetzner removing the server. Check first so
        // cleanup remains safe when the server has already disappeared.
        var server = await _cloud.GetServerAsync(token, state.ServerId, cancellationToken);
        if (server is not null)
        {
            HetznerCloudClient.VerifyOwnership(server.Value, state.Controller);
            var actionId = await _cloud.DeleteServerAsync(token, state.ServerId, cancellationToken);
            if (actionId is not null) await _cloud.WaitForActionAsync(token, actionId.Value, cancellationToken);
            await _cloud.WaitForServerDeletionAsync(token, state.ServerId, cancellationToken);
        }
        if (state.PrimaryIpv4Id > 0 && await _cloud.PrimaryIpExistsAsync(token, state.PrimaryIpv4Id, cancellationToken))
            await _cloud.DeletePrimaryIpAsync(token, state.PrimaryIpv4Id, cancellationToken);
        WorkerStateStore.DeleteLocalState(statePath, state.KnownHostsFile);
    }

    private static void ApplyConnection(AppSettings settings, WorkerState state)
    {
        settings.Processing.RemoteSsh.Host = state.Address;
        settings.Processing.RemoteSsh.User = "videoopt";
        settings.Processing.RemoteSsh.KnownHostsFile = state.KnownHostsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(state.KnownHostsFile)!);
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

    private void RequireManaged(AppSettings settings)
    {
        if (!IsManaged(settings)) throw new InvalidOperationException("The remote SSH lifecycle is not configured for Hetzner.");
    }

    public void Dispose() => _cloud.Dispose();
}
