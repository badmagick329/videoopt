using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoOptimiser.Application;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Hetzner;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.Infrastructure.Features.Worker;

/// <summary>
/// The worker's lifecycle: finds, creates, waits for and deletes the one managed Hetzner server, and decides in what order.
/// What each step does lives in its own class: <see cref="HetznerCloudClient"/> talks to Hetzner,
/// <see cref="HetznerPlacement"/> chooses where, <see cref="WorkerReadiness"/> waits for bootstrap and
/// <see cref="WorkerStateStore"/> keeps the local record. The worker's bill (<see cref="SqliteWorkerBillStore"/>) is written
/// here too, at each step that sees the server created, up or gone, because nothing else sees them; <see cref="CheckAsync"/>
/// writes nothing, the bill included.
/// </summary>
public sealed class HetznerRemoteWorkerLifecycle : IRemoteWorkerLifecycle, IDisposable
{
    private readonly HetznerCloudClient _cloud;
    private readonly HetznerPlacement _placement;
    private readonly WorkerReadiness _readiness;
    private readonly SqliteWorkerBillStore _bills;
    private readonly ICommandHints _hints;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _checkTimeout;
    private const string MoreThanOneServerMessage = "More than one Hetzner worker has this controller label; refusing to choose or delete either one.";

    public HetznerRemoteWorkerLifecycle(IExternalProcessRunner processes, SqliteWorkerBillStore bills, ICommandHints hints)
        : this(processes, bills, hints, new HttpClient { BaseAddress = new Uri(HetznerHttp.ApiBase) }, Task.Delay, ownsHttpClient: true)
    {
    }

