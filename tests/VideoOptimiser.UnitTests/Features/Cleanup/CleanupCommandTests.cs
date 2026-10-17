using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Cleanup;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests.Features.Cleanup;

/// <summary>What <c>cleanup</c> prints and returns: the plan, the prompt, and how a delete that fails ends.</summary>
public sealed class CleanupCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public CleanupCommandTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task NothingFoundNamesTheUnknownJobFilesThenSaysSo()
    {
        var cleanup = new FakeCleanup(new([], [], [Path.Combine(_directory, "unknown.tmp")], []));

        var (code, stdout, stderr) = await RunAsync(cleanup, null, "cleanup");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            $"Kept  {Path.Combine(_directory, "unknown.tmp")} (its job is not in this database)\n" +
            "No abandoned temporary outputs or staged Immich originals found.\n");
        cleanup.DeleteCalls.Should().Be(0);
        // The staging folder is searched whether or not Immich is turned on: a user who emptied immich.baseUrl still has copies in it.
        cleanup.Searched.Should().Be((Path.Combine(_directory, "jobs.db"), Path.Combine(_directory, "immich-staging")));
    }

    [Fact]
    public async Task ADryRunThatFindsNothingStillSaysNothingWasDeleted()
    {
        var (code, stdout, stderr) = await RunAsync(new FakeCleanup(new([], [], [], [])), null, "cleanup", "--dry-run");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            "No abandoned temporary outputs or staged Immich originals found.\n" +
            "Dry run: nothing was deleted.\n");
    }

    [Fact]
    public async Task ADryRunListsTheFilesAndTheStagedOriginalNoteWithoutAskingOrDeleting()
    {
        // Two abandoned files share a folder, which is printed once; the total names both kinds found.
        var cleanup = new FakeCleanup(Plan());

        var (code, stdout, stderr) = await RunAsync(cleanup, null, "cleanup", "--dry-run");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            $"{Path.Combine(_directory, "out")}\n" +
            "     1.0 MiB  clip-a.mp4.tmp (job failed)\n" +
            "     3.0 MiB  clip-c.mp4.tmp (job cancelled)\n" +
            $"{Path.Combine(_directory, "staging")}\n" +
            "     2.0 MiB  clip-b.mp4 (job finished)\n" +
            "Abandoned temporary outputs: 2. Staged Immich originals: 1. Total: 6.0 MiB.\n" +
            "Staged Immich originals are local copies of Immich assets. After 'queue retry', 'immich run' downloads the copy again.\n" +
            "Dry run: nothing was deleted.\n");
        cleanup.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task AnsweringYDeletesAndReportsTheCount()
    {
        var cleanup = new FakeCleanup(Plan(), new(2, 3 * 1024 * 1024, []));

        // Capital Y too: the answer is not case-sensitive.
        var (code, stdout, stderr) = await RunAsync(cleanup, "Y\n", "cleanup");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().EndWith(
            "Staged Immich originals are local copies of Immich assets. After 'queue retry', 'immich run' downloads the copy again.\n" +
            "Delete these files? [y/N] Deleted 2 files, 3.0 MiB.\n");
        cleanup.DeleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task NoAnswerCancelsWithoutDeleting()
    {
        var cleanup = new FakeCleanup(Plan(staged: false));

        var (code, stdout, stderr) = await RunAsync(cleanup, null, "cleanup");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        // Only abandoned outputs: the total names just that kind, and the note about staged originals is left out.
        stdout.ReplaceLineEndings("\n").Should().Be(
            $"{Path.Combine(_directory, "out")}\n" +
            "     1.0 MiB  clip-a.mp4.tmp (job failed)\n" +
            "     3.0 MiB  clip-c.mp4.tmp (job cancelled)\n" +
            "Abandoned temporary outputs: 2. Total: 4.0 MiB.\n" +
            "Delete these files? [y/N] Cleanup cancelled.\n");
        cleanup.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task AFailedDeleteIsReportedOnStderrAndEndsInPartialSuccess()
    {
        var cleanup = new FakeCleanup(Plan(abandoned: false), new(1, 2 * 1024 * 1024, ["clip-b.mp4: access denied"]));

        var (code, stdout, stderr) = await RunAsync(cleanup, "y\n", "cleanup");

        code.Should().Be((int)ExitCode.PartialSuccess);
        stderr.ReplaceLineEndings("\n").Should().Be("Could not delete clip-b.mp4: access denied\n");
        // Only staged originals: the total names just that kind.
        stdout.ReplaceLineEndings("\n").Should().Be(
            $"{Path.Combine(_directory, "staging")}\n" +
            "     2.0 MiB  clip-b.mp4 (job finished)\n" +
            "Staged Immich originals: 1. Total: 2.0 MiB.\n" +
            "Staged Immich originals are local copies of Immich assets. After 'queue retry', 'immich run' downloads the copy again.\n" +
            "Delete these files? [y/N] Deleted 1 file, 2.0 MiB.\n");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Two abandoned outputs in one folder and one staged original in another, or only one of the kinds.</summary>
    private TemporaryOutputCleanupPlan Plan(bool abandoned = true, bool staged = true) => new(
        abandoned
            ? [new AbandonedTemporaryOutput(Path.Combine(_directory, "out", "clip-a.mp4.tmp"), Guid.NewGuid(), 1, 1024 * 1024, "job failed"),
               new AbandonedTemporaryOutput(Path.Combine(_directory, "out", "clip-c.mp4.tmp"), Guid.NewGuid(), 1, 3 * 1024 * 1024, "job cancelled")]
            : [],
        staged ? [new StagedImmichOriginal(Path.Combine(_directory, "staging", "clip-b.mp4"), Guid.NewGuid(), 2 * 1024 * 1024, "job finished")] : [],
        [],
        []);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(FakeCleanup cleanup, string? input, params string[] args)
    {
        var config = await RunCli.WriteConfigAsync(Path.Combine(_directory, "jobs.db"));
        return await ConsoleCapture.RunCliWithInputAsync(input, services =>
        {
            CliFakes.AcceptAnySettings(services);
            services.AddSingleton<ITemporaryOutputCleanupService>(cleanup);
        }, [.. args, "--config", config]);
    }

    private sealed class FakeCleanup(TemporaryOutputCleanupPlan plan, TemporaryOutputCleanupResult? result = null) : ITemporaryOutputCleanupService
    {
        public int DeleteCalls { get; private set; }
        public (string DatabasePath, string StagingDirectory)? Searched { get; private set; }

        public Task<TemporaryOutputCleanupPlan> FindAsync(string databasePath, ImmichSettings immich, CancellationToken cancellationToken = default)
        {
            Searched = (databasePath, immich.StagingDirectory);
            return Task.FromResult(plan);
        }

        public TemporaryOutputCleanupResult Delete(TemporaryOutputCleanupPlan plan)
        {
            DeleteCalls++;
            return result ?? new(0, 0, []);
        }
    }
}
