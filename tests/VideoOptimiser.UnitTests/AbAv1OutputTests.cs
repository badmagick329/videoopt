using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests;

public sealed class AbAv1OutputTests
{
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AbAv1", name));

    [Fact]
    public void TheRealRemoteLogsBestAttemptIsTheCrfSearchVerdictNotAPassingSample()
    {
        // Its per-sample line "sample 2/2 crf 32 VMAF 97.39 (82%)" passes the VMAF on its own, though CRF 32 as a whole
        // reached only 96.64; reading that line made CRF 32 at 82% the "best" attempt.
        var exception = AbAv1Output.NoWorthwhileCrf(Fixture("remote-crf-not-worth-converting.log"), new QualitySettings { MinimumVmaf = 97, MaxEncodedPercent = 80 });

        exception!.Best.Should().Be(new Application.Tools.CrfAttempt(31, 97.31, 95));
    }
}