    internal HetznerRemoteWorkerLifecycle(
        IExternalProcessRunner processes,
        SqliteWorkerBillStore bills,
        ICommandHints hints,
        HttpClient http,
        Func<TimeSpan, CancellationToken, Task> delay,
        bool ownsHttpClient = false,
        TimeSpan? readinessProbeTimeout = null,
        TimeSpan? progressHeartbeatInterval = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? checkTimeout = null)
    {
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(15);
        _bills = bills;
        _hints = hints;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
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
        var state = await ResolveStateAsync(token, controller, databasePath, statePath, cancellationToken);
        if (state is null)
        {
            _ = await _cloud.DeleteOrphanedPrimaryIpsAsync(token, controller, cancellationToken);
            progress?.Report("No Hetzner worker exists yet.");
            var script = await ReadBootstrapScriptAsync(settings, cancellationToken);
            var (candidates, reports) = await _placement.PreflightCandidatesAsync(token, settings.Processing.RemoteSsh, progress, cancellationToken);
            // Asked once, inside the lifecycle lock, so the approved candidates are the ones tried. The fallback loop never
            // asks again: the quote already named the most it can cost.
            if (confirmCreation is not null && !await confirmCreation(HetznerPlacement.Quote(candidates), cancellationToken))
                throw new RemoteWorkerCreationDeclinedException();
            state = await PlaceAsync(token, settings, controller, databasePath, statePath, script, candidates, reports, progress, cancellationToken);
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

    public async Task<IReadOnlyList<RemoteWorkerCreationOption>> ListCreationOptionsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        RequireManaged(settings);
        // No lifecycle lock: this only reads Hetzner, and a run that is bootstrapping holds the lock for minutes.
        var token = GetToken(settings);
        var (candidates, _) = await _placement.PreflightCandidatesAsync(token, settings.Processing.RemoteSsh, null, cancellationToken);
        return candidates.Select(candidate => new RemoteWorkerCreationOption(candidate.ServerType, candidate.Location, candidate.HourlyGross)).ToArray();
    }

    public async Task<RemoteWorkerInfo> CreateAsync(
        AppSettings settings,
        string databasePath,
        RemoteWorkerCreationOption placement,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequireManaged(settings);
        await using var lifecycleLock = await WorkerStateStore.AcquireLockAsync(databasePath, progress, cancellationToken);
        var token = GetToken(settings);
        var controller = ControllerId(databasePath);
        var statePath = WorkerStateStore.StatePath(databasePath);
        // The dialog's list can be minutes old, or another command can have created the worker since: never a second server.
        // Whether the existing one finished bootstrapping isn't known here, so its status is the record's.
        if (await ResolveStateAsync(token, controller, databasePath, statePath, cancellationToken) is { } existing)
            return existing.ToInfo(RemoteWorkerStatuses.Recorded);

        _ = await _cloud.DeleteOrphanedPrimaryIpsAsync(token, controller, cancellationToken);
        progress?.Report("No Hetzner worker exists yet.");
        var script = await ReadBootstrapScriptAsync(settings, cancellationToken);
        var (candidates, reports) = await _placement.PreflightCandidatesAsync(token, settings.Processing.RemoteSsh, progress, cancellationToken);
        var chosen = candidates.Where(candidate =>
            candidate.ServerType.Equals(placement.ServerType, StringComparison.OrdinalIgnoreCase) &&
            candidate.Location.Equals(placement.Location, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Only the chosen type's outcome: the user picked this one, so the other types' skip notes would be noise.
        var chosenReports = new OrderedDictionary<string, TypeReport>(StringComparer.OrdinalIgnoreCase);
        if (reports.TryGetValue(placement.ServerType, out var chosenReport)) chosenReports.Add(placement.ServerType, chosenReport);
        if (chosen.Length == 0)
        {
            var reason = chosenReport is { Notes.Count: > 0 }
                ? string.Join("; ", chosenReport.Notes)
                : $"not offered in {placement.Location} within the price cap";
            throw new RemoteWorkerCapacityUnavailableException([new RemoteWorkerTypeOutcome(placement.ServerType, [placement.Location], reason)]);
        }

        var state = await PlaceAsync(token, settings, controller, databasePath, statePath, script, chosen, chosenReports, progress, cancellationToken);
        progress?.Report($"Created the Hetzner worker {state.Name}; it is bootstrapping.");
        return state.ToInfo(RemoteWorkerStatuses.Bootstrapping);
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
        var state = await ResolveStateAsync(token, controller, databasePath, statePath, cancellationToken, retainStateWhenServerMissing: true);
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
            await _bills.DeletedAsync(databasePath, state.ServerId, _utcNow(), CancellationToken.None);
        }
        else
        {
            await _bills.GoneAsync(databasePath, state.ServerId, CancellationToken.None);
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

    private async Task<WorkerState?> ResolveStateAsync(string token, string controller, string databasePath, string statePath, CancellationToken cancellationToken, bool retainStateWhenServerMissing = false)
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
                await RecordSeenAsync(token, databasePath, server.Value, cancellationToken);
                return refreshed;
            }
            // Deleted in the Hetzner console: when is unknown, so the bill ends at the last time it was seen up.
            await _bills.GoneAsync(databasePath, state.ServerId, CancellationToken.None);
            if (retainStateWhenServerMissing) return state;
            if (state.PrimaryIpv4Id > 0 && await _cloud.PrimaryIpExistsAsync(token, state.PrimaryIpv4Id, cancellationToken))
                await _cloud.DeletePrimaryIpAsync(token, state.PrimaryIpv4Id, cancellationToken);
            WorkerStateStore.DeleteLocalState(statePath, state.KnownHostsFile);
        }

        return await RecoverByLabelAsync(token, controller, databasePath, statePath, cancellationToken);
    }

    /// <summary>The server Hetzner has for this database that the state file doesn't record, found by its label and recorded (state file and bill).</summary>
    private async Task<WorkerState?> RecoverByLabelAsync(string token, string controller, string databasePath, string statePath, CancellationToken cancellationToken)
    {
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
            WorkerStateStore.KnownHostsPath(databasePath, servers[0].Id),
            controller,
            servers[0].CreatedUtc);
        await WorkerStateStore.WriteStateAsync(statePath, recovered, cancellationToken);
        await RecordSeenAsync(token, databasePath, servers[0], cancellationToken);
        return recovered;
    }

    /// <summary>
    /// A live server moves its bill's last-seen time to now. One with no bill yet (created before migration 13, or recovered
    /// by its label) gets one from what Hetzner says now, so the worker up when this version arrived is counted too.
    /// </summary>
    private async Task RecordSeenAsync(string token, string databasePath, ServerSnapshot server, CancellationToken cancellationToken)
    {
        var now = _utcNow();
        if (await _bills.SeenAsync(databasePath, server.Id, now, CancellationToken.None)) return;
        var hourly = await WorkerHourlyGrossAsync(token, server.HourlyGross, server.Location, cancellationToken);
        await _bills.AddAsync(databasePath, new WorkerBillEntry(server.Id, server.Name, server.ServerType, server.Location, hourly, server.CreatedUtc ?? now), now, CancellationToken.None);
    }

    /// <summary>
    /// What the worker bills per hour: the server type's gross plus the Primary IPv4's in that location, which Hetzner prices
    /// apart. The IPv4 adds nothing when Hetzner lists no price for it there, or when the pricing read fails or is stopped:
    /// by then the server exists and bills, so the read must never fail what called it, nor keep its bill from being written.
    /// A stop still lands at the caller's next Hetzner call.
    /// </summary>
    private async Task<decimal?> WorkerHourlyGrossAsync(string token, decimal? serverHourlyGross, string location, CancellationToken cancellationToken)
    {
        if (serverHourlyGross is not { } server) return null;
        try
        {
            return server + (await _cloud.GetPrimaryIpv4HourlyGrossAsync(token, location, cancellationToken) ?? 0m);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return server;
        }
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

    private static async Task<string> ReadBootstrapScriptAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        // cloud-init runs the script with bash, which fails on CRLF; a Windows checkout with core.autocrlf has CRLF.
        var script = (await File.ReadAllTextAsync(settings.Processing.RemoteSsh.Hetzner.BootstrapScriptPath, cancellationToken)).ReplaceLineEndings("\n");
        if (Encoding.UTF8.GetByteCount(script) > 32 * 1024) throw new InvalidOperationException("The Hetzner bootstrap script exceeds the 32 KiB user-data limit.");
        return script;
    }

