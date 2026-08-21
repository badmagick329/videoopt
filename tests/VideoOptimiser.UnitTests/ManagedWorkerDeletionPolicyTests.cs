using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class ManagedWorkerDeletionPolicyTests
{
    [Theory]
    [InlineData(CleanupRoute.Process)]
    [InlineData(CleanupRoute.QueueRun)]
    public async Task EachCliRouteDeletesAfterCleanSuccess(CleanupRoute route)
    {
        var fixture = new Fixture(response: null, available: true);

        var result = await fixture.RunAsync(route, hasFailures: false, interrupted: false, unexpectedFailure: false);

        result.Deleted.Should().BeTrue();
        result.Prompted.Should().BeFalse();
        fixture.Lifecycle.DeleteCalls.Should().Be(1);
        fixture.Confirmation.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(CleanupRoute.Process, "n")]
    [InlineData(CleanupRoute.Process, "")]
    [InlineData(CleanupRoute.Process, null)]
    [InlineData(CleanupRoute.QueueRun, "n")]
    [InlineData(CleanupRoute.QueueRun, "")]
    [InlineData(CleanupRoute.QueueRun, null)]
    public async Task EachCliRouteRetainsAfterOrdinaryFailureUnlessConfirmed(CleanupRoute route, string? response)
    {
        var fixture = new Fixture(response, available: true);

        var result = await fixture.RunAsync(route, hasFailures: true, interrupted: false, unexpectedFailure: false);

        result.Decision.Should().Be(ManagedWorkerDeletionDecision.RetainAfterFailure);
        result.Prompted.Should().BeTrue();
        result.Deleted.Should().BeFalse();
        fixture.Lifecycle.DeleteCalls.Should().Be(0);
        fixture.Confirmation.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(CleanupRoute.Process, "y")]
    [InlineData(CleanupRoute.QueueRun, "yes")]
    public async Task EachCliRouteDeletesOnlyAfterExplicitConfirmation(CleanupRoute route, string response)
    {
        var fixture = new Fixture(response, available: true);

        var result = await fixture.RunAsync(route, hasFailures: true, interrupted: false, unexpectedFailure: false);

        result.Prompted.Should().BeTrue();
        result.Deleted.Should().BeTrue();
        fixture.Lifecycle.DeleteCalls.Should().Be(1);
        fixture.Confirmation.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(CleanupRoute.Process)]
    [InlineData(CleanupRoute.QueueRun)]
    public async Task EachCliRouteRetainsWithoutPromptWhenInputIsUnavailable(CleanupRoute route)
    {
        var fixture = new Fixture(response: "yes", available: false);

        var result = await fixture.RunAsync(route, hasFailures: true, interrupted: false, unexpectedFailure: false);

        result.Prompted.Should().BeFalse();
        result.Deleted.Should().BeFalse();
        fixture.Lifecycle.DeleteCalls.Should().Be(0);
        fixture.Confirmation.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(CleanupRoute.Process, true, false, ManagedWorkerDeletionDecision.RetainInterrupted)]
    [InlineData(CleanupRoute.Process, false, true, ManagedWorkerDeletionDecision.RetainAfterUnexpectedFailure)]
    [InlineData(CleanupRoute.QueueRun, true, false, ManagedWorkerDeletionDecision.RetainInterrupted)]
    [InlineData(CleanupRoute.QueueRun, false, true, ManagedWorkerDeletionDecision.RetainAfterUnexpectedFailure)]
    public async Task EachCliRouteRetainsInterruptedAndUnexpectedFailuresWithoutPrompting(CleanupRoute route, bool interrupted, bool unexpectedFailure, ManagedWorkerDeletionDecision expectedDecision)
    {
        var fixture = new Fixture(response: "yes", available: true);

        var result = await fixture.RunAsync(route, hasFailures: interrupted, interrupted, unexpectedFailure);

        result.Decision.Should().Be(expectedDecision);
        result.Prompted.Should().BeFalse();
        result.Deleted.Should().BeFalse();
        fixture.Lifecycle.DeleteCalls.Should().Be(0);
        fixture.Confirmation.Calls.Should().Be(0);
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
        public Task<RemoteWorkerInfo> EnsureReadyAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoteWorkerInfo?> GetAsync(AppSettings settings, string databasePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(AppSettings settings, string databasePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult(true);
        }
    }
}
