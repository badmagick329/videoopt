using FluentAssertions;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.UnitTests.Features.Queue;

namespace VideoOptimiser.UnitTests.Features.Cleanup;

/// <summary>
/// <c>cleanup</c> run for real against a database and files on disk, so what it finds, keeps and prints is read from the
/// transcript. The Immich staging folder is the one <see cref="QueueTestBase"/> writes into the config.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class CleanupTranscriptTests : QueueTestBase
{
    /// <summary>A user who empties immich.baseUrl still has downloaded copies on disk, and cleanup must list them.</summary>
    [Fact]
    public async Task ADownloadedCopyIsFoundWhetherOrNotImmichIsTurnedOn()
    {
        await WriteConfigAsync(immichConfigured: false);
        var job = await StagedAsync(JobStatus.Completed);

        var result = await RunAsync("cleanup", "--dry-run");

        result.Should().Be((0, Transcript(
            Path.GetDirectoryName(job.SourcePath)!,
            "        20 B  IMG_0001.MOV (staged Immich original; job completed)",
            "Staged Immich originals: 1. Total: 20 B.",
            "Staged Immich originals are local copies of Immich assets. After 'queue retry', 'immich run' downloads the copy again.",
            "Dry run: nothing was deleted."), ""));
        File.Exists(job.SourcePath).Should().BeTrue();
    }

    private static string Transcript(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    /// <summary>An Immich job in <paramref name="status"/> whose downloaded copy is on disk.</summary>
    private async Task<JobRecord> StagedAsync(JobStatus status)
    {
        var assetId = Guid.NewGuid().ToString("D");
        var source = await FileAsync(Path.Combine(StagingDirectory, assetId), "IMG_0001.MOV");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = status, SourceKind = JobSourceKind.Immich, ImmichAssetId = assetId, ImmichChecksum = "checksum" };
        await Repository.CreateAsync(DatabasePath, job);
        return job;
    }
}
