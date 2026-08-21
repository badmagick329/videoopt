using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Processing;

public enum ManagedWorkerDeletionDecision
{
    None,
    DeleteAutomatically,
    RetainInterrupted,
    RetainAfterFailure,
    RetainAfterUnexpectedFailure
}

public sealed record ManagedWorkerCleanupResult(ManagedWorkerDeletionDecision Decision, bool Prompted, bool Deleted);

public interface IInteractiveConfirmation
{
    bool IsAvailable { get; }
    bool Confirm(string prompt);
}

public interface IManagedWorkerCleanupOrchestrator
{
    Task<ManagedWorkerCleanupResult> CleanUpAfterProcessAsync(AppSettings settings, string databasePath, bool workerWasUsed, bool hasFailures, bool interrupted, bool unexpectedFailure, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<ManagedWorkerCleanupResult> CleanUpAfterQueueRunAsync(AppSettings settings, string databasePath, bool workerWasUsed, bool hasFailures, bool interrupted, bool unexpectedFailure, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

public static class ManagedWorkerDeletionPolicy
{
    public static ManagedWorkerDeletionDecision Decide(bool workerWasUsed, bool deleteAfterRun, bool hasFailures, bool interrupted, bool unexpectedFailure = false)
    {
        if (!workerWasUsed) return ManagedWorkerDeletionDecision.None;
        if (interrupted) return ManagedWorkerDeletionDecision.RetainInterrupted;
        if (unexpectedFailure) return ManagedWorkerDeletionDecision.RetainAfterUnexpectedFailure;
        if (hasFailures) return ManagedWorkerDeletionDecision.RetainAfterFailure;
        return deleteAfterRun ? ManagedWorkerDeletionDecision.DeleteAutomatically : ManagedWorkerDeletionDecision.None;
    }

    public static bool IsAffirmative(string? response) =>
        string.Equals(response, "y", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase);

    public static async Task<ManagedWorkerCleanupResult> ApplyAsync(
        bool workerWasUsed,
        bool deleteAfterRun,
        bool hasFailures,
        bool interrupted,
        bool unexpectedFailure,
        IInteractiveConfirmation confirmation,
        Func<CancellationToken, Task> delete,
        CancellationToken cancellationToken = default)
    {
        var decision = Decide(workerWasUsed, deleteAfterRun, hasFailures, interrupted, unexpectedFailure);
        if (decision == ManagedWorkerDeletionDecision.DeleteAutomatically)
        {
            await delete(cancellationToken);
            return new ManagedWorkerCleanupResult(decision, Prompted: false, Deleted: true);
        }
        if (decision != ManagedWorkerDeletionDecision.RetainAfterFailure || !confirmation.IsAvailable)
        {
            return new ManagedWorkerCleanupResult(decision, Prompted: false, Deleted: false);
        }

        var confirmed = confirmation.Confirm("One or more jobs failed. Delete the managed worker anyway? [y/N] ");
        if (confirmed) await delete(cancellationToken);
        return new ManagedWorkerCleanupResult(decision, Prompted: true, Deleted: confirmed);
    }
}

public sealed class ManagedWorkerCleanupOrchestrator(IRemoteWorkerLifecycle lifecycle, IInteractiveConfirmation confirmation) : IManagedWorkerCleanupOrchestrator
{
    public Task<ManagedWorkerCleanupResult> CleanUpAfterProcessAsync(AppSettings settings, string databasePath, bool workerWasUsed, bool hasFailures, bool interrupted, bool unexpectedFailure, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        CleanUpAsync(settings, databasePath, workerWasUsed, hasFailures, interrupted, unexpectedFailure, progress, cancellationToken);

    public Task<ManagedWorkerCleanupResult> CleanUpAfterQueueRunAsync(AppSettings settings, string databasePath, bool workerWasUsed, bool hasFailures, bool interrupted, bool unexpectedFailure, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        CleanUpAsync(settings, databasePath, workerWasUsed, hasFailures, interrupted, unexpectedFailure, progress, cancellationToken);

    private Task<ManagedWorkerCleanupResult> CleanUpAsync(AppSettings settings, string databasePath, bool workerWasUsed, bool hasFailures, bool interrupted, bool unexpectedFailure, IProgress<string>? progress, CancellationToken cancellationToken) =>
        ManagedWorkerDeletionPolicy.ApplyAsync(
            workerWasUsed,
            settings.Processing.RemoteSsh.Hetzner.DeleteAfterRun,
            hasFailures,
            interrupted,
            unexpectedFailure,
            confirmation,
            token => lifecycle.DeleteAsync(settings, databasePath, progress, token),
            cancellationToken);
}
