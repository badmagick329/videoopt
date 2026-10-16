using FluentAssertions;
using VideoOptimiser.Infrastructure.Features.Run;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class DetachedLauncherTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("--flag=value", "--flag=value")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    // Backslashes matter only before a quote: left alone in the middle, doubled before the closing quote.
    [InlineData(@"C:\a\b", @"C:\a\b")]
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]
    [InlineData(@"C:\my dir\sub", "\"C:\\my dir\\sub\"")]
    // A backslash run before an embedded quote is doubled, then the quote is escaped.
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("\\\\", "\\\\")]
    public void QuoteArgumentFollowsCommandLineToArgvWRules(string argument, string expected)
    {
        DetachedLauncher.QuoteArgument(argument).Should().Be(expected);
    }

    [Fact]
    public void TheCommandLineStartsWithTheQuotedExecutableAndJoinsTheQuotedArguments()
    {
        DetachedLauncher.BuildCommandLine(@"C:\Program Files\videoopt\videoopt.exe", ["runner", "--config", @"C:\my dir\", ""])
            .Should().Be("\"C:\\Program Files\\videoopt\\videoopt.exe\" runner --config \"C:\\my dir\\\\\" \"\"");
    }
}
