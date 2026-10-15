using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Features.Worker;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class RunBannerTests
{
    [Theory]
    [InlineData("videoopt", "encoder.lan", "Encoding on videoopt@encoder.lan over SSH.")]
    [InlineData("", "encoder", "Encoding on encoder over SSH.")]
    public void ManualRemoteNamesTheSshTarget(string user, string host, string expected)
    {
        var settings = Settings(ProcessingModes.RemoteSsh);
        settings.Processing.RemoteSsh.Host = host;
        settings.Processing.RemoteSsh.User = user;

        RunBanner.Describe(settings).Should().Be(expected);
    }

    [Fact]
    public void ManagedHetznerWithoutACapSaysSo()
    {
        var settings = Settings(ProcessingModes.RemoteSsh, RemoteLifecycleModes.Hetzner);
        settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cpx42"];
        settings.Processing.RemoteSsh.Hetzner.MaxHourlyPrice = null;

        RunBanner.Describe(settings).Should().Be(
            "Encoding on a new Hetzner worker created for this run, billed hourly, trying cpx42 with no price cap.");
    }

    [Fact]
    public void ManagedHetznerWithARetainedWorkerNamesItAndWhenBillingStarted()
    {
        var worker = new RemoteWorkerInfo(42, "video-optimiser-1", "203.0.113.7", "recorded", new DateTimeOffset(2026, 10, 12, 20, 4, 0, TimeSpan.FromHours(2)), "cx43", "hel1");

        RunBanner.Describe(Settings(ProcessingModes.RemoteSsh, RemoteLifecycleModes.Hetzner), worker).Should().Be(
            "Encoding on the Hetzner worker video-optimiser-1, a cx43 in hel1, kept from an earlier run and billing since 2026-10-12 18:04 UTC.");
    }

    [Fact]
    public void ARetainedWorkerRecoveredWithoutPlacementOrCreationTimeStillDescribesIt()
    {
        var worker = new RemoteWorkerInfo(42, "video-optimiser-1", "203.0.113.7", "recorded", null, string.Empty, string.Empty);

        RunBanner.Describe(Settings(ProcessingModes.RemoteSsh, RemoteLifecycleModes.Hetzner), worker).Should().Be(
            "Encoding on the Hetzner worker video-optimiser-1, kept from an earlier run and still billing.");
    }

    [Fact]
    public void AWorkerIsIgnoredOutsideManagedHetzner()
    {
        var worker = new RemoteWorkerInfo(42, "video-optimiser-1", "203.0.113.7", "recorded", null, "cx43", "hel1");

        RunBanner.Describe(Settings(ProcessingModes.Local), worker).Should().Be("Encoding on this PC.");
    }

    private static AppSettings Settings(string mode, string lifecycle = RemoteLifecycleModes.Manual) => new()
    {
        Processing = new ProcessingSettings
        {
            Mode = mode,
            RemoteSsh = new RemoteSshSettings { Lifecycle = lifecycle }
        }
    };
}
