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

    /// <summary>
    /// The advice follows the config as the file says it (VOPT-74 B11). A started job on the managed worker, under a config whose
    /// mode and worker match it, resumes by a plain run; under a config in the other mode, by the flag of its own mode; under a
    /// config that points at another worker, by neither.
    /// </summary>
    [Theory]
    [InlineData(ProcessingModes.RemoteSsh, "managed", SkippedJobResume.RunWithoutFlag, "the Hetzner worker")]
    [InlineData(ProcessingModes.Local, "managed", SkippedJobResume.RunWithFlag, "the Hetzner worker")]
    [InlineData(ProcessingModes.RemoteSsh, "other-box", SkippedJobResume.ChangeConfig, "other-box")]
    [InlineData(ProcessingModes.Local, "other-box", SkippedJobResume.ChangeConfig, "other-box")]
    public void SkippedAdviceNamesTheCommandThatResumesTheJobUnderTheConfigAsTheFileSaysIt(string configuredMode, string configuredWorker, SkippedJobResume expected, string configWorkerOrMode)
    {
        var job = ManagedWorker.Started("", RemoteExecutionIdentity.ManagedHetzner);
        var settings = ManagedWorker.Settings();
        if (configuredWorker != "managed") (settings.Processing.RemoteSsh.Lifecycle, settings.Processing.RemoteSsh.Host) = (RemoteLifecycleModes.Manual, configuredWorker);
        ProcessingModeOverride.Local.Apply(settings);

        var advice = JobCompatibility.AdviseSkipped(job, settings, configuredMode);

        advice.Resume.Should().Be(expected);
        advice.JobWhere.Should().Be("the Hetzner worker");
        if (expected == SkippedJobResume.ChangeConfig) advice.ConfigWhere.Should().Be(configWorkerOrMode);
        else advice.ConfigWhere.Should().Be(configuredMode == ProcessingModes.Local ? "this PC" : "the Hetzner worker");
    }

    /// <summary>The same worker under another working directory has the same name, so only the setting can be named.</summary>
    [Fact]
    public void AWorkerKeptUnderAnotherWorkingDirectoryAsksForThatSettingBack()
    {
        var job = ManagedWorker.Started("", RemoteExecutionIdentity.ManagedHetzner);
        var settings = ManagedWorker.Settings();
        settings.Processing.RemoteSsh.WorkingDirectory = "/srv/other";

        JobCompatibility.AdviseSkipped(job, settings, ProcessingModes.RemoteSsh).Resume.Should().Be(SkippedJobResume.ChangeWorkingDirectory);
    }

    [Fact]
    public void AJobThatStartedOnThisPcResumesByTheLocalFlagWhateverTheRemoteBlockSays()
    {
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = "clip.mov", ExecutionMode = ProcessingModes.Local, Status = JobStatus.Interrupted, ResumeStatus = JobStatus.Encoding, Crf = 30, Attempt = 1 };
        var settings = ManagedWorker.Settings();

        JobCompatibility.AdviseSkipped(job, settings, ProcessingModes.RemoteSsh).Should().Be(new SkippedJobAdvice(SkippedJobResume.RunWithFlag, "this PC", "the Hetzner worker", ProcessingModes.Local));
    }
}
