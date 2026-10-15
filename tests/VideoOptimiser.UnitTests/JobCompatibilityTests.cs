using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// Whether a started job may resume under today's settings. What <c>QueueService.PrepareAsync</c> does with the verdict is
/// in <c>QueueServiceTests</c>.
/// </summary>
public sealed class JobCompatibilityTests
{
    /// <summary>
    /// A job started under the old binding that names the server type and locations still resumes when they change. The
    /// current <c>hetzner:managed</c> binding is proven runnable by <c>QueueServiceTests</c>' resume after a local override.
    /// </summary>
    [Fact]
    public void StartedJobKeepsResumingAfterTheManagedServerTypeOrLocationsChange() =>
        JobCompatibility.Evaluate(ManagedWorker.Started("", "hetzner:cx43:hel1,fsn1,nbg1"), ManagedWorker.Settings()).Status.Should().Be(JobCompatibilityStatus.Compatible);

    [Fact]
    public void ManagedHetznerJobDoesNotMatchAManualAliasNamedHetzner()
    {
        var settings = ManagedWorker.Settings();
        settings.Processing.RemoteSsh.Lifecycle = RemoteLifecycleModes.Manual;
        settings.Processing.RemoteSsh.Host = "hetzner";

        JobCompatibility.Evaluate(ManagedWorker.Started("", RemoteExecutionIdentity.ManagedHetzner), settings).Status.Should().Be(JobCompatibilityStatus.Incompatible);
    }

    [Theory]
    [InlineData(ProcessingModes.Local, null, "this PC")]
    [InlineData(ProcessingModes.RemoteSsh, "encoder", "encoder over SSH")]
    public void SkippedMessageNamesWhereTheJobStarted(string mode, string? host, string binding) =>
        JobCompatibility.SkippedMessage(new JobRecord { SourcePath = Path.Combine(Path.GetTempPath(), "clip.mov"), ExecutionMode = mode, RemoteHost = host })
            .Should().Be($"Skipped clip.mov: started on {binding}; run without --local/--remote to resume it.");
}
