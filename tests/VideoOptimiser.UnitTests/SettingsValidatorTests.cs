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

    internal static AppSettings CreateValidSettings() => new()
    {
        Watch = new WatchSettings { Roots = [new WatchRootSettings { Path = "C:\\Videos" }] },
        Eligibility = new EligibilitySettings { Rules = [new EligibilityRuleSettings { Codecs = ["h264"], Resolution = "1080p-1440p", MinimumVideoBitrate = "8Mbps", MinimumFileSize = "800MiB" }] },
        Original = new OriginalSettings { Action = "delete" }
    };
}
