using System.Text.RegularExpressions;
using FluentAssertions;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli;
using VideoOptimiser.Cli.Features.Finalize;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// Colour on a real terminal. The words must never change with it, so most tests render the same events twice and require
/// the coloured text, stripped of its codes, to equal the plain text.
/// </summary>
public sealed partial class ColourOutputTests
{
    private const string Esc = "\u001b[";
    private const string Reset = "\u001b[0m";

    [Fact]
    public void EmptyTextGetsNoCodesSoABlankLineStaysBlank() =>
        TerminalStyle.Ansi.Dim(string.Empty).Should().BeEmpty();

    [Theory]
    [InlineData(false, null, null, false, true)]
    [InlineData(true, null, "xterm", false, false)]
    [InlineData(false, "1", "xterm", false, false)]
    [InlineData(false, "", "xterm", false, true)]
    [InlineData(false, null, "DUMB", false, false)]
    [InlineData(false, null, "xterm", true, false)]
    public void ColourNeedsATerminalNoOptOutAndNoJson(bool redirected, string? noColor, string? term, bool json, bool expected) =>
        TerminalStyle.IsColourWanted(redirected, noColor, term, json).Should().Be(expected);

    [Fact]
    public void AJobBlockIsBoldAtTheHeaderDimInDetailAndColouredByTheOutcomesTone()
    {
        var output = new StringWriter { NewLine = "\n" };
        var console = new JobProgressConsole(output, TerminalStyle.Ansi);

        console.Report(new JobProgress(JobProgressKind.Header, "[1/1] a.mp4 (1.0 MiB)"));
        console.Report(JobProgress.Step("Finding the best CRF."));
        console.Report(JobProgress.Detail("CRF 34   VMAF 94.35   size  15%   below target"));
        console.Report(JobProgress.Tool("a line the translator did not recognise"));
        console.Report(JobProgress.Outcome(JobTone.Success, "Ready to finalise: 1.0 MiB -> 500.0 KiB, 51.2% saved."));
        console.Report(JobProgress.Outcome(JobTone.Warning, "Not worth converting: too big."));
        console.Report(JobProgress.Outcome(JobTone.Failure, "Failed validation: duration differs"));

        output.ToString().Should().Be(
            $"\n{Esc}1m[1/1] a.mp4 (1.0 MiB){Reset}\n" +
            "      Finding the best CRF.\n" +
            $"        {Esc}2mCRF 34   VMAF 94.35   size  15%   below target{Reset}\n" +
            $"        {Esc}2ma line the translator did not recognise{Reset}\n" +
            $"      {Esc}32mReady to finalise: 1.0 MiB -> 500.0 KiB, 51.2% saved.{Reset}\n" +
            $"      {Esc}33mNot worth converting: too big.{Reset}\n" +
            $"      {Esc}31mFailed validation: duration differs{Reset}\n");
    }

    [Fact]
    public void DiscoveryColoursTheVerdictsAndDimsTheExplanationButLeavesTheFactsPlain()
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var console = new ImmichDiscoveryConsole(output, TerminalStyle.Ansi, error, TerminalStyle.Ansi);

