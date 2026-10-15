using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.UnitTests.Features.Queue;
using VideoOptimiser.Application.TemporaryOutputs;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.TemporaryOutputs;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.Finalize;

/// <summary>
/// The commands that <c>finalize</c> and <c>immich finalize</c> suggest, through the real CLI. The services word them, not the
/// CLI, so these pin that the <c>--config</c> the user typed reaches the application layer. Every run here types one.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class FinalizeCommandHintsTests : QueueTestBase
{
    private readonly LockedFolderImmich _immich = new();

    protected override void RegisterFakes(IServiceCollection services) => services.AddSingleton<IImmichClientFactory>(_immich);

    [Fact]
    public async Task AMissingOriginalNamesFinalizeAndDropWithTheTypedConfig()
    {
        var job = await ReadyFolderJobAsync();
        File.Delete(job.SourcePath);

        var result = await RunAsync("finalize", job.Id.ToString("N"));

        result.Should().Be((8, "", Transcript(
            $"Original does not exist: {job.SourcePath}. If you moved it, put it back and run 'video-optimiser finalize {job.Id:N}{ConfigArgument}' again. If you deleted it on purpose, the AV1 at {job.OutputPath} is now the only copy: move it somewhere safe before running 'video-optimiser queue drop {job.Id:N}{ConfigArgument}'.")));
    }

    [Fact]
    public async Task AMissingAv1NamesFinalizeDropAndRetryWithTheTypedConfig()
    {
        var job = await ReadyFolderJobAsync();
        File.Delete(job.OutputPath!);

        var result = await RunAsync("finalize", job.Id.ToString("N"));

        result.Should().Be((8, "", Transcript(
            $"The AV1 is missing: {job.OutputPath}. If you moved it, put it back and run 'video-optimiser finalize {job.Id:N}{ConfigArgument}' again. To convert the video again instead, run 'video-optimiser queue drop {job.Id:N}{ConfigArgument}', then 'video-optimiser queue retry {job.Id:N}{ConfigArgument}'.")));
    }

    [Fact]
    public async Task AMissingValidationRecordNamesDropAndRetryWithTheTypedConfig()
    {
        var job = await ReadyFolderJobAsync();

        var result = await RunAsync("finalize", job.Id.ToString("N"));

        result.Should().Be((8, "", Transcript(
            $"The AV1's validation record is missing or unreadable: {new OutputManifestStore().GetPath(job.OutputPath!)}, so the AV1 can't be checked. To convert the video again, run 'video-optimiser queue drop {job.Id:N}{ConfigArgument}', then 'video-optimiser queue retry {job.Id:N}{ConfigArgument}'.")));
    }

    /// <summary>
    /// The interrupted job is made ready, and the message stored with it is what <c>queue list</c> shows later, so it must
    /// carry the typed config too.
    /// </summary>
    [Fact]
    public async Task AnInterruptedImmichFinalisationIsStoredWithTheTypedResumeCommand()
    {
        await WriteConfigAsync(immichConfigured: true);
        var job = await ImmichJobAsync(JobStatus.Finalizing, withManifest: false);
        File.Delete(job.OutputPath!);

        var finalize = await RunAsync("finalize", job.Id.ToString("N"));
        var list = await RunAsync("queue", "list");

        finalize.ExitCode.Should().Be(8);
        list.Should().Be((0, Transcript(
            "Ready to finalise",
            $"  {job.Id:N}  IMG_0001.MOV (Immich); the last finalise stopped: Finalisation was interrupted. Run 'video-optimiser immich finalize{ConfigArgument}' again to resume."), ""));
    }

    [Fact]
    public async Task ImmichFinalizeSaysToRunItAgainAndToDropWithTheTypedConfig()
    {
        await WriteConfigAsync(immichConfigured: true);
        var job = await ImmichJobAsync(JobStatus.ReadyToFinalize, withManifest: true);

        var (code, stdout, stderr) = await ConsoleCapture.RunCliWithInputAsync("y\n", RegisterFakes, "immich", "finalize", "--config", Config);

        code.Should().Be(8);
        var again = $"video-optimiser immich finalize{ConfigArgument}";
        var drop = $"video-optimiser queue drop {job.Id:N}{ConfigArgument}";
        var message = $"Immich can't find the original, asset {job.ImmichAssetId}: it was deleted for good or moved to the Locked Folder. If it's in the Locked Folder, move it out and run '{again}' again. If it was deleted, the downloaded copy at {job.SourcePath} and its AV1 at {job.OutputPath} may be the only copies left: move them somewhere safe before running '{drop}'.";
        stdout.Should().Contain(message);
        stderr.Should().Contain(message);
    }

    /// <summary>A job at the end of encoding: AV1 and source on disk, and the validation record when asked for.</summary>
    private async Task<JobRecord> ReadyFolderJobAsync()
    {
        var source = await FileAsync(Root, "clip.mp4");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = JobStatus.ReadyToFinalize, Attempt = 1 };
        job.OutputPath = Path.Combine(Root, TemporaryOutputClassifier.FolderName, $"clip.{job.Id:N}.1.encoding.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);
        await File.WriteAllTextAsync(job.OutputPath, "av1");
        await Repository.CreateAsync(DatabasePath, job);
        return job;
    }

    private async Task<JobRecord> ImmichJobAsync(JobStatus status, bool withManifest)
    {
        var assetId = Guid.NewGuid().ToString("D");
        var source = Path.Combine(StagingDirectory, assetId, "IMG_0001.MOV");
        var job = new JobRecord { Id = Guid.NewGuid(), SourcePath = source, SourceFingerprint = "original", Status = status, SourceKind = JobSourceKind.Immich, ImmichAssetId = assetId, ImmichChecksum = "checksum", Attempt = 1 };
        job.OutputPath = Path.Combine(Path.GetDirectoryName(source)!, TemporaryOutputClassifier.FolderName, $"IMG_0001.{job.Id:N}.1.encoding.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(job.OutputPath, "av1");
        if (withManifest)
        {
            await new OutputManifestStore().SaveAsync(new OutputManifest
            {
                SourcePath = source,
                SourceFingerprint = await new FileFingerprintService().CreateAsync(source),
                OutputPath = job.OutputPath,
                Crf = 30,
                CreatedUtc = DateTimeOffset.UtcNow,
                Validation = new ValidationReport { Passed = true }
            });
        }

        await Repository.CreateAsync(DatabasePath, job);
        return job;
    }

    /// <summary>Immich as it answers when an asset is deleted or in the Locked Folder: HTTP 400 on the asset.</summary>
    private sealed class LockedFolderImmich : ImmichClientStub
    {
        public override Task<ImmichAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default) =>
            Task.FromException<ImmichAsset>(new ImmichApiException("ImmichRequestFailed", $"Immich returned HTTP 400 for GET /api/assets/{assetId}: Not found or no asset.read access.", 400));
    }
}
