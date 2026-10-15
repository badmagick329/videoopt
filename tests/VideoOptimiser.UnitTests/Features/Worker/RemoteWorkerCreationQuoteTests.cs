using FluentAssertions;
using VideoOptimiser.Application.Features.Worker;

namespace VideoOptimiser.UnitTests.Features.Worker;

public sealed class RemoteWorkerCreationQuoteTests
{
    [Fact]
    public void AnUnlistedPriceIsSaidPlainly()
    {
        var unlistedFirst = new RemoteWorkerCreationOption("cx43", "hel1", null);
        var unlistedFallback = new RemoteWorkerCreationQuote(new("cx43", "hel1", 0.031m), new("ccx33", "sin", null));

        new RemoteWorkerCreationQuote(unlistedFirst, unlistedFirst).FormatPrompt().Should().Be(Lines(
            "Will create cx43 in hel1 (price not listed).",
            "Create the server? [y/N] "));
        unlistedFallback.FormatPrompt().Should().Be(Lines(
            "Will create cx43 in hel1 at EUR 0.031/h incl. VAT.",
            "If that placement fails, a fallback may cost more (ccx33 in sin, price not listed).",
            "Create the server? [y/N] "));
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);
}
