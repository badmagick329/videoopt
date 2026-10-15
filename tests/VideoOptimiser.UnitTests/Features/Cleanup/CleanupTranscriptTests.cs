using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.TemporaryOutputs;
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
    private const string StagedNote = "Staged Immich originals are local copies of Immich assets. After 'queue retry', 'immich run' downloads the copy again.";

    private readonly FakeImmich _immich = new();

    protected override void RegisterFakes(IServiceCollection services) => services.AddSingleton<IImmichClientFactory>(_immich);

    /// <summary>A user who empties immich.baseUrl still has downloaded copies on disk, and cleanup must list them.</summary>
    [Fact]
    public async Task ADownloadedCopyIsFoundWhetherOrNotImmichIsTurnedOn()
    {
        await WriteConfigAsync(immichConfigured: false);
        var job = await StagedAsync(JobStatus.Completed);

        var result = await RunAsync("cleanup", "--dry-run");

        // A completed job's copy is a leftover of finalisation, not a possible only copy, so Immich is not asked.
        result.Should().Be((0, Transcript(
            Path.GetDirectoryName(job.SourcePath)!,
            Row("20 B", "IMG_0001.MOV", "staged Immich original; job completed"),
            "Staged Immich originals: 1. Total: 20 B.",
            StagedNote,
            "Dry run: nothing was deleted."), ""));
        _immich.Calls.Should().Be(0);
        File.Exists(job.SourcePath).Should().BeTrue();
    }

    /// <summary>VOPT-104: the AV1 of a failed job is the only copy once its original is gone.</summary>
    [Fact]
    public async Task AFolderVideoWhoseOriginalIsMissingKeepsItsAv1()
    {
        var job = await QueuedAsync("clip.mp4");
        await WriteAv1Async(job);
        await MoveAsync(job, JobStatus.Failed);
        File.Delete(job.SourcePath);

        var result = await RunAsync("cleanup", "--dry-run");

        result.Should().Be((0, Transcript(
            $"Kept  {job.OutputPath} (the original is missing, so this AV1 may be the only copy)",
            Path.GetDirectoryName(job.OutputPath!)!,
            Row("2 B", Path.GetFileName(job.OutputPath!) + ".manifest.json", "job failed"),
            "Abandoned temporary outputs: 1. Total: 2 B.",
            "Dry run: nothing was deleted."), ""));
    }

    [Fact]
    public async Task AFolderVideoWhoseOriginalIsThereHasItsAv1Listed()
    {
        var job = await QueuedAsync("clip.mp4");
        await WriteAv1Async(job);
        await MoveAsync(job, JobStatus.Failed);

        var result = await RunAsync("cleanup", "--dry-run");

        result.Should().Be((0, Transcript(
            Path.GetDirectoryName(job.OutputPath!)!,
            Row("3 B", Path.GetFileName(job.OutputPath!), "job failed"),
            Row("2 B", Path.GetFileName(job.OutputPath!) + ".manifest.json", "job failed"),
            "Abandoned temporary outputs: 2. Total: 5 B.",
            "Dry run: nothing was deleted."), ""));
    }

    /// <summary>A completed folder job's leftover is a temporary file even with the original gone: finalise installed the AV1.</summary>
    [Fact]
    public async Task ACompletedFolderVideoLeftoverIsListedEvenWithoutItsOriginal()
    {
        var job = await QueuedAsync("clip.mp4");
        await WriteAv1Async(job);
        await MoveAsync(job, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing, JobStatus.Completed);
        File.Delete(job.SourcePath);

        var result = await RunAsync("cleanup", "--dry-run");

        result.Stdout.Should().Contain("(job completed)").And.NotContain("Kept");
    }

    /// <summary>VOPT-104: Immich says the original is gone (deleted, trashed or replaced), so the AV1 and the download may be the only copies. Immich is asked once for the job, not once per file.</summary>
    [Fact]
    public async Task AnImmichVideoWhoseOriginalIsGoneKeepsBothFiles()
    {
        await WriteConfigAsync(immichConfigured: true);
        var job = await StagedAsync(JobStatus.Failed);
        await WriteAv1Async(job);
        _immich.Asset = null;

        var result = await RunAsync("cleanup", "--dry-run");

        const string reason = "the original is gone from Immich, so this may be the only copy";
        result.Should().Be((0, Transcript(
            $"Kept  {job.OutputPath} ({reason})",
            $"Kept  {job.SourcePath} ({reason})",
            Path.GetDirectoryName(job.OutputPath!)!,
            Row("2 B", Path.GetFileName(job.OutputPath!) + ".manifest.json", "job failed"),
            "Abandoned temporary outputs: 1. Total: 2 B.",
            "Dry run: nothing was deleted."), ""));
        _immich.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AnImmichVideoWhoseOriginalIsStillThereIsListedAfterOneQuestionToImmich()
    {
        await WriteConfigAsync(immichConfigured: true);
        var job = await StagedAsync(JobStatus.Failed);
        await WriteAv1Async(job);
        _immich.Asset = job;

        var result = await RunAsync("cleanup", "--dry-run");

        result.Stdout.Should().NotContain("Kept");
        result.Stdout.Should().Contain("IMG_0001.MOV (staged Immich original; job failed)").And.Contain(Path.GetFileName(job.OutputPath!) + " (job failed)");
        _immich.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AnImmichVideoIsKeptWhenImmichIsNotConfigured()
    {
        await WriteConfigAsync(immichConfigured: false);
        var job = await StagedAsync(JobStatus.Failed);
        await WriteAv1Async(job);

        var result = await RunAsync("cleanup", "--dry-run");

        const string reason = "Immich can't be checked: immich.baseUrl isn't set; this may be the only copy";
        result.Should().Be((0, Transcript(
            $"Kept  {job.OutputPath} ({reason})",
            $"Kept  {job.SourcePath} ({reason})",
            Path.GetDirectoryName(job.OutputPath!)!,
            Row("2 B", Path.GetFileName(job.OutputPath!) + ".manifest.json", "job failed"),
            "Abandoned temporary outputs: 1. Total: 2 B.",
            "Dry run: nothing was deleted."), ""));
        _immich.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AnImmichVideoIsKeptWhenImmichCannotBeReached()
    {
        await WriteConfigAsync(immichConfigured: true);
        var job = await StagedAsync(JobStatus.Cancelled);
        _immich.Failure = new ImmichApiException("ImmichUnavailable", "Immich is unreachable at http://immich.test: refused.");

        var result = await RunAsync("cleanup", "--dry-run");

        result.Should().Be((0, Transcript(
            $"Kept  {job.SourcePath} (Immich can't be checked: Immich is unreachable at http://immich.test: refused; this may be the only copy)",
            "No abandoned temporary outputs or staged Immich originals found.",
            "Dry run: nothing was deleted."), ""));
    }

    /// <summary>The same rule decides a real run: a file kept in the dry run is still there after the user answers y.</summary>
    [Fact]
    public async Task AKeptAv1SurvivesARealRun()
    {
        await WriteConfigAsync();
        var job = await QueuedAsync("clip.mp4");
        await WriteAv1Async(job);
        await MoveAsync(job, JobStatus.Failed);
        File.Delete(job.SourcePath);

        var result = await ConsoleCapture.RunCliWithInputAsync("y\n", RegisterFakes, "cleanup", "--config", Config);

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("Deleted 1 file");
        File.Exists(job.OutputPath).Should().BeTrue();
        File.Exists(job.OutputPath + ".manifest.json").Should().BeFalse();
    }

    private static string Transcript(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private static string Row(string size, string name, string reason) => $"  {size,10}  {name} ({reason})";

    /// <summary>An Immich job in <paramref name="status"/> whose downloaded copy is on disk.</summary>
    private async Task<JobRecord> StagedAsync(JobStatus status)
    {
        var assetId = Guid.NewGuid().ToString("D");
        var source = await FileAsync(Path.Combine(StagingDirectory, assetId), "IMG_0001.MOV");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = status, SourceKind = JobSourceKind.Immich, ImmichAssetId = assetId, ImmichChecksum = "checksum" };
        await Repository.CreateAsync(DatabasePath, job);
        return job;
    }

    /// <summary>The AV1 of attempt 1 and its manifest, where the processor writes them, recorded on the job.</summary>
    private async Task WriteAv1Async(JobRecord job)
    {
        var folder = Path.Combine(Path.GetDirectoryName(job.SourcePath)!, TemporaryOutputClassifier.FolderName);
        Directory.CreateDirectory(folder);
        job.Attempt = 1;
        job.OutputPath = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(job.SourcePath)}.{job.Id:N}.1.encoding.mp4");
        await File.WriteAllTextAsync(job.OutputPath, "av1");
        await File.WriteAllTextAsync(job.OutputPath + ".manifest.json", "{}");
        await Repository.UpdateAsync(DatabasePath, job);
    }

    /// <summary>Answers the one asset lookup the only-copy check makes, and counts it: <see cref="Asset"/>'s job, or 400 (deleted) when none, or <see cref="Failure"/>.</summary>
    private sealed class FakeImmich : ImmichClientStub
    {
        public JobRecord? Asset { get; set; }
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }

        public override Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Failure is not null) return Task.FromException<ImmichAsset>(Failure);
            if (Asset is null) return Task.FromException<ImmichAsset>(new ImmichApiException("ImmichRequestFailed", "Immich returned HTTP 400 for GET /api/assets.", 400));
            return Task.FromResult(new ImmichAsset(Asset.ImmichAssetId!, Asset.ImmichChecksum!, "IMG_0001.MOV", ImmichValues.Video, ImmichValues.Timeline, false, false, null, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, new ImmichExif(null, null, null, null, null, null, null, null), []));
        }
    }
}
