# VideoOptimiser

Windows CLI for converting H.264 videos to AV1 safely.

Current status: process one source through CRF search, temporary AV1 encoding, and validation; then explicitly replace it.

## Requirements

- Windows 11
- .NET SDK 9
- `ab-av1`, `ffmpeg`, and `ffprobe` on `PATH` or configured explicitly

## Setup

```powershell
dotnet run --project src/VideoOptimiser.Cli -- config init --config .\video-optimiser.yaml
```

Edit `watch.roots` to your video folder and set `original.action: "delete"`. Then check setup:

```powershell
dotnet run --project src/VideoOptimiser.Cli -- doctor --config .\video-optimiser.yaml
```

## Commands

Always run commands through `dotnet run` for now; `video-optimiser` is not installed as a system command.

```powershell
# Find the next eligible H.264 file, then stop. Read-only.
dotnet run --project src/VideoOptimiser.Cli -- scan --first --config .\video-optimiser.yaml

# List every eligible file. Add --all for diagnostic output.
dotnet run --project src/VideoOptimiser.Cli -- scan --config .\video-optimiser.yaml

# Process one file through CRF search, temporary encoding, and validation.
# It stops before replacing the original and prints a job ID.
dotnet run --project src/VideoOptimiser.Cli -- process "C:\Videos\movie.mp4" --config .\video-optimiser.yaml

# List active jobs. Copy the full job ID for validate/finalize.
dotnet run --project src/VideoOptimiser.Cli -- status --config .\video-optimiser.yaml

# Re-run validation for a job if needed.
dotnet run --project src/VideoOptimiser.Cli -- validate <job-id> --config .\video-optimiser.yaml

# Explicitly replace the original only after validation passes.
dotnet run --project src/VideoOptimiser.Cli -- finalize <job-id> --config .\video-optimiser.yaml

# Show completed, failed, and interrupted jobs.
dotnet run --project src/VideoOptimiser.Cli -- history --config .\video-optimiser.yaml
```

`process` writes temporary output and its manifest under:

```text
<source folder>\.video-optimiser\<file>.<job-id>.encoding.<extension>
```

`status --json` and `history --json` emit machine-readable JSON. Use `Ctrl+C` to stop `process`; the job is recorded as `Interrupted` and both source and temporary files are retained.

## Safety

- `scan` only reads files.
- `process` creates a job, then CRF-searches, encodes, and validates a separate temporary AV1 file.
- `validate` rechecks a job's temporary AV1 before replacement.
- `finalize` is explicit: it renames the original to a rollback file, installs the AV1, then deletes the rollback file.
- `finalize` currently requires `original.action: "delete"`.
