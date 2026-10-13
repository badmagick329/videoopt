using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class ManagedWorkerDeletionPolicyTests
{
    // Both CLI routes share one policy; alternating them across the rows pins each outcome on each route.
    [Theory]
    [InlineData(CleanupRoute.Process, null, true, false, false, false, ManagedWorkerDeletionDecision.DeleteAutomatically, false, true)]
    [InlineData(CleanupRoute.QueueRun, "n", true, true, false, false, ManagedWorkerDeletionDecision.RetainAfterFailure, true, false)]
    [InlineData(CleanupRoute.Process, "yes", true, true, false, false, ManagedWorkerDeletionDecision.RetainAfterFailure, true, true)]
    [InlineData(CleanupRoute.QueueRun, "yes", false, true, false, false, ManagedWorkerDeletionDecision.RetainAfterFailure, false, false)]
    [InlineData(CleanupRoute.Process, "yes", true, true, true, false, ManagedWorkerDeletionDecision.RetainInterrupted, false, false)]
    [InlineData(CleanupRoute.QueueRun, "yes", true, false, false, true, ManagedWorkerDeletionDecision.RetainAfterUnexpectedFailure, false, false)]
    public async Task EachCliRouteDeletesTheWorkerOnlyAfterCleanSuccessOrExplicitConfirmation(
        CleanupRoute route,
        string? response,
        bool inputAvailable,
        bool hasFailures,
        bool interrupted,
        bool unexpectedFailure,
        ManagedWorkerDeletionDecision expectedDecision,
        bool expectedPrompted,
        bool expectedDeleted)
    {
        var fixture = new Fixture(response, inputAvailable);

        var result = await fixture.RunAsync(route, hasFailures, interrupted, unexpectedFailure);

        result.Decision.Should().Be(expectedDecision);
        result.Prompted.Should().Be(expectedPrompted);
        result.Deleted.Should().Be(expectedDeleted);
        fixture.Lifecycle.DeleteCalls.Should().Be(expectedDeleted ? 1 : 0);
        fixture.Confirmation.Calls.Should().Be(expectedPrompted ? 1 : 0);
    }

    public enum CleanupRoute
    {
        Process,
        QueueRun
    }

    private sealed class Fixture
    {
        private readonly AppSettings _settings = new()
        {
            Processing = new ProcessingSettings
            {
                Mode = ProcessingModes.RemoteSsh,
                RemoteSsh = new RemoteSshSettings
                {
                    Lifecycle = RemoteLifecycleModes.Hetzner,
                    Hetzner = new HetznerSettings { DeleteAfterRun = true }
                }
            }
        };

        public Fixture(string? response, bool available)
        {
            Confirmation = new FakeConfirmation(available, response);
            Lifecycle = new FakeLifecycle();
            Orchestrator = new ManagedWorkerCleanupOrchestrator(Lifecycle, Confirmation);
        }

        public FakeConfirmation Confirmation { get; }
        public FakeLifecycle Lifecycle { get; }
        public ManagedWorkerCleanupOrchestrator Orchestrator { get; }

        public Task<ManagedWorkerCleanupResult> RunAsync(CleanupRoute route, bool hasFailures, bool interrupted, bool unexpectedFailure) => route == CleanupRoute.Process
            ? Orchestrator.CleanUpAfterProcessAsync(_settings, "jobs.db", workerWasUsed: true, hasFailures, interrupted, unexpectedFailure)
            : Orchestrator.CleanUpAfterQueueRunAsync(_settings, "jobs.db", workerWasUsed: true, hasFailures, interrupted, unexpectedFailure);
    }

    private sealed class FakeConfirmation(bool available, string? response) : IInteractiveConfirmation
    {
        public int Calls { get; private set; }
        public bool IsAvailable => available;

        public bool Confirm(string prompt)
        {
            Calls++;
            prompt.Should().Be("One or more jobs failed. Delete the managed worker anyway? [y/N] ");
            return ManagedWorkerDeletionPolicy.IsAffirmative(response);
        }
    }

    private sealed class FakeLifecycle : IRemoteWorkerLifecycle
    {
        public int DeleteCalls { get; private set; }
        public bool IsManaged(AppSettings settings) => true;
        public Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, Func<RemoteWorkerCreationQuote, CancellationToken, Task<bool>>? confirmCreation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoteWorkerInfo?> ReadRecordedAsync(string databasePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult(true);
        }
    }
}
