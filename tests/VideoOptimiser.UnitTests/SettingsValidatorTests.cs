using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Configuration;

namespace VideoOptimiser.UnitTests;

public sealed class SettingsValidatorTests
{
    [Fact]
    public void ValidateTemplateSettingsReportsRequiredUserEdits()
    {
        var diagnostics = new SettingsValidator().Validate(new AppSettings());

        diagnostics.Select(diagnostic => diagnostic.Code).Should().Contain("WatchRootsRequired");
    }

    [Fact]
    public void ValidateAcceptsAnImmichOnlySetupWithoutWatchRoots()
    {
        var settings = CreateValidSettings();
        settings.Watch.Roots.Clear();
        settings.Immich.BaseUrl = "http://immich.test:2283";

        new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code).Should().NotContain("WatchRootsRequired");
    }

    [Fact]
    public void RemoteOverrideOnAConfigWithoutARemoteSshBlockIsInvalid()
    {
        var settings = CreateValidSettings();
        ProcessingModeOverride.Remote.Apply(settings);

        new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code).Should().Contain("RemoteHostRequired");
    }

    [Fact]
    public void ValidateCompleteSettingsIsValid()
    {
        var settings = CreateValidSettings();

        new SettingsValidator().Validate(settings).Should().BeEmpty();
    }

    [Fact]
    public void ValidateRejectsDecodeTestOutsideKnownModes()
    {
        var settings = CreateValidSettings();
        settings.Validation.DecodeTest = "true";

        new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code).Should().Contain("InvalidDecodeTest");
    }

    [Theory]
    [InlineData("delete", "Archive", null)]
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

        var codes = new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code);

        if (expectedCode is null) codes.Should().BeEmpty();
        else codes.Should().Equal(expectedCode);
    }

    [Fact]
    public void ValidateRemoteModeRequiresOnlyRemoteSpecificToolSettings()
    {
        var settings = CreateValidSettings();
        settings.Processing.Mode = ProcessingModes.RemoteSsh;
        settings.Processing.RemoteSsh.Host = "video-worker";
        settings.Tools.AbAv1Path = string.Empty;

        new SettingsValidator().Validate(settings).Should().BeEmpty();
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

        var codes = new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code);

        codes.Should().Contain([
            "SshPathRequired",
            "SftpPathRequired",
            "InvalidRemoteHost",
            "InvalidRemoteWorkingDirectory",
            "InvalidRemoteMinimumCpuCount",
            "InvalidRemoteMinimumMemory",
            "InvalidRemoteDiskMultiplier"]);
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

            new SettingsValidator().Validate(settings).Should().BeEmpty();
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

        new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code).Should().Contain([
            "HetznerSshKeyRequired",
            "RemoteIdentityFileRequired",
            "HetznerBootstrapMissing",
            "InvalidHetznerBootstrapTimeout"]);
    }

    [Fact]
    public void ValidateManagedHetznerRejectsEmptyLocationList()
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
                    IdentityFile = identity,
                    Hetzner = new HetznerSettings { Locations = [], SshKeyName = "video-optimiser-hetzner-cx43", BootstrapScriptPath = bootstrap }
                }
            };

            new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code).Should().Contain("HetznerLocationsRequired");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(new string[0], null, "HetznerServerTypesRequired")]
    [InlineData(new[] { "cx43", " " }, null, "InvalidHetznerServerTypes")]
    [InlineData(new[] { "cx43" }, "-0.10", "InvalidHetznerMaxHourlyPrice")]
    [InlineData(new[] { "cx43" }, "0", "InvalidHetznerMaxHourlyPrice")]
    public void ValidateManagedHetznerRejectsUnusableServerTypesOrPriceCap(string[] serverTypes, string? maxHourlyPrice, string expectedCode)
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
                    MaxHourlyPrice = maxHourlyPrice is null ? 0.30m : decimal.Parse(maxHourlyPrice, System.Globalization.CultureInfo.InvariantCulture)
                }
            }
        };

        new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code).Should().Contain(expectedCode);
    }

    [Fact]
    public void ValidateRejectsUnknownProcessingMode()
    {
        var settings = CreateValidSettings();
        settings.Processing.Mode = "cloud";

        new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code).Should().Contain("InvalidProcessingMode");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/var/tmp/../video-optimiser")]
    [InlineData("/var/./video-optimiser")]
    public void ValidateRemoteModeRejectsUnsafeWorkingDirectory(string workingDirectory)
    {
        var settings = CreateValidSettings();
        settings.Processing.Mode = ProcessingModes.RemoteSsh;
        settings.Processing.RemoteSsh.Host = "video-worker";
        settings.Processing.RemoteSsh.WorkingDirectory = workingDirectory;

        new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code).Should().Contain("InvalidRemoteWorkingDirectory");
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

        var codes = new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code);

        if (expectedCode is null) codes.Should().BeEmpty();
        else codes.Should().Equal(expectedCode);
    }

    internal static AppSettings CreateValidSettings() => new()
    {
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = "C:\\Videos" }] },
        Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "8Mbps", MinimumFileSize = "800MiB" }] },
        Original = new OriginalSettings { Action = "delete" }
    };
}
