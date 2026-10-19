using FluentAssertions;
using VideoOptimiser.Cli.Features.Tui;

namespace VideoOptimiser.UnitTests.Features.Tui;

public sealed class TuiKeysTests
{
    /// <summary>
    /// The key guide is built from the key table, so a key added to a pane's table is listed in that pane's group with its own
    /// words, and a key with no words of its own (<c>X</c>) shares the row before it instead of dropping out.
    /// </summary>
    [Fact]
    public void EveryKeyInTheTableIsInItsPanesGroupOfTheGuide()
    {
        foreach (var pane in TuiKeys.Panes)
        {
            var group = TuiHelp.Groups.Single(candidate => candidate.Title == pane.Title);
            foreach (var key in pane.All)
            {
                var row = group.Keys.SingleOrDefault(help => help.Key == key.GuideName || help.Key.Split(" / ").Contains(key.GuideName));
                row.Should().NotBeNull($"{key.GuideName} is in the {pane.Title} table");
                if (key.Help is { } what) row!.What.Should().Be(what, key.GuideName);
            }
        }
    }
}
