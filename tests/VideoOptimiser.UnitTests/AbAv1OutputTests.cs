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

    [Fact]
    public void TheRealLocalLogsBestAttemptIsTheSmallestThatReachedTheVmaf()
    {
        // Run with --min-vmaf 95 --max-encoded-percent 80: CRF 26 and 28 passed at 97% and 84%, CRF 29 missed at 94.74.
        var exception = AbAv1Output.NoWorthwhileCrf(Fixture("local-crf-not-worth-converting.stderr.log"), new QualitySettings { MinimumVmaf = 95, MaxEncodedPercent = 80 });

        exception!.Best.Should().Be(new Application.Tools.CrfAttempt(28, 95.19, 84));
    }
}
