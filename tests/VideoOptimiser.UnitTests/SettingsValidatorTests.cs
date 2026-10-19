using FluentAssertions;
using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.UnitTests;

public sealed class SettingsValidatorTests
{
    [Fact]
    public void ValidateTemplateSettingsReportsRequiredUserEdits()
    {
        Codes(new AppSettings()).Should().Contain("WatchRootsRequired");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void ValidateRequiresMaxEncodedPercentToBeAWholeNumberFromOneToOneHundred(int percent, bool valid)
    {
        var settings = CreateValidSettings();
        settings.Quality.MaxEncodedPercent = percent;

        Codes(settings).Contains("InvalidMaxEncodedPercent").Should().Be(!valid);
    }

    [Theory]
    [InlineData(SettingsValidator.LowestMinimumVmaf - 0.01, false)]
    [InlineData(SettingsValidator.LowestMinimumVmaf, true)]
    [InlineData(SettingsValidator.HighestMinimumVmaf, true)]
    [InlineData(SettingsValidator.HighestMinimumVmaf + 0.01, false)]
    [InlineData(double.NaN, false)]
    public void ValidateRequiresAPlausibleMinimumVmaf(double vmaf, bool valid)
    {
        var settings = CreateValidSettings();
        settings.Quality.MinimumVmaf = vmaf;

        Codes(settings).Contains("InvalidVmaf").Should().Be(!valid);
    }

    [Theory]
    [InlineData(SettingsValidator.LowestPreset - 1, false)]
    [InlineData(SettingsValidator.LowestPreset, true)]
    [InlineData(SettingsValidator.HighestPreset, true)]
    [InlineData(SettingsValidator.HighestPreset + 1, false)]
    public void ValidateRequiresAnSvtAv1Preset(int preset, bool valid)
    {
        var settings = CreateValidSettings();
        settings.Quality.Preset = preset;

        Codes(settings).Contains("InvalidPreset").Should().Be(!valid);
    }

    [Theory]
    [InlineData(SettingsValidator.LowestCrf - 1, 50, "InvalidMinCrf")]
    [InlineData(SettingsValidator.LowestCrf, 50, null)]
    [InlineData(8, SettingsValidator.HighestCrf, null)]
    [InlineData(8, SettingsValidator.HighestCrf + 1, "InvalidMaxCrf")]
    [InlineData(30, 30, null)]
    [InlineData(31, 30, "InvalidCrfRange")]
    [InlineData(SettingsValidator.HighestCrf + 1, 30, "InvalidMinCrf")]
    public void ValidateRequiresAnOrderedCrfRangeOnTheSvtAv1Scale(int minCrf, int maxCrf, string? expectedCode)
    {
        var settings = CreateValidSettings();
        settings.Quality.CrfSearch.MinCrf = minCrf;
        settings.Quality.CrfSearch.MaxCrf = maxCrf;

        if (expectedCode is null) Codes(settings).Should().BeEmpty();
        else Codes(settings).Should().Equal(expectedCode);
    }

    [Theory]
    [InlineData("hevc", false)]
    [InlineData("AV1", true)]
    public void ValidateRefusesARuleThatAdmitsAv1(string codec, bool rejected)
    {
        var settings = CreateValidSettings();
        settings.Eligibility.Rules[0].Codecs = ["h264", codec];

        Codes(settings).Contains("InvalidEligibilityRuleCodec").Should().Be(rejected);
    }

    [Fact]
    public void ValidateAcceptsAnImmichOnlySetupWithoutWatchRoots()
    {
        var settings = CreateValidSettings();
        settings.Watch.Roots.Clear();
        settings.Immich.BaseUrl = "http://immich.test:2283";

        Codes(settings).Should().NotContain("WatchRootsRequired");
    }

    [Fact]
    public void ValidateRejectsDecodeTestOutsideKnownModes()
    {
        var settings = CreateValidSettings();
        settings.Validation.DecodeTest = "true";

        Codes(settings).Should().Contain("InvalidDecodeTest");
    }

    [Theory]
    [InlineData("Skip", false)]
    [InlineData("keep", true)]
    public void ValidateAcceptsOnlyDropOrSkipForUnsupportedAudio(string value, bool rejected)
    {
        var settings = CreateValidSettings();
        settings.Quality.UnsupportedAudio = value;

        Codes(settings).Contains("InvalidUnsupportedAudio").Should().Be(rejected);
    }

    [Theory]
    [InlineData("archive", "Archive", null)]
    [InlineData("Keep", "", null)]
    [InlineData("archive", "D:\\Video archive", null)]
    [InlineData("move", "Archive", "InvalidOriginalAction")]
    [InlineData("archive", "", "ArchiveDirectoryRequired")]
    [InlineData("archive", "old\\originals", "InvalidArchiveDirectory")]
    [InlineData("delete", "..", "InvalidArchiveDirectory")]
    public void ValidateAcceptsEveryOriginalActionAndOnlyAFolderNameOrAbsoluteArchiveDirectory(string action, string archiveDirectory, string? expectedCode)
    {
        var settings = CreateValidSettings();
        settings.Original = new OriginalSettings { Action = action, ArchiveDirectory = archiveDirectory };

        if (expectedCode is null) Codes(settings).Should().BeEmpty();
        else Codes(settings).Should().Equal(expectedCode);
    }

    [Fact]
    public void ValidateRemoteModeRequiresOnlyRemoteSpecificToolSettings()
    {
        var settings = CreateValidSettings();
        settings.Processing.Mode = ProcessingModes.RemoteSsh;
        settings.Processing.RemoteSsh.Host = "video-worker";
        settings.Tools.AbAv1Path = string.Empty;

        Codes(settings).Should().BeEmpty();
    }

    [Fact]
    public void ValidateRemoteModeRejectsInvalidRemoteSettings()
    {
        var settings = CreateValidSettings();
        settings.Processing = new ProcessingSettings
        {
            Mode = ProcessingModes.RemoteSsh,
            RemoteSsh = new RemoteSshSettings
            {
                Host = "-unsafe",
                WorkingDirectory = "relative/path",
                MinimumCpuCount = 0,
                MinimumAvailableMemory = "none",
                MinimumFreeDiskMultiplier = 0
            }
        };
        settings.Tools.SshPath = string.Empty;
        settings.Tools.SftpPath = string.Empty;

        Codes(settings).Should().Contain([
            "SshPathRequired",
            "SftpPathRequired",
            "InvalidRemoteHost",
            "InvalidRemoteWorkingDirectory",
            "InvalidRemoteMinimumCpuCount",
            "InvalidRemoteMinimumMemory",
            "InvalidRemoteDiskMultiplier"]);
    }

    [Theory]
    [InlineData("45m", null)]
    [InlineData("0s", "InvalidRemoteStageSilenceTimeout")]
    [InlineData("never", "InvalidRemoteStageSilenceTimeout")]
    public void ValidateRemoteModeAcceptsOnlyAPositiveStageSilenceTimeout(string timeout, string? expectedCode)
    {
        var settings = CreateValidSettings();
        settings.Processing.Mode = ProcessingModes.RemoteSsh;
        settings.Processing.RemoteSsh.Host = "video-worker";
        settings.Processing.RemoteSsh.StageSilenceTimeout = timeout;

        if (expectedCode is null) Codes(settings).Should().BeEmpty();
        else Codes(settings).Should().Equal(expectedCode);
    }

    [Fact]
    public void ValidateManagedHetznerAllowsRuntimeHostAndRequiresProvisioningInputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "video-optimiser-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var identity = Path.Combine(directory, "worker-key");
            var bootstrap = Path.Combine(directory, "bootstrap.sh");
            File.WriteAllText(identity, "key");
            File.WriteAllText(bootstrap, "#!/bin/bash");
            var settings = CreateValidSettings();
            settings.Processing = new ProcessingSettings
            {
                Mode = ProcessingModes.RemoteSsh,
                RemoteSsh = new RemoteSshSettings
                {
                    Lifecycle = RemoteLifecycleModes.Hetzner,
                    Host = string.Empty,
                    IdentityFile = identity,
                    Hetzner = new HetznerSettings { SshKeyName = "video-optimiser-hetzner-cx43", BootstrapScriptPath = bootstrap }
                }
            };

            Codes(settings).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidateManagedHetznerRejectsMissingKeyAndBootstrap()
    {
        var settings = CreateValidSettings();
        settings.Processing = new ProcessingSettings
        {
            Mode = ProcessingModes.RemoteSsh,
            RemoteSsh = new RemoteSshSettings
            {
                Lifecycle = RemoteLifecycleModes.Hetzner,
                Hetzner = new HetznerSettings { SshKeyName = string.Empty, BootstrapScriptPath = "missing-bootstrap.sh", BootstrapTimeout = "never" }
            }
        };

        Codes(settings).Should().Contain([
            "HetznerSshKeyRequired",
            "RemoteIdentityFileRequired",
            "HetznerBootstrapMissing",
            "InvalidHetznerBootstrapTimeout"]);
    }

    [Theory]
    [InlineData(new string[0], null, "HetznerServerTypesRequired", false)]
    [InlineData(new[] { "cx43", " " }, null, "InvalidHetznerServerTypes", false)]
    [InlineData(new[] { "cx43" }, "0", "InvalidHetznerMaxHourlyPrice", false)]
    [InlineData(new[] { "cx43" }, null, "HetznerLocationsRequired", true)]
    public void ValidateManagedHetznerRejectsUnusableServerTypesLocationsOrPriceCap(string[] serverTypes, string? maxHourlyPrice, string expectedCode, bool emptyLocations)
    {
        var settings = CreateValidSettings();
        settings.Processing = new ProcessingSettings
        {
            Mode = ProcessingModes.RemoteSsh,
            RemoteSsh = new RemoteSshSettings
            {
                Lifecycle = RemoteLifecycleModes.Hetzner,
                Hetzner = new HetznerSettings
                {
                    ServerTypes = [.. serverTypes],
                    Locations = emptyLocations ? [] : new HetznerSettings().Locations,
                    MaxHourlyPrice = maxHourlyPrice is null ? 0.30m : decimal.Parse(maxHourlyPrice, System.Globalization.CultureInfo.InvariantCulture)
                }
            }
        };

        Codes(settings).Should().Contain(expectedCode);
    }

    [Fact]
    public void ValidateRejectsUnknownProcessingMode()
    {
        var settings = CreateValidSettings();
        settings.Processing.Mode = "cloud";

        Codes(settings).Should().Contain("InvalidProcessingMode");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/var/tmp/../video-optimiser")]
    public void ValidateRemoteModeRejectsUnsafeWorkingDirectory(string workingDirectory)
    {
        var settings = CreateValidSettings();
        settings.Processing.Mode = ProcessingModes.RemoteSsh;
        settings.Processing.RemoteSsh.Host = "video-worker";
        settings.Processing.RemoteSsh.WorkingDirectory = workingDirectory;

        Codes(settings).Should().Contain("InvalidRemoteWorkingDirectory");
    }

    [Theory]
    [InlineData("", 0, "", null)]
    [InlineData("http://192.168.1.113:2283", 90, "C:\\staging", null)]
    [InlineData("https://photos.example.com/api/", 1, "C:\\staging", null)]
    [InlineData("192.168.1.113:2283", 90, "C:\\staging", "InvalidImmichBaseUrl")]
    [InlineData("ftp://192.168.1.113", 90, "C:\\staging", "InvalidImmichBaseUrl")]
    [InlineData("http://192.168.1.113:2283", 0, "C:\\staging", "InvalidImmichMinimumAge")]
    [InlineData("http://192.168.1.113:2283", 90, " ", "InvalidImmichStagingDirectory")]
    public void ValidateChecksTheImmichSectionOnlyWhenABaseUrlIsSet(string baseUrl, int minimumAgeDays, string stagingDirectory, string? expectedCode)
    {
        var settings = CreateValidSettings();
        settings.Immich = new ImmichSettings { BaseUrl = baseUrl, MinimumAgeDays = minimumAgeDays, StagingDirectory = stagingDirectory };

        if (expectedCode is null) Codes(settings).Should().BeEmpty();
        else Codes(settings).Should().Equal(expectedCode);
    }

    private static string[] Codes(AppSettings settings) => [.. new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code)];

    internal static AppSettings CreateValidSettings() => new()
    {
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = "C:\\Videos" }] },
        Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "8Mbps", MinimumFileSize = "800MiB" }] },
        Original = new OriginalSettings { Action = "delete" }
    };
}
