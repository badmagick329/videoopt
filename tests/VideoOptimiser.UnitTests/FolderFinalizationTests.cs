using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Diagnostics;
using VideoOptimiser.Infrastructure.Immich;
using VideoOptimiser.Infrastructure.Jobs;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class FolderFinalizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task FinalizeStillCompletesAndShowsTheWarningWhenTheFileDatesCannotBeCopied()
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".video-optimiser"));
        var source = Path.Combine(_directory, "movie.mp4");
        var output = Path.Combine(_directory, ".video-optimiser", "movie.encoding.mp4");
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(output, "av1");
        var repository = new SqliteJobRepository(new SqliteDatabaseInitializer());
        var manifests = new OutputManifestStore();
        var fingerprints = new FileFingerprintService();
        var fingerprint = await fingerprints.CreateAsync(source);
        await manifests.SaveAsync(new OutputManifest
        {
            SourcePath = source,
            SourceFingerprint = fingerprint,
            OutputPath = output,
            Crf = 30,
            CreatedUtc = DateTimeOffset.UtcNow,
            Validation = new ValidationReport { Passed = true }
        });
        var settings = new AppSettings { Database = new DatabaseSettings { Path = Path.Combine(_directory, "jobs.db") } };
        var job = await repository.CreateAsync(settings.Database.Path, new JobRecord
        {
            Id = Guid.NewGuid(),
            SourcePath = source,
            SourceFingerprint = fingerprint,
            Status = JobStatus.ReadyToFinalize,
            Attempt = 1,
            OutputPath = output
        });
        var installer = new SafeFileInstaller((_, _, _) => throw new IOException("locked"));
        // A folder job never reaches the Immich finalizer, so it gets no client.
        var finalizer = new FinalizationService(repository, manifests, fingerprints, installer, new ImmichFinalizer(null!, repository, manifests));

        var result = await finalizer.FinalizeAsync(settings.Database.Path, job.Id, settings);

        result.ExitCode.Should().Be(ExitCode.Success, result.Message);
        result.Message.Should().StartWith("Finalisation complete. Original deleted. Warning: ").And.Contain("file dates").And.Contain("locked");
        (await repository.GetAsync(settings.Database.Path, job.Id))!.Status.Should().Be(JobStatus.Completed);
        (await File.ReadAllTextAsync(source)).Should().Be("av1");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