    /// <summary>
    /// Requests the candidates one after another until Hetzner accepts one, and returns its server once the create action has
    /// finished. Shared by the run's create (every candidate, fallback included) and the chosen-placement create (one
    /// candidate, so a refusal ends it). A failed attempt is cleaned up first, so nothing bills when it throws.
    /// </summary>
    private async Task<WorkerState> PlaceAsync(
        string token,
        AppSettings settings,
        string controller,
        string databasePath,
        string statePath,
        string bootstrapScript,
        IReadOnlyList<PlacementCandidate> candidates,
        OrderedDictionary<string, TypeReport> reports,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var hetzner = settings.Processing.RemoteSsh.Hetzner;
        var name = $"{hetzner.ServerNamePrefix}-{controller[..12]}".ToLowerInvariant();
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
                // On no token, through the state and bill writes below: once the POST is sent Hetzner may make the server, and a
                // stop that cut off the answer naming it would leave it billing with no record. The HTTP timeout bounds the wait.
                created = await _cloud.CreateServerAsync(token, name, hetzner, candidate.ServerType, candidate.Location, controller, bootstrapScript, CancellationToken.None);
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
            // Any other failure but Hetzner's own refusal left no answer to read (a timeout, a broken connection, an answer that
            // didn't parse), and the server may exist all the same.
            catch (Exception exception) when (exception is not HetznerApiException and not OutOfMemoryException)
            {
                await RecoverUnansweredCreateAsync(token, controller, databasePath, statePath, name);
                throw;
            }

            using (created)
            {
                var server = created.Server;
                var knownHostsFile = WorkerStateStore.KnownHostsPath(statePath[..^".hetzner-worker.json".Length], server.Id);
                var state = new WorkerState(server.Id, server.PrimaryIpv4Id, server.Name, server.Address, candidate.Location, candidate.ServerType, knownHostsFile, controller, server.CreatedUtc);

                // A failed POST never reaches this point, so no local state is left for a failed placement attempt.
                // On no token: a stop during the write would leave only the .partial file, and the run's end reads the state
                // file alone to decide what happens to the worker.
                await WorkerStateStore.WriteStateAsync(statePath, state, CancellationToken.None);
                // Its bill starts now, whatever happens next. Written on no token: a stop here must not leave a billing server
                // without a row (a later resolve would backfill it, but only if one comes).
                var now = _utcNow();
                var hourly = await WorkerHourlyGrossAsync(token, candidate.HourlyGross, candidate.Location, cancellationToken);
                await _bills.AddAsync(databasePath, new WorkerBillEntry(server.Id, server.Name, candidate.ServerType, candidate.Location, hourly, server.CreatedUtc ?? now), now, CancellationToken.None);
                // Recorded, so a stop that came during the create now stops the run, and its end decides the worker's fate.
                cancellationToken.ThrowIfCancellationRequested();

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
                    await CleanupFailedCreateAsync(token, databasePath, statePath, state, cancellationToken);
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
    /// After a create request whose answer was lost, records the server it may have made, found by its label, as a later resolve
    /// would: the run's end then decides its fate like any recorded worker's. Runs on no token, as the create did. When Hetzner
    /// can't be asked either, nothing records the server, so the user is told how to check for it and delete it.
    /// </summary>
    private async Task RecoverUnansweredCreateAsync(string token, string controller, string databasePath, string statePath, string name)
    {
        try
        {
            _ = await RecoverByLabelAsync(token, controller, databasePath, statePath, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new RemoteWorkerCreationUnconfirmedException(
                $"The request to create the worker {name} got no answer, and Hetzner couldn't be asked afterwards whether it made it: {exception.GetBaseException().Message}" + Environment.NewLine +
                $"If it did, it's billing hourly. To check: {_hints.Command("worker status")}. To delete it: {_hints.Command("worker delete")}");
        }
    }

    /// <remarks>The server existed, so Hetzner bills its started hour: its bill ends now rather than disappearing.</remarks>
    private async Task CleanupFailedCreateAsync(string token, string databasePath, string statePath, WorkerState state, CancellationToken cancellationToken)
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
        await _bills.DeletedAsync(databasePath, state.ServerId, _utcNow(), CancellationToken.None);
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
