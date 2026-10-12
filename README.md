# VideoOptimiser

Windows CLI for safely converting selected videos to AV1.

## Requirements

- Windows 11
- `ab-av1`, `ffmpeg`, and `ffprobe` on `PATH` or configured explicitly

For source builds, install the .NET 10 SDK and run `scripts\build.ps1`. It creates a self-contained Native AOT executable at `artifacts\local\video-optimiser.exe`. The portable release is one self-contained EXE and still requires the three media tools above.

## Quick start

Create a config:

```powershell
.\video-optimiser.exe config init --config .\video-optimiser.yaml
```

Edit `watch.roots`. The generated eligibility rules and safe-delete policy are ready to use. Check the setup:

```powershell
.\video-optimiser.exe doctor --config .\video-optimiser.yaml
```

Then the normal workflow is:

1. Find and queue eligible videos.
2. Process the queue. Each video goes through CRF search, temporary encoding, and validation. Originals are untouched.
3. Review ready jobs, then explicitly finalise them.

```powershell
# Queue every eligible video. Add --first to queue only one.
.\video-optimiser.exe queue discover --config .\video-optimiser.yaml

# Process all queued videos. This stops before replacing originals.
.\video-optimiser.exe queue run --config .\video-optimiser.yaml

# See what is ready, then replace every ready original after one confirmation.
.\video-optimiser.exe status --config .\video-optimiser.yaml
.\video-optimiser.exe finalize --ready --config .\video-optimiser.yaml
```

`queue run` can be stopped with `Ctrl+C`. The job becomes `Interrupted`; a later `queue run` resumes it safely. It never finalises an original automatically.

## Remote SSH processing

CRF search and encoding can run on Ubuntu while validation and finalisation stay local. The recommended [remote SSH workflow](docs/remote-ssh.md) creates, bootstraps, reuses, and safely deletes a Hetzner CX43 and its Primary IPv4 automatically. A manual SSH lifecycle remains available.

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

## Original actions

`original.action` sets what `finalize` does with each original:

| Action | Original | AV1 |
| --- | --- | --- |
| `delete` (default) | Deleted | Takes the original's path |
| `archive` | Moved to `original.archiveDirectory` | Takes the original's path |
| `keep` | Left in place | Installed beside it as `<name>.av1<extension>` |

```yaml
original:
  action: "archive"
  archiveDirectory: "Archive"
```

A folder name such as `Archive` is created beside each original, so archiving is a rename on the same drive. An absolute path collects every original in one folder; on another drive, `finalize` copies each original there, which takes longer. A name that is already taken gets a number, as in `movie (2).mp4`. `queue discover` never scans the archive directory, and skips an original that `keep` left in place until that file changes.

## Command reference

```powershell
# Queue all eligible files, or only the first one.
.\video-optimiser.exe queue discover --config .\video-optimiser.yaml
.\video-optimiser.exe queue discover --first --config .\video-optimiser.yaml

# Process queued jobs.
.\video-optimiser.exe queue run --config .\video-optimiser.yaml

# Process one file directly. --force ignores eligibility rules.
.\video-optimiser.exe process "C:\Videos\movie.mp4" --config .\video-optimiser.yaml

# List active jobs, or cancel jobs. --all cancels queued and interrupted jobs;
# a job ID also cancels a job that is ready to finalise.
.\video-optimiser.exe queue list --config .\video-optimiser.yaml
.\video-optimiser.exe queue cancel <job-id> --config .\video-optimiser.yaml
.\video-optimiser.exe queue cancel --all --config .\video-optimiser.yaml

# Queue a failed or cancelled job's original again. The old job remains in history;
# the new job uses a fresh workspace and state.
.\video-optimiser.exe queue retry <job-id> --config .\video-optimiser.yaml

# Revalidate or finalise one job.
.\video-optimiser.exe validate <job-id> --config .\video-optimiser.yaml
.\video-optimiser.exe finalize <job-id> --config .\video-optimiser.yaml

# Finalise every validated job after one confirmation.
.\video-optimiser.exe finalize --ready --config .\video-optimiser.yaml

# List temporary outputs no job will use again, then delete them after one confirmation.
.\video-optimiser.exe cleanup --dry-run --config .\video-optimiser.yaml
.\video-optimiser.exe cleanup --config .\video-optimiser.yaml

# Active jobs and terminal job history. Add --json for machine-readable output.
.\video-optimiser.exe status --config .\video-optimiser.yaml
.\video-optimiser.exe history --config .\video-optimiser.yaml

# Configuration and dependency checks.
.\video-optimiser.exe config show --config .\video-optimiser.yaml
.\video-optimiser.exe config validate --config .\video-optimiser.yaml
.\video-optimiser.exe doctor --config .\video-optimiser.yaml
.\video-optimiser.exe version
```

`process` writes temporary output and its manifest under:

```text
<original folder>\.video-optimiser\<file>.<job-id>.<attempt>.encoding.<extension>
```

`cleanup` deletes the files there that belong to a completed, failed, or cancelled job, to an earlier attempt, or to an interrupted local encode. It keeps the current output of every other job, everything after a failed or interrupted finalisation, and any file whose job is not in the configured database. Revalidate a failed job's output with `validate` before running `cleanup` if you want to keep it.

## Safety

- `queue discover` scans configured roots and queues eligible files. It skips an original whose last job failed or was cancelled until that file changes or `queue retry` queues it, so a file that fails is not encoded again on every run. It still requeues a job that failed only because the file was not ready, was ineligible, or was outside the watch roots when it ran.
- `process` creates a job, then CRF-searches, encodes, and validates a separate temporary AV1 file.
- `queue run` resumes interrupted CRF searches, encodes, or validations from a safe stage. A local encode cannot continue a partial file, so its resume deletes that file and starts a new attempt; `status` says which jobs this applies to.
- `validate` rechecks a job's temporary AV1 before replacement.
- `finalize` is explicit. With `delete` or `archive`, it moves the original aside, installs the AV1 in its place, and moves the original back if that fails; `delete` then deletes it. `keep` never moves the original.
- A never-started queued job adopts a changed processing configuration. A job that has started keeps its captured mode, remote identity, and workspace. If they differ, it fails before a managed worker is created.
- With a managed Hetzner worker, `deleteAfterRun: true` deletes the worker only after a clean run. Failed or interrupted jobs retain it. On an interactive console, failed runs offer one explicit deletion confirmation; redirected input never prompts.

## Source builds

Build and smoke-test the self-contained Windows executable:

```powershell
.\scripts\build.ps1
```

The output is `artifacts\local\video-optimiser.exe`. When following commands in this README from a source checkout, replace `.\video-optimiser.exe` with `.\artifacts\local\video-optimiser.exe`.

For a faster development run without publishing, use `dotnet run --project src/VideoOptimiser.Cli -- <arguments>`.

CI also runs an end-to-end test that encodes, validates and finalises generated clips with pinned ab-av1 and FFmpeg builds. To run it locally, put the pinned tools on `PATH` first:

```powershell
$env:PATH = ((.\scripts\install-media-tools.ps1 -Destination .\artifacts\media-tools) -join ';') + ";$env:PATH"
.\scripts\e2e-test.ps1 -Executable .\artifacts\local\video-optimiser.exe
```
