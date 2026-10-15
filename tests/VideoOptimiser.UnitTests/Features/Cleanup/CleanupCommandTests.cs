using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Cleanup;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain;

namespace VideoOptimiser.UnitTests.Features.Cleanup;

/// <summary>What <c>cleanup</c> prints and returns: the plan, the prompt, and how a delete that fails ends.</summary>
[Collection(ConsoleCapture.Collection)]
public sealed class CleanupCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public CleanupCommandTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task NothingFoundNamesTheUnknownJobFilesThenSaysSo()
    {
        var cleanup = new FakeCleanup(new([], [], [Path.Combine(_directory, "unknown.tmp")]));

        var (code, stdout, stderr) = await RunAsync(cleanup, null, "cleanup");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            $"Skipped  {Path.Combine(_directory, "unknown.tmp")} (its job is not in this database)\n" +
            "No abandoned temporary outputs or staged Immich originals found.\n");
        cleanup.DeleteCalls.Should().Be(0);
        // Without Immich configured there is no staging folder to search.
        cleanup.Searched.Should().Be((Path.Combine(_directory, "jobs.db"), string.Empty));
    }

    [Fact]
    public async Task ADryRunListsTheFilesAndTheStagedOriginalNoteWithoutAskingOrDeleting()
    {
        var cleanup = new FakeCleanup(Plan());

        var (code, stdout, stderr) = await RunAsync(cleanup, null, "cleanup", "--dry-run");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().Be(
            $"{Path.Combine(_directory, "out")}\n" +
            "     1.0 MiB  clip-a.mp4.tmp (job failed)\n" +
            $"{Path.Combine(_directory, "staging")}\n" +
            "     2.0 MiB  clip-b.mp4 (job finished)\n" +
            "Abandoned temporary outputs: 1. Staged Immich originals: 1. Total: 3.0 MiB.\n" +
            "Staged Immich originals are local copies of Immich assets. After 'queue retry', 'immich run' downloads the copy again.\n");
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
        var cleanup = new FakeCleanup(Plan());

        var (code, stdout, stderr) = await RunAsync(cleanup, null, "cleanup");

        code.Should().Be((int)ExitCode.Success);
        stderr.Should().BeEmpty();
        stdout.ReplaceLineEndings("\n").Should().EndWith("Delete these files? [y/N] Cleanup cancelled.\n");
        cleanup.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task AFailedDeleteIsReportedOnStderrAndEndsInPartialSuccess()
    {
        var cleanup = new FakeCleanup(Plan(), new(1, 1024 * 1024, ["clip-b.mp4: access denied"]));

        var (code, stdout, stderr) = await RunAsync(cleanup, "y\n", "cleanup");

        code.Should().Be((int)ExitCode.PartialSuccess);
        stderr.ReplaceLineEndings("\n").Should().Be("Could not delete clip-b.mp4: access denied\n");
        stdout.ReplaceLineEndings("\n").Should().EndWith("Delete these files? [y/N] Deleted 1 file, 1.0 MiB.\n");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private TemporaryOutputCleanupPlan Plan() => new(
        [new AbandonedTemporaryOutput(Path.Combine(_directory, "out", "clip-a.mp4.tmp"), Guid.NewGuid(), 1, 1024 * 1024, "job failed")],
        [new StagedImmichOriginal(Path.Combine(_directory, "staging", "clip-b.mp4"), Guid.NewGuid(), 2 * 1024 * 1024, "job finished")],
        []);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(FakeCleanup cleanup, string? input, params string[] args)
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, $"database:\n  path: '{Path.Combine(_directory, "jobs.db")}'\n");
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

        public Task<TemporaryOutputCleanupPlan> FindAsync(string databasePath, string immichStagingDirectory, CancellationToken cancellationToken = default)
        {
            Searched = (databasePath, immichStagingDirectory);
            return Task.FromResult(plan);
        }

        public TemporaryOutputCleanupResult Delete(TemporaryOutputCleanupPlan plan)
        {
            DeleteCalls++;
            return result ?? new(0, 0, []);
        }
    }
}
