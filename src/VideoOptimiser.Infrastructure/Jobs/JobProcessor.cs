using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Application.Scanning;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Jobs;

public sealed class JobProcessor(
    IJobRepository jobs,
    IFileStabilityService stability,
    Func<string, IMediaProbe> mediaProbeFactory,
    Func<string, ICrfSearchClient> crfSearchFactory,
    Func<string, IVideoEncoder> encoderFactory,
    IFileFingerprintService fingerprints,
    IOutputManifestStore manifests,
    IOutputValidationService validator) : IJobProcessor
{
    public async Task<JobProcessingResult> ProcessAsync(string databasePath, string sourcePath, AppSettings settings, bool force, IProgress<CrfSearchOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path)) return FailureWithoutJob(ExitCode.ProcessingFailure, $"Source file does not exist: {path}");

        var fingerprint = await fingerprints.CreateAsync(path, cancellationToken);
        var active = await jobs.FindActiveAsync(databasePath, path, fingerprint, cancellationToken);
        if (active is not null)
        {
            return new JobProcessingResult(active, ExitCode.Success, $"Job {active.Id:N} is already {active.Status}.");
        }

        var job = await jobs.CreateAsync(databasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = path, SourceFingerprint = fingerprint, Status = JobStatus.Queued }, cancellationToken);
        var stage = "ProcessingFailed";
        try
        {
            if (!force && !settings.Watch.Roots.Any(root => IsWithinRoot(path, root.Path))) return await FailAsync(job, databasePath, "SourceOutsideWatchRoot", "Source file is outside configured watch roots. Use --force to bypass this check.", ExitCode.ProcessingFailure, cancellationToken);
            var info = new FileInfo(path);
            if (!force && (!HumanReadableValues.TryParseSize(settings.Processing.MinimumFileSize, out var minimumSize) || info.Length < minimumSize)) return await FailAsync(job, databasePath, "BelowMinimumFileSize", "Source file is below processing.minimumFileSize. Use --force to bypass this check.", ExitCode.NoEligibleFiles, cancellationToken);
            var stable = await stability.WaitUntilStableAsync(path, settings.Watch.Stability, requireRepeatedObservations: false, cancellationToken);
            if (!stable.IsStable) return await FailAsync(job, databasePath, "SourceNotReady", $"Source is not ready: {stable.Reason}", ExitCode.ProcessingFailure, cancellationToken);

            var media = await mediaProbeFactory(settings.Tools.FfprobePath).ProbeAsync(path, cancellationToken);
            if (!force && !settings.Eligibility.RequiredVideoCodecs.Contains(media.PrimaryVideoCodec, StringComparer.OrdinalIgnoreCase)) return await FailAsync(job, databasePath, "UnsupportedCodec", $"Source codec is {media.PrimaryVideoCodec}; expected {string.Join(", ", settings.Eligibility.RequiredVideoCodecs)}. Use --force to bypass this check.", ExitCode.NoEligibleFiles, cancellationToken);

            var sampleCount = CrfSampleCountCalculator.Calculate(media.DurationSeconds, settings.Quality.CrfSearch.SampleCount);
            var quality = WithSampleCount(settings.Quality, sampleCount);
            stage = "CrfSearchFailed";
            job.Status = JobStatus.CrfSearching;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            var crfResult = await crfSearchFactory(settings.Tools.AbAv1Path).SearchAsync(path, quality, progress, cancellationToken);
            job.Crf = crfResult.Crf;

            stage = "EncodeFailed";
            job.Status = JobStatus.Encoding;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            var outputDirectory = Path.Combine(Path.GetDirectoryName(path)!, ".video-optimiser");
            var outputPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(path)}.{job.Id:N}.encoding{Path.GetExtension(path)}");
            Directory.CreateDirectory(outputDirectory);
            var encodeResult = await encoderFactory(settings.Tools.AbAv1Path).EncodeAsync(path, outputPath, job.Crf.Value, quality, progress, cancellationToken);
            var manifest = new OutputManifest { SourcePath = path, SourceFingerprint = fingerprint, OutputPath = encodeResult.OutputPath, Crf = job.Crf.Value, CreatedUtc = DateTimeOffset.UtcNow };
            await manifests.SaveAsync(manifest, cancellationToken);
            job.OutputPath = encodeResult.OutputPath;
            job.ManifestPath = manifests.GetPath(encodeResult.OutputPath);

            stage = "ValidationFailed";
            job.Status = JobStatus.Validating;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            manifest.Validation = await validator.ValidateAsync(manifest, settings, cancellationToken);
            await manifests.SaveAsync(manifest, cancellationToken);
            job.ValidationPassed = manifest.Validation.Passed;
            job.SourceSizeBytes = manifest.Validation.SourceSizeBytes;
            job.OutputSizeBytes = manifest.Validation.OutputSizeBytes;
            job.PercentageSaved = manifest.Validation.PercentageSaved;
            if (!manifest.Validation.Passed)
            {
                return await FailAsync(job, databasePath, "ValidationFailed", string.Join("; ", manifest.Validation.Failures), ExitCode.ValidationFailure, cancellationToken);
            }

            job.Status = JobStatus.ReadyToFinalize;
            await jobs.UpdateAsync(databasePath, job, cancellationToken);
            return new JobProcessingResult(job, ExitCode.Success, $"Job {job.Id:N} is ready to finalize.");
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Interrupted;
            job.FailureCategory = "Interrupted";
            job.FailureMessage = "Processing was cancelled.";
            job.CompletedUtc = DateTimeOffset.UtcNow;
            await jobs.UpdateAsync(databasePath, job, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            return await FailAsync(job, databasePath, stage, exception.Message, ExitCode.ProcessingFailure, cancellationToken);
        }
    }

    private static JobProcessingResult FailureWithoutJob(ExitCode exitCode, string message) => new(new JobRecord { Status = JobStatus.Failed }, exitCode, message);

    private async Task<JobProcessingResult> FailAsync(JobRecord job, string databasePath, string category, string message, ExitCode exitCode, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Failed;
        job.FailureCategory = category;
        job.FailureMessage = message;
        job.CompletedUtc = DateTimeOffset.UtcNow;
        await jobs.UpdateAsync(databasePath, job, cancellationToken);
        return new JobProcessingResult(job, exitCode, message);
    }

    private static bool IsWithinRoot(string sourcePath, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        return sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static QualitySettings WithSampleCount(QualitySettings source, int sampleCount) => new()
    {
        MinimumVmaf = source.MinimumVmaf,
        Preset = source.Preset,
        Encoder = source.Encoder,
        PixelFormat = source.PixelFormat,
        CrfSearch = new CrfSearchSettings { Enabled = source.CrfSearch.Enabled, MinCrf = source.CrfSearch.MinCrf, MaxCrf = source.CrfSearch.MaxCrf, SampleCount = sampleCount, SampleDuration = source.CrfSearch.SampleDuration }
    };

}
