using FluentAssertions;
using VideoOptimiser.Application.Features.Config;
using VideoOptimiser.Cli.Features.Config;

namespace VideoOptimiser.UnitTests.Features.Config;

public sealed class ConfigInitQuestionTests
{
    private static readonly string Question = string.Join(Environment.NewLine,
        "Quality target?",
        "  1. Archive   VMAF 97: indistinguishable from the original",
        "  2. Standard  VMAF 95: hard to tell apart (default)",
        "  3. Compact   VMAF 93: small visible loss, bigger savings",
        "Choose 1-3 [2]: ");

    [Theory]
    [InlineData("1", QualityLevel.Archive)]
    [InlineData("", QualityLevel.Standard)]
    [InlineData(" 3 ", QualityLevel.Compact)]
    [InlineData(null, QualityLevel.Standard)]
    public void AsksAndReadsTheAnswer(string? answer, QualityLevel expected)
    {
        var output = new StringWriter();
        // A null answer is the end of input: no line at all.
        var input = new StringReader(answer is null ? string.Empty : answer + Environment.NewLine);

        var level = ConfigInitQuestion.Choose(null, interactive: true, input, output);

        level.Should().Be(expected);
        output.ToString().Should().Be(Question);
    }

    [Fact]
    public void AsksAgainUntilTheAnswerIsALevel()
    {
        var output = new StringWriter();

        var level = ConfigInitQuestion.Choose(null, interactive: true, new StringReader($"4{Environment.NewLine}archive{Environment.NewLine}1{Environment.NewLine}"), output);

        level.Should().Be(QualityLevel.Archive);
        output.ToString().Should().EndWith($"Choose 1-3 [2]: Enter 1, 2 or 3.{Environment.NewLine}Choose 1-3 [2]: Enter 1, 2 or 3.{Environment.NewLine}Choose 1-3 [2]: ");
    }

    [Fact]
    public void WithoutAConsoleItUsesStandardAndSaysSo()
    {
        var output = new StringWriter();

        var level = ConfigInitQuestion.Choose(null, interactive: false, new StringReader("1" + Environment.NewLine), output);

        level.Should().Be(QualityLevel.Standard);
        output.ToString().Should().Be("No console to ask on, so the quality target is Standard (VMAF 95). Pass --quality archive, standard or compact to choose." + Environment.NewLine);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFlagSkipsTheQuestion(bool interactive)
    {
        var output = new StringWriter();

        var level = ConfigInitQuestion.Choose(QualityLevel.Compact, interactive, new StringReader("1" + Environment.NewLine), output);

        level.Should().Be(QualityLevel.Compact);
        output.ToString().Should().BeEmpty();
    }
}
