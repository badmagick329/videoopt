using FluentAssertions;
using VideoOptimiser.Cli;

namespace VideoOptimiser.UnitTests;

/// <summary>Failure categories are names for code and <c>--json</c>; a list shows a person a phrase.</summary>
public sealed class FailureWordsTests
{
    [Theory]
    [InlineData("EncodeFailed", true, "Encoding failed: ab-av1 exited with 1.")]
    [InlineData("EncodeFailed", false, "encoding failed: ab-av1 exited with 1.")]
    [InlineData("CrfSearchFailed", true, "The CRF search failed: ab-av1 exited with 1.")]
    [InlineData("RemoteUnavailable", true, "The worker couldn't be reached: ab-av1 exited with 1.")]
    [InlineData("Ineligible", true, "Doesn't fit your rules: ab-av1 exited with 1.")]
    [InlineData("ManualInterventionRequired", true, "Needs your review: ab-av1 exited with 1.")]
    [InlineData("SourceMissing", true, "ab-av1 exited with 1.")]
    [InlineData("SomethingNew", true, "ab-av1 exited with 1.")]
    public void ACategoryReadsAsAPhraseBeforeItsMessage(string category, bool sentence, string expected) =>
        FailureWords.Describe(category, "ab-av1 exited with 1.", sentence).Should().Be(expected);

    [Fact]
    public void ADroppedVideoAddsNothingToItsStatus() => FailureWords.Describe("Cancelled", "Dropped by user.", sentence: false).Should().BeNull();
}
