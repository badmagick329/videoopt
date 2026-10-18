using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Finalize;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests.Features.Finalize;

/// <summary>
/// What <c>finalize</c> and <c>immich finalize</c> print and return, with the finalisation service, the job list and Immich
/// faked: the early returns, the prompt, and how a batch ends. The services' own behaviour is in their own tests.
/// </summary>
public sealed class FinalizeCommandsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly FakeFinalizer _finalizer = new();
    private readonly FakeImmich _immich = new();
    private readonly List<JobRecord> _jobs = [];

    public FinalizeCommandsTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    [Fact]
    public async Task AnInvalidJobIdIsRefusedBeforeFinalizing()
    {
        var (code, stdout, stderr) = await RunAsync(null, "finalize", "not-an-id");

        code.Should().Be((int)ExitCode.InvalidArguments);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("Job ID is invalid.\n");
        _finalizer.Calls.Should().BeEmpty();
    }

    /// <summary>A clean video finalises without a question: scripts and the e2e test run <c>finalize &lt;id&gt;</c> unattended.</summary>
    [Fact]
    public async Task ASuccessfulFinalizeOfACleanJobPrintsItsMessageOnStdoutWithoutAPrompt()
    {
        var job = FileJob("clip-a.mp4", null, null, null);
        _jobs.Add(job);
        _finalizer.Results[job.Id] = new(job, ExitCode.Success, "Original deleted.");

        var (code, stdout, stderr) = await RunAsync(null, "finalize", job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be("Original deleted.\n");
        _finalizer.Calls.Should().Equal(job.Id);
    }

    [Theory]
    [InlineData(false, "y", true)]
    [InlineData(false, "n", false)]
    [InlineData(true, "y", true)]
    public async Task FinalizingAWarnedJobListsItsWarningsAndFinalisesOnlyOnYes(bool immich, string answer, bool finalises)
    {
        var job = Warned(immich ? ImmichJob("clip-a.mp4", "asset-a") : FileJob("clip-a.mp4", null, null, null));
        _jobs.Add(job);
        _finalizer.Results[job.Id] = new(job, ExitCode.Success, "Original deleted.");

        var (code, stdout, stderr) = await RunAsync(answer + "\n", "finalize", job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        var originals = immich
            ? "Each Immich original goes to Immich's trash once its AV1 is uploaded with its albums, tags and other relations."
            : "Each original is deleted and its AV1 takes its place as an .mp4 file.";
        stdout.ReplaceLineEndings("\n").Should().Be(
            "clip-a.mp4  sizes unknown\n" +
            "Validation warnings:\n" +
            "  The AV1 is 2.0 s shorter than the original.\n" +
            "  The AV1 has no audio.\n" +
            $"{originals}\n" +
            $"Finalise it? [y/N] {(finalises ? "Original deleted." : "Finalisation cancelled.")}\n");
        _finalizer.Calls.Should().HaveCount(finalises ? 1 : 0);
    }

    /// <summary>A note is not a warning: finalising one video lists its notes under their own heading, after any warnings, and a noted video with no warnings still finalises without a question.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinalizingANotedJobListsItsNotesApartFromItsWarnings(bool warned)
    {
        var job = Noted(warned ? Warned(FileJob("clip-a.mp4", null, null, null)) : FileJob("clip-a.mp4", null, null, null));
        _jobs.Add(job);
        _finalizer.Results[job.Id] = new(job, ExitCode.Success, "Original deleted.");

        var (code, stdout, stderr) = await RunAsync("y\n", "finalize", job.Id.ToString("N"));

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(warned
            ? "clip-a.mp4  sizes unknown\n" +
              "Validation warnings:\n" +
              "  The AV1 is 2.0 s shorter than the original.\n" +
              "  The AV1 has no audio.\n" +
              "Notes:\n" +
              "  Audio track 2 (apac) left out: FFmpeg can't copy it. The other audio tracks are kept.\n" +
              "Each original is deleted and its AV1 takes its place as an .mp4 file.\n" +
              "Finalise it? [y/N] Original deleted.\n"
            // Only a warned video asks first; the noted one finalises at once.
            : "Original deleted.\n");
        _finalizer.Calls.Should().Equal(job.Id);
    }

    /// <summary>A note holds nothing back from a batch: the noted, unwarned video is finalised with the clean one, and nothing is left out.</summary>
    [Fact]
    public async Task ReadyFinalisesANotedJobWithNoWarnings()
    {
        var clean = FileJob("clip-a.mp4", null, null, null);
        var noted = Noted(FileJob("clip-b.mp4", null, null, null));
        _jobs.AddRange([clean, noted]);
        _finalizer.Results[clean.Id] = new(clean, ExitCode.Success, "Original deleted.");
        _finalizer.Results[noted.Id] = new(noted, ExitCode.Success, "Original deleted.");

        var (code, stdout, stderr) = await RunAsync("y\n", "finalize", "--ready");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().NotContain("Left out");
        _finalizer.Calls.Should().Equal(clean.Id, noted.Id);
    }

    [Fact]
    public async Task ReadyFinalisesOnlyTheCleanJobAndNamesTheWarnedOneBeforeThePromptAndAtTheEnd()
    {
        var clean = FileJob("clip-a.mp4", null, null, null);
        var warned = Warned(FileJob("clip-b.mp4", null, null, null));
        _jobs.AddRange([clean, warned]);
        _finalizer.Results[clean.Id] = new(clean, ExitCode.Success, "Original deleted.");

        var (code, stdout, stderr) = await RunAsync("y\n", "finalize", "--ready");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            "clip-a.mp4  sizes unknown\n" +
            "Each original is deleted and its AV1 takes its place as an .mp4 file.\n" +
            "Left out because validation warned about them. Watch each one, then finalise it on its own:\n" +
            $"  clip-b.mp4  {FinalizeHint(warned)}\n" +
            "Finalise these jobs? [y/N] Done    clip-a.mp4: Original deleted.\n" +
            "Finalised: 1.\n" +
            "Left out with validation warnings:\n" +
            $"  clip-b.mp4  {FinalizeHint(warned)}\n");
        _finalizer.Calls.Should().Equal(clean.Id);
    }

    /// <summary>A finalisation that was under way when it stopped is resumed in bulk despite its warnings: its user already chose.</summary>
    [Fact]
    public async Task ReadyTakesAResumedFinalisationWithWarnings()
    {
        var resumed = Warned(ImmichJob("clip-a.mp4", "asset-a"));
        resumed.FailureCategory = FailureCategories.FinalizationIncomplete;
        resumed.FailureMessage = "The upload finished.";
        _jobs.Add(resumed);
        _finalizer.Results[resumed.Id] = new(resumed, ExitCode.Success, "New asset n1; the original is in Immich's trash.");

        var (code, stdout, _) = await RunAsync("y\n", "finalize", "--ready");

        code.Should().Be((int)ExitCode.Success);
        stdout.Should().NotContain("Left out");
        _finalizer.Calls.Should().Equal(resumed.Id);
    }

    [Theory]
    [InlineData("finalize", "every ready video has validation warnings")]
    [InlineData("immich", "every ready Immich video has validation warnings")]
    public async Task BulkWithOnlyWarnedJobsNamesThemAndSucceedsWithoutAPrompt(string verb, string why)
    {
        var warned = Warned(ImmichJob("clip-a.mp4", "asset-a"));
        _jobs.Add(warned);

        var (code, stdout, stderr) = verb == "finalize" ? await RunAsync("y\n", "finalize", "--ready") : await RunAsync("y\n", "immich", "finalize");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            $"Nothing to finalise in bulk: {why}. Watch each one, then finalise it on its own:\n" +
            $"  clip-a.mp4  {FinalizeHint(warned)}\n");
        _finalizer.Calls.Should().BeEmpty();
        _immich.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedFinalizePrintsItsMessageOnStderrWithTheResultsExitCode()
    {
        var id = Guid.NewGuid();
        _finalizer.Results[id] = new(null, ExitCode.FinalisationFailure, "Original changed after encoding.");

        var (code, stdout, stderr) = await RunAsync(null, "finalize", id.ToString("N"));

        code.Should().Be((int)ExitCode.FinalisationFailure);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be("Original changed after encoding.\n");
    }

    [Fact]
    public async Task ReadyListsEachJobTheOriginalActionAndThePromptThenCancelsWithoutFinalizing()
    {
        _jobs.Add(FileJob("clip-a.mp4", 1024 * 1024 * 1024, 400 * 1024 * 1024, 60m));
        // A file job whose finalisation Ctrl+C or a crash stopped is listed too, so `finalize` can check what moved (VOPT-70).
        var stopped = FileJob("clip-b.mp4", null, null, null);
        stopped.Status = JobStatus.Interrupted;
        stopped.ResumeStatus = JobStatus.Finalizing;
        _jobs.Add(stopped);

        var (code, stdout, stderr) = await RunAsync(null, "finalize", "--ready");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            "clip-a.mp4  1.0 GiB -> 400.0 MiB, 60.0% saved\n" +
            "clip-b.mp4  sizes unknown\n" +
            "Each original is deleted and its AV1 takes its place as an .mp4 file.\n" +
            "Finalise these jobs? [y/N] Finalisation cancelled.\n");
        _finalizer.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("archive", "Archive", "Each original moves to an Archive folder beside it and its AV1 takes its place as an .mp4 file.")]
    [InlineData("keep", "Archive", "Each original stays; its AV1 is installed beside it as <name>.av1<extension>.")]
    [InlineData("archive", "<absolute>", "Each original moves to <absolute> and its AV1 takes its place as an .mp4 file.")]
    public async Task ReadyDescribesWhatHappensToAFileJobsOriginalPerTheConfiguredAction(string action, string archive, string line)
    {
        _jobs.Add(FileJob("clip-a.mp4", null, null, null));
        // An archive in an absolute folder is named by its path.
        if (archive == "<absolute>") archive = Path.Combine(_directory, "archived");
        line = line.Replace("<absolute>", archive, StringComparison.Ordinal);

        var (_, stdout, _) = await RunAsync(null, "finalize", "--ready", extraConfig: $"original:\n  action: {action}\n  archiveDirectory: '{archive}'\n");

        stdout.ReplaceLineEndings("\n").Should().Be($"clip-a.mp4  sizes unknown\n{line}\nFinalise these jobs? [y/N] Finalisation cancelled.\n");
    }

    /// <summary>The AV1 takes the container of its own output, not the original's: a .mov original is replaced by an .mp4 file.</summary>
    [Fact]
    public async Task ReadyNamesTheContainerEachAv1TakesItsPlaceIn()
    {
        var mov = FileJob("IMG_0001.MOV", null, null, null);
        mov.OutputPath = Path.Combine(_directory, "IMG_0001.encoding.MP4");
        var mkv = FileJob("clip.mkv", null, null, null);
        mkv.OutputPath = Path.Combine(_directory, "clip.encoding.mkv");
        _jobs.AddRange([mov, mkv]);

        var (_, stdout, _) = await RunAsync(null, "finalize", "--ready");

        stdout.ReplaceLineEndings("\n").Should().Be(
            "IMG_0001.MOV  sizes unknown\n" +
            "clip.mkv  sizes unknown\n" +
            "Each original is deleted and its AV1 takes its place as an .mkv or .mp4 file.\n" +
            "Finalise these jobs? [y/N] Finalisation cancelled.\n");
    }

    [Fact]
    public async Task ReadyWithAPartialFailureReportsEachJobAndEndsInPartialSuccess()
    {
        var done = FileJob("clip-a.mp4", null, null, null);
        var broken = FileJob("clip-b.mp4", null, null, null);
        _jobs.AddRange([done, broken]);
        _finalizer.Results[done.Id] = new(done, ExitCode.Success, "Original deleted.");
        _finalizer.Results[broken.Id] = new(broken, ExitCode.FinalisationFailure, "Original changed after encoding.");

        var (code, stdout, stderr) = await RunAsync("y\n", "finalize", "--ready");

        code.Should().Be((int)ExitCode.PartialSuccess);
        stderr.ReplaceLineEndings("\n").Should().Be("Failed  clip-b.mp4: Original changed after encoding.\n");
        stdout.ReplaceLineEndings("\n").Should().Be(
            "clip-a.mp4  sizes unknown\n" +
            "clip-b.mp4  sizes unknown\n" +
            "Each original is deleted and its AV1 takes its place as an .mp4 file.\n" +
            "Finalise these jobs? [y/N] Done    clip-a.mp4: Original deleted.\n" +
            "Finalised: 1. Failed: 1.\n");
        _finalizer.Calls.Should().Equal(done.Id, broken.Id);
    }

    [Fact]
    public async Task ReadyListsAJobStoppedWhileBeingFinalisedAndLeavesOutOneThatIsNotReady()
    {
        var queued = FileJob("clip-a.mp4", null, null, null);
        queued.Status = JobStatus.Queued;
        var stopped = ImmichJob("clip-b.mp4", "asset-b");
        stopped.Status = JobStatus.Finalizing;
        _jobs.AddRange([queued, stopped]);

        var (code, stdout, stderr) = await RunAsync(null, "finalize", "--ready");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        // No file job is listed, so there is no original-action line either.
        stdout.ReplaceLineEndings("\n").Should().Be(
            "clip-b.mp4  sizes unknown\n" +
            "Each Immich original goes to Immich's trash once its AV1 is uploaded with its albums, tags and other relations.\n" +
            "Finalise these jobs? [y/N] Finalisation cancelled.\n");
    }

    [Fact]
    public async Task ReadyWhereEveryJobFailsEndsInPartialSuccessUnlikeImmichFinalize()
    {
        var first = FileJob("clip-a.mp4", null, null, null);
        var second = FileJob("clip-b.mp4", null, null, null);
        _jobs.AddRange([first, second]);
        _finalizer.Results[first.Id] = new(first, ExitCode.FinalisationFailure, "Original changed after encoding.");
        _finalizer.Results[second.Id] = new(second, ExitCode.FinalisationFailure, "Original changed after encoding.");

        var (code, stdout, stderr) = await RunAsync("y\n", "finalize", "--ready");

        // Only immich finalize tells a total failure from a partial one; finalize --ready ends in PartialSuccess either way.
        code.Should().Be((int)ExitCode.PartialSuccess);
        stderr.ReplaceLineEndings("\n").Should().Be(
            "Failed  clip-a.mp4: Original changed after encoding.\n" +
            "Failed  clip-b.mp4: Original changed after encoding.\n");
        stdout.ReplaceLineEndings("\n").Should().EndWith("Finalise these jobs? [y/N] Failed: 2.\n");
    }

    [Fact]
    public async Task ReadyWhereEveryJobFinalisesEndsInSuccess()
    {
        var job = FileJob("clip-a.mp4", null, null, null);
        _jobs.Add(job);
        _finalizer.Results[job.Id] = new(job, ExitCode.Success, "Original deleted.");

        var (code, stdout, stderr) = await RunAsync("y\n", "finalize", "--ready");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().EndWith("Finalise these jobs? [y/N] Done    clip-a.mp4: Original deleted.\nFinalised: 1.\n");
    }

    [Fact]
    public async Task ImmichFinalizeWithoutImmichConfiguredSaysToSetIt()
    {
        var (code, stdout, stderr) = await RunAsync(null, "immich", "finalize", configureImmich: false);

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be($"Set immich.baseUrl in the configuration first; 'video-optimiser doctor --config \"{Path.Combine(_directory, "config.yaml")}\"' then checks the connection and the API key.\n");
        _immich.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ImmichFinalizeWithNothingReadyOnlySaysSo()
    {
        _jobs.Add(FileJob("clip-a.mp4", null, null, null));

        var (code, stdout, stderr) = await RunAsync(null, "immich", "finalize");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be("No Immich jobs are ready to finalise.\n");
        _immich.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ImmichFinalizeListsEachAssetWithWhatImmichSaysOfItThenCancelsWithoutFinalizing()
    {
        var live = ImmichJob("clip-a.mp4", "asset-a", 1024 * 1024 * 1024, 400 * 1024 * 1024, 60m);
        var trashed = ImmichJob("clip-b.mp4", "asset-b");
        var gone = ImmichJob("clip-c.mp4", "asset-c");
        gone.FailureCategory = FailureCategories.FinalizationIncomplete;
        gone.FailureMessage = "The upload finished.";
        // A job stopped while being finalised is listed; one that is still queued is neither listed nor read from Immich.
        var stopped = ImmichJob("clip-d.mp4", "asset-d");
        stopped.Status = JobStatus.Interrupted;
        stopped.ResumeStatus = JobStatus.Finalizing;
        var queued = ImmichJob("clip-e.mp4", "asset-e");
        queued.Status = JobStatus.Queued;
        _jobs.AddRange([live, trashed, gone, stopped, queued]);
        _immich.Assets["asset-a"] = Asset("asset-a", trashed: false);
        _immich.Assets["asset-b"] = Asset("asset-b", trashed: true);
        _immich.Assets["asset-d"] = Asset("asset-d", trashed: false);

        var (code, stdout, stderr) = await RunAsync(null, "immich", "finalize");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            $"{live.Id:N}  clip-a.mp4  taken 2025-05-01 14:30  1.0 GiB -> 400.0 MiB, 60.0% saved\n" +
            $"{trashed.Id:N}  clip-b.mp4  taken 2025-05-01 14:30, in Immich's trash  sizes unknown\n" +
            $"{gone.Id:N}  clip-c.mp4  not readable in Immich  sizes unknown\n" +
            "    Resumes after: The upload finished.\n" +
            $"{stopped.Id:N}  clip-d.mp4  taken 2025-05-01 14:30  sizes unknown\n" +
            "4 videos: 1.0 GiB -> 400.0 MiB, saving 624.0 MiB.\n" +
            "Each AV1 is uploaded as a new asset with the original's albums, tags, favourite and other relations; then the original goes to Immich's trash.\n" +
            "Finalise these jobs? [y/N] Finalisation cancelled.\n");
        _finalizer.Calls.Should().BeEmpty();
        _immich.Trashed.Should().BeEmpty();
        _immich.Calls.Should().NotContain("get asset-e");
    }

    /// <summary><c>immich finalize</c> names only the Immich videos it left out; a warned file video is not its to mention.</summary>
    [Fact]
    public async Task ImmichFinalizeSkipsAWarnedImmichJobAndNamesIt()
    {
        var clean = ImmichJob("clip-a.mp4", "asset-a");
        var warned = Warned(ImmichJob("clip-b.mp4", "asset-b"));
        _jobs.AddRange([clean, warned, Warned(FileJob("clip-c.mp4", null, null, null))]);
        _immich.Assets["asset-a"] = Asset("asset-a", trashed: false);
        _finalizer.Results[clean.Id] = new(clean, ExitCode.Success, "New asset n1; the original is in Immich's trash.");
        _immich.Trash = new ImmichTrashSettings(true, 30);

        var (code, stdout, stderr) = await RunAsync("y\n", "immich", "finalize");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            $"{clean.Id:N}  clip-a.mp4  taken 2025-05-01 14:30  sizes unknown\n" +
            "1 video: 0 B -> 0 B, saving 0 B.\n" +
            "Each AV1 is uploaded as a new asset with the original's albums, tags, favourite and other relations; then the original goes to Immich's trash.\n" +
            "Left out because validation warned about them. Watch each one, then finalise it on its own:\n" +
            $"  clip-b.mp4  {FinalizeHint(warned)}\n" +
            "Finalise these jobs? [y/N] Done    clip-a.mp4: New asset n1; the original is in Immich's trash.\n" +
            "Finalised: 1.\n" +
            "Immich keeps the originals in its trash for 30 days; space comes back when it empties.\n" +
            "Left out with validation warnings:\n" +
            $"  clip-b.mp4  {FinalizeHint(warned)}\n");
        _finalizer.Calls.Should().Equal(clean.Id);
        _immich.Calls.Should().NotContain("get asset-b");
    }

    [Fact]
    public async Task ImmichFinalizeRefusesBeforeThePromptWhileImmichsTrashIsOff()
    {
        _jobs.Add(ImmichJob("clip-a.mp4", "asset-a"));
        _immich.Trash = new ImmichTrashSettings(false, 30);

        var (code, stdout, stderr) = await RunAsync("y\n", "immich", "finalize");

        code.Should().Be((int)ExitCode.InvalidConfiguration);
        stdout.Should().BeEmpty();
        stderr.ReplaceLineEndings("\n").Should().Be(ImmichTrash.DisabledMessage + "\n");
        _finalizer.Calls.Should().BeEmpty();
        _immich.Trashed.Should().BeEmpty();
    }

    [Fact]
    public async Task ImmichFinalizeWhereEveryJobFailsEndsInFinalisationFailureWithoutTheTrashRetentionLine()
    {
        var first = ImmichJob("clip-a.mp4", "asset-a");
        var second = ImmichJob("clip-b.mp4", "asset-b");
        _jobs.AddRange([first, second]);
        _finalizer.Results[first.Id] = new(first, ExitCode.FinalisationFailure, "Original changed in Immich.");
        _finalizer.Results[second.Id] = new(second, ExitCode.FinalisationFailure, "Original changed in Immich.");

        var (code, stdout, stderr) = await RunAsync("y\n", "immich", "finalize");

        code.Should().Be((int)ExitCode.FinalisationFailure);
        stderr.ReplaceLineEndings("\n").Should().Be(
            "Failed  clip-a.mp4: Original changed in Immich.\n" +
            "Failed  clip-b.mp4: Original changed in Immich.\n");
        stdout.ReplaceLineEndings("\n").Should().EndWith(
            "Finalise these jobs? [y/N] Failed: 2.\n");
        stdout.Should().NotContain("trash for");
    }

    [Fact]
    public async Task ImmichFinalizeWithAPartialFailureEndsInPartialSuccessWithoutTheTrashRetentionLine()
    {
        var done = ImmichJob("clip-a.mp4", "asset-a");
        var broken = ImmichJob("clip-b.mp4", "asset-b");
        _jobs.AddRange([done, broken]);
        _finalizer.Results[done.Id] = new(done, ExitCode.Success, "New asset n1; the original is in Immich's trash.");
        _finalizer.Results[broken.Id] = new(broken, ExitCode.FinalisationFailure, "Original changed in Immich.");

        var (code, stdout, _) = await RunAsync("y\n", "immich", "finalize");

        code.Should().Be((int)ExitCode.PartialSuccess);
        stdout.Should().NotContain("trash for");
    }

    [Fact]
    public async Task ImmichFinalizeAfterFullSuccessSaysHowLongImmichKeepsTheOriginals()
    {
        var job = ImmichJob("clip-a.mp4", "asset-a");
        _jobs.Add(job);
        _finalizer.Results[job.Id] = new(job, ExitCode.Success, "New asset n1; the original is in Immich's trash.");
        _immich.Trash = new ImmichTrashSettings(true, 30);

        var (code, stdout, stderr) = await RunAsync("y\n", "immich", "finalize");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().EndWith(
            "Finalise these jobs? [y/N] Done    clip-a.mp4: New asset n1; the original is in Immich's trash.\n" +
            "Finalised: 1.\n" +
            "Immich keeps the originals in its trash for 30 days; space comes back when it empties.\n");
    }

    [Fact]
    public async Task ImmichFinalizeLeavesOutTheRetentionLineWhenImmichStopsAnswering()
    {
        var job = ImmichJob("clip-a.mp4", "asset-a");
        _jobs.Add(job);
        _finalizer.Results[job.Id] = new(job, ExitCode.Success, "New asset n1; the original is in Immich's trash.");
        // The trash check before the prompt is the first read; the retention line asks again after the work is done.
        _immich.FailTrashSettingsAfter = 1;

        var (code, stdout, stderr) = await RunAsync("y\n", "immich", "finalize");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().EndWith("Finalised: 1.\n");
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string? input, string command, string? argument = null, string extraConfig = "", bool configureImmich = true)
    {
        var config = Path.Combine(_directory, "config.yaml");
        File.WriteAllText(config, $"database:\n  path: '{DatabasePath}'\n{(configureImmich ? "immich:\n  baseUrl: 'http://immich.test:2283'\n" : string.Empty)}{extraConfig}");
        string[] args = argument is null ? [command, "--config", config] : [command, argument, "--config", config];
        return ConsoleCapture.RunCliWithInputAsync(input, Configure, args);
    }

    private void Configure(IServiceCollection services)
    {
        services.AddSingleton<IFinalizationService>(_finalizer);
        services.AddSingleton<IImmichClientFactory>(_immich);
        services.AddSingleton<IJobRepository>(new FixedList(new SqliteJobRepository(new SqliteDatabaseInitializer()), _jobs));
    }

    private JobRecord FileJob(string name, long? before, long? after, decimal? saved) => new()
    {
        Id = Guid.NewGuid(),
        SourcePath = Path.Combine(_directory, name),
        Status = JobStatus.ReadyToFinalize,
        OutputPath = Path.Combine(_directory, name + ".av1.mp4"),
        SourceSizeBytes = before,
        OutputSizeBytes = after,
        PercentageSaved = saved
    };

    private JobRecord ImmichJob(string name, string assetId, long? before = null, long? after = null, decimal? saved = null)
    {
        var job = FileJob(name, before, after, saved);
        job.SourceKind = JobSourceKind.Immich;
        job.ImmichAssetId = assetId;
        return job;
    }

    private static JobRecord Warned(JobRecord job)
    {
        job.ValidationWarnings = ["The AV1 is 2.0 s shorter than the original.", "The AV1 has no audio."];
        return job;
    }

    private static JobRecord Noted(JobRecord job)
    {
        job.Notes = ["Audio track 2 (apac) left out: FFmpeg can't copy it. The other audio tracks are kept."];
        return job;
    }

    private string FinalizeHint(JobRecord job) => $"video-optimiser finalize {job.Id:N} --config \"{Path.Combine(_directory, "config.yaml")}\"";

    private static ImmichAsset Asset(string id, bool trashed) => new(
        id, "checksum", "clip.mp4", ImmichValues.Video, ImmichValues.Timeline, false, trashed, null, null,
        new DateTimeOffset(2025, 5, 1, 14, 30, 0, TimeSpan.Zero), new DateTimeOffset(2025, 5, 1, 14, 30, 0, TimeSpan.Zero), new DateTimeOffset(2025, 5, 1, 14, 30, 0, TimeSpan.Zero),
        null, new ImmichExif(null, null, null, null, null, null, null, null), []);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// The real repository with the list fixed, so the order the command prints in does not depend on timestamps. It splits the
    /// list on <c>terminal</c> as the real one does, so a finished job stays out of the commands' list for the right reason.
    /// </summary>
    private sealed class FixedList(IJobRepository inner, List<JobRecord> jobs) : DelegatingJobRepository(inner)
    {
        private static readonly JobStatus[] TerminalStatuses = [JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Skipped];

        public override Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JobRecord>>(jobs.Where(job => TerminalStatuses.Contains(job.Status) == terminal).ToArray());

        /// <summary><c>finalize &lt;id&gt;</c> reads the job to see whether it has warnings; an ID not in the list is unknown.</summary>
        public override Task<JobRecord?> GetAsync(string databasePath, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(jobs.FirstOrDefault(job => job.Id == id));
    }

    private sealed class FakeFinalizer : IFinalizationService
    {
        public Dictionary<Guid, FinalizationResult> Results { get; } = [];
        public List<Guid> Calls { get; } = [];

        public Task<FinalizationResult> FinalizeAsync(string databasePath, Guid jobId, AppSettings settings, CancellationToken cancellationToken = default)
        {
            Calls.Add(jobId);
            return Task.FromResult(Results[jobId]);
        }
    }

    /// <summary>Immich as far as these commands reach it: the trash setting, one asset read per listed job, and the trash call, which they never make themselves.</summary>
    private sealed class FakeImmich : ImmichClientStub
    {
        private int _trashReads;

        public Dictionary<string, ImmichAsset> Assets { get; } = [];
        public List<string> Calls { get; } = [];
        public List<string> Trashed { get; } = [];

        /// <summary>Reads of the trash setting that answer before the next one fails as an unreachable Immich would.</summary>
        public int? FailTrashSettingsAfter { get; set; }

        public override Task<ImmichTrashSettings> GetTrashSettingsAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("trash-settings");
            if (FailTrashSettingsAfter is { } allowed && _trashReads++ >= allowed) throw new ImmichApiException("ImmichRequestFailed", "Immich did not answer.");
            return base.GetTrashSettingsAsync(cancellationToken);
        }

        public override Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"get {assetId}");
            return Assets.TryGetValue(assetId, out var asset) ? Task.FromResult(asset) : throw new ImmichApiException("ImmichRequestFailed", "Not found.", 404);
        }

        public override Task TrashAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
        {
            Trashed.AddRange(assetIds);
            return Task.CompletedTask;
        }
    }
}
