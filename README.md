# VideoOptimiser

Windows CLI for safely converting selected videos to AV1.

## Requirements

- Windows 11
- `ab-av1`, `ffmpeg`, and `ffprobe` on `PATH` or configured explicitly

For source builds, install the .NET SDK 9. The portable release is one self-contained EXE and still requires the three media tools above.

## Quick start

Create a config:

```powershell
dotnet run --project src/VideoOptimiser.Cli -- config init --config .\video-optimiser.yaml
```

Edit `watch.roots`. The generated eligibility rules and safe-delete policy are ready to use. Check the setup:

```powershell
dotnet run --project src/VideoOptimiser.Cli -- doctor --config .\video-optimiser.yaml
```

Then the normal workflow is:

1. Find and queue eligible videos.
2. Process the queue. Each video goes through CRF search, temporary encoding, and validation. Originals are untouched.
3. Review ready jobs, then explicitly finalize them.

```powershell
# Queue every eligible video. Add --first to queue only one.
dotnet run --project src/VideoOptimiser.Cli -- queue discover --config .\video-optimiser.yaml

# Process all queued videos. This stops before replacing originals.
dotnet run --project src/VideoOptimiser.Cli -- queue run --config .\video-optimiser.yaml

# See what is ready, then replace every ready original after one confirmation.
dotnet run --project src/VideoOptimiser.Cli -- status --config .\video-optimiser.yaml
dotnet run --project src/VideoOptimiser.Cli -- finalize --ready --config .\video-optimiser.yaml
```

`queue run` can be stopped with `Ctrl+C`. The job becomes `Interrupted`; a later `queue run` resumes it safely. It never finalizes an original automatically.

## Eligibility rules

A file queues if it matches any rule. Every condition within one rule must match.

```yaml
eligibility:
  rules:
    - codecs: ["h264"]
      resolution: "4k+"
      minimumVideoBitrate: "20Mbps"
      minimumFileSize: "2GiB"
    - codecs: ["h264"]
      resolution: "1080p-1440p"
      minimumVideoBitrate: "8Mbps"
      minimumFileSize: "800MiB"
```

Resolution bands use total pixels: `1080p-1440p`, `1440p-4k`, and `4k+`. Bitrate is the primary video-stream bitrate from `ffprobe`; files without it do not match.

## Command reference

For a source build, prefix commands with `dotnet run --project src/VideoOptimiser.Cli --`. For the portable release, run `video-optimiser.exe` directly.

```powershell
# Queue all eligible files, or only the first one.
dotnet run --project src/VideoOptimiser.Cli -- queue discover --config .\video-optimiser.yaml
dotnet run --project src/VideoOptimiser.Cli -- queue discover --first --config .\video-optimiser.yaml

# Process queued jobs.
dotnet run --project src/VideoOptimiser.Cli -- queue run --config .\video-optimiser.yaml

# Process one file directly. --force ignores eligibility rules.
dotnet run --project src/VideoOptimiser.Cli -- process "C:\Videos\movie.mp4" --config .\video-optimiser.yaml

# List active jobs, or cancel queued/interrupted jobs.
dotnet run --project src/VideoOptimiser.Cli -- queue list --config .\video-optimiser.yaml
dotnet run --project src/VideoOptimiser.Cli -- queue cancel <job-id> --config .\video-optimiser.yaml
dotnet run --project src/VideoOptimiser.Cli -- queue cancel --all --config .\video-optimiser.yaml

# Revalidate or finalize one job.
dotnet run --project src/VideoOptimiser.Cli -- validate <job-id> --config .\video-optimiser.yaml
dotnet run --project src/VideoOptimiser.Cli -- finalize <job-id> --config .\video-optimiser.yaml

# Finalize every validated job after one confirmation.
dotnet run --project src/VideoOptimiser.Cli -- finalize --ready --config .\video-optimiser.yaml

# Active jobs and terminal job history. Add --json for machine-readable output.
dotnet run --project src/VideoOptimiser.Cli -- status --config .\video-optimiser.yaml
dotnet run --project src/VideoOptimiser.Cli -- history --config .\video-optimiser.yaml

# Configuration and dependency checks.
dotnet run --project src/VideoOptimiser.Cli -- config show --config .\video-optimiser.yaml
dotnet run --project src/VideoOptimiser.Cli -- config validate --config .\video-optimiser.yaml
dotnet run --project src/VideoOptimiser.Cli -- doctor --config .\video-optimiser.yaml
dotnet run --project src/VideoOptimiser.Cli -- version
```

`process` writes temporary output and its manifest under:

```text
<source folder>\.video-optimiser\<file>.<job-id>.<attempt>.encoding.<extension>
```

## Safety

- `queue discover` scans configured roots and queues eligible files.
- `process` creates a job, then CRF-searches, encodes, and validates a separate temporary AV1 file.
- `queue run` resumes interrupted CRF searches, encodes, or validations from a safe stage.
- `validate` rechecks a job's temporary AV1 before replacement.
- `finalize` is explicit: it renames the original to a rollback file, installs the AV1, then deletes the rollback file.
- `finalize` currently requires `original.action: "delete"`.
