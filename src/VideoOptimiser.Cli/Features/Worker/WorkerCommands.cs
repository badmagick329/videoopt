using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Domain;
using static CliOutput;

namespace VideoOptimiser.Cli.Features.Worker;

/// <summary><c>worker create</c>, <c>status</c> and <c>delete</c>: the managed Hetzner worker, by hand.</summary>
internal sealed class WorkerCommands(IRemoteWorkerLifecycle lifecycle, ISettingsValidator validator)
{
    /// <summary>
    /// The worker commands act on the managed server whatever <c>processing.mode</c> says, so a user who switched back to
    /// local can still delete a server that is billing (VOPT-56). <c>lifecycle</c> alone decides; the mode is set to remote
    /// in memory only, because the lifecycle treats a server as managed only in remote mode. Returns the exit code when it refuses.
    /// </summary>
    public static int? RequireManagedLifecycle(CliCommandKind kind, LoadedConfiguration configuration)
    {
        // An empty "lifecycle:" in YAML deserialises as null, so the value is compared with string.Equals.
        var lifecycle = configuration.Settings.Processing.RemoteSsh.Lifecycle;
        if (string.Equals(lifecycle, RemoteLifecycleModes.Hetzner, StringComparison.OrdinalIgnoreCase))
        {
            ProcessingModeOverride.Remote.Apply(configuration.Settings);
            return null;
        }
        var name = kind switch { CliCommandKind.WorkerCreate => "worker create", CliCommandKind.WorkerStatus => "worker status", _ => "worker delete" };
        var current = string.IsNullOrWhiteSpace(lifecycle) ? "it is not set." : $"it is \"{lifecycle}\".";
        var message = $"'{name}' needs processing.remoteSsh.lifecycle set to \"hetzner\" in {configuration.Path}; {current}";
        // worker status is a read-only question, so "not managed" is an answer on stdout, as it has always been.
        if (kind == CliCommandKind.WorkerStatus)
        {
            CliConsole.WriteLine(message);
            return (int)ExitCode.Success;
        }
        WriteError(message);
        return (int)ExitCode.InvalidConfiguration;
    }

    public async Task<int> CreateAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (ReportInvalidSettings(validator, configuration)) return (int)ExitCode.InvalidConfiguration;
        var databasePath = configuration.Settings.Database.Path;
        try
        {
            var worker = await lifecycle.EnsureReadyAsync(configuration.Settings, databasePath, new InlineProgress<string>(CliConsole.WriteLine), ConsolePrompts.ConfirmWorkerCreationAsync, cancellationToken);
            CliConsole.WriteLine($"Worker ready: {worker.Name} {worker.Address}{DescribePlacement(worker)}");
            return (int)ExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            // The state file is written the moment the server exists, so a record means a server that bills from now on. A
            // press before that (the price prompt, the lock wait) leaves nothing, and stays silent as every stop there does.
            if (await lifecycle.ReadRecordedAsync(databasePath, CancellationToken.None) is not { } worker) throw;
            foreach (var line in WorkerKeepLines.For(worker.Name, command)) CliConsole.WriteLine(line);
            return (int)ExitCode.Cancelled;
        }
    }

    /// <summary>
    /// Reads through <see cref="IRemoteWorkerLifecycle.CheckAsync"/>, which never takes the lifecycle lock: a run holds it for
    /// a worker's whole setup, and a status question must not wait for that. When Hetzner couldn't be asked, the record on
    /// this PC is shown with the reason, and "none" only means none recorded.
    /// </summary>
    public async Task<int> StatusAsync(LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var check = await lifecycle.CheckAsync(configuration.Settings, configuration.Settings.Database.Path, cancellationToken);
        var worker = check.Worker;
        var asked = check.NotCheckedReason is null;
        if (worker is not null) CliConsole.WriteLine($"{worker.Name}  {RemoteWorkerStatuses.Describe(worker.Status)}  {worker.Address}{DescribePlacement(worker)}");
        else CliConsole.WriteLine(asked ? "No Hetzner worker exists." : "No Hetzner worker is recorded on this PC.");
        if (!asked) CliConsole.WriteLine($"Hetzner wasn't checked: {check.NotCheckedReason!.TrimEnd('.')}.");
        return (int)ExitCode.Success;
    }

    // A worker recovered from Hetzner without a stored state file can lack either.
    private static string DescribePlacement(RemoteWorkerInfo worker) =>
        worker.ServerType.Length == 0 ? string.Empty
        : worker.Location.Length == 0 ? $"  {worker.ServerType}"
        : $"  {worker.ServerType} in {worker.Location}";

    public async Task<int> DeleteAsync(LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        var deleted = await lifecycle.DeleteAsync(configuration.Settings, configuration.Settings.Database.Path, new InlineProgress<string>(CliConsole.WriteLine), cancellationToken);
        if (!deleted) CliConsole.WriteLine("No Hetzner worker exists.");
        return (int)ExitCode.Success;
    }
}
