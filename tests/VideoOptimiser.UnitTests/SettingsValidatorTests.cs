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
    public void ValidateCompleteSettingsIsValid()
    {
        var settings = CreateValidSettings();

        new SettingsValidator().Validate(settings).Should().BeEmpty();
    }

    [Fact]
    public void ValidateDeleteWithoutProbeRejectsUnsafeConfiguration()
    {
        var settings = CreateValidSettings();
        settings.Original.Action = "delete";
        settings.Validation.RunFfprobe = false;

        new SettingsValidator().Validate(settings).Select(diagnostic => diagnostic.Code).Should().Contain("UnsafeDeleteValidation");
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

    internal static AppSettings CreateValidSettings() => new()
    {
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = "C:\\Videos" }] },
        Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "8Mbps", MinimumFileSize = "800MiB" }] },
        Original = new OriginalSettings { Action = "delete" }
    };
}