        console.Report(new ImmichDiscoveryProgress.Started(1, 418, 3));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", new DateTime(2025, 7, 18)));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Skipped, "rule 1 needs 2GiB", null, 0, new CandidateFacts("h264", 3840, 2160, 41_700_000, 519_200_000)));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(2, "b.mp4", new DateTime(2026, 2, 23)));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 1, new CandidateFacts("hevc", 3840, 2160, 33_000_000, 334_900_000), 3));
        console.Report(new ImmichDiscoveryProgress.CandidateDownloading(3, "c.mp4", new DateTime(2023, 1, 2)));
        console.Report(new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Issue, "Immich answered 404", "it will be tried again next run", 0));
        console.Report(new ImmichDiscoveryProgress.AssetExcluded("fav.mp4", "0f1e", "is a favourite"));

        output.ToString().Should().Be(
            $"{Esc}2mLooking in Immich for up to 1 new video to convert.{Reset}\n" +
            "418 videos that didn't fit your earlier rules are checked again under the current ones when the search reaches them.\n" +
            $"{Esc}2mEach video below is downloaded and checked. If it doesn't fit your rules, the download is deleted and the video is not checked again until your rules change.{Reset}\n" +
            "\n" +
            $"  1. a.mp4  2025-07-18  h264 4K 42 Mbps  495.1 MiB  {Esc}33mdoesn't fit: rule 1 needs 2GiB{Reset}\n" +
            $"  2. b.mp4  2026-02-23  hevc 4K 33 Mbps  319.4 MiB  {Esc}32mqueued: fits rule 3 (1 of 1){Reset}\n" +
            $"  3. c.mp4  2023-01-02  {Esc}31missue: Immich answered 404; it will be tried again next run{Reset}\n");
        error.ToString().Should().Be($"{Esc}31mLeft alone: fav.mp4 (Immich's search should not have returned it: it is a favourite).{Reset}\n");
    }

    [Fact]
    public void DiscoveryReadsTheSameWithAndWithoutColour()
    {
        ImmichProgress[] events =
        [
            new ImmichDiscoveryProgress.Started(null, 5, 1),
            new ImmichProgress.Restaging("r.mp4", Guid.Empty),
            new ImmichProgress.Restaged("r.mp4", "gone"),
            new ImmichDiscoveryProgress.CandidateDownloading(1, "a.mp4", new DateTime(2025, 7, 18)),
            new ImmichDiscoveryProgress.CandidateJudged(CandidateOutcome.Queued, null, null, 3, new CandidateFacts("hevc", 1920, 1080, 8_100_000, 10), 1),
            new ImmichProgress.AssetIssue("b.txt", "cannot be staged"),
            new ImmichDiscoveryProgress.AssetExcluded("fav.mp4", "0f1e", "is a favourite")
        ];

        var (plainOutput, plainError) = RenderDiscovery(events, TerminalStyle.Plain);
        var (colourOutput, colourError) = RenderDiscovery(events, TerminalStyle.Ansi);

        plainOutput.Should().NotContain(Esc);
        plainError.Should().NotContain(Esc);
        Strip(colourOutput).Should().Be(plainOutput);
        Strip(colourError).Should().Be(plainError);
    }

    [Fact]
    public void FinalisationColoursDoneGreenOnOutputAndFailedRedOnErrorEachByItsOwnStreamsStyle()
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var done = new JobRecord { Id = Guid.NewGuid(), SourcePath = "D:\\v\\a.mp4" };
        var failed = new JobRecord { Id = Guid.NewGuid(), SourcePath = "D:\\v\\b.mp4" };

        // Output is a terminal and error is redirected: the failure line must carry no codes.
        var console = new FinalizationConsole(output, TerminalStyle.Ansi, error, TerminalStyle.Plain);
        console.Outcome(done, new FinalizationResult(done, ExitCode.Success, "Original deleted."));
        console.Outcome(failed, new FinalizationResult(failed, ExitCode.FinalisationFailure, "The AV1 changed since validation."));
        console.Summary(2, 1);

        output.ToString().Should().Be(
            $"{Esc}32mDone{Reset}    a.mp4: Original deleted.\n" +
            $"{Esc}32mFinalised: 1.{Reset} {Esc}31mFailed: 1.{Reset}\n");
        error.ToString().Should().Be("Failed  b.mp4: The AV1 changed since validation.\n");

        var redError = new StringWriter { NewLine = "\n" };
        new FinalizationConsole(new StringWriter(), TerminalStyle.Plain, redError, TerminalStyle.Ansi)
            .Outcome(failed, new FinalizationResult(failed, ExitCode.FinalisationFailure, "x"));
        redError.ToString().Should().Be($"{Esc}31mFailed{Reset}  b.mp4: x\n");
    }

    [Fact]
    public void TheRunTallyColoursEachPartByItsOutcomeAndLeavesNothingToColourWhenEmpty()
    {
        OutcomeSummary.Run(1, 2, 3, TerminalStyle.Ansi).Should().Be(
            $"{Esc}32mReady to finalise: 1.{Reset} {Esc}33mNot worth converting: 2.{Reset} {Esc}31mFailed: 3.{Reset}");
        OutcomeSummary.Run(0, 0, 0, TerminalStyle.Ansi).Should().Be("Nothing to finalise.");
        OutcomeSummary.Run(1, 2, 3, TerminalStyle.Plain).Should().Be("Ready to finalise: 1. Not worth converting: 2. Failed: 3.");
        // Earlier runs' ready videos make "Nothing to finalise." false, so a run that made nothing has no tally; one that failed still has.
        OutcomeSummary.Run(0, 0, 0, TerminalStyle.Plain, othersReady: true).Should().BeNull();
        OutcomeSummary.Run(0, 0, 1, TerminalStyle.Plain, othersReady: true).Should().Be("Failed: 1.");
    }

    private static (string Output, string Error) RenderDiscovery(ImmichProgress[] events, TerminalStyle style)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var console = new ImmichDiscoveryConsole(output, style, error, style);
        foreach (var value in events) console.Report(value);
        return (output.ToString(), error.ToString());
    }

    private static string Strip(string text) => EscapeCodes().Replace(text, string.Empty);

    [GeneratedRegex("\u001b\\[\\d+m")]
    private static partial Regex EscapeCodes();
}
