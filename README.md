# VideoOptimiser

Windows CLI for converting H.264 videos to AV1 safely.

Current status: scan files, find a CRF, encode a temporary AV1, validate it, then explicitly replace the original.

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
# Find eligible H.264 files. Read-only.
dotnet run --project src/VideoOptimiser.Cli -- scan --config .\video-optimiser.yaml

# Find a quality CRF for one file. No full output file is created.
dotnet run --project src/VideoOptimiser.Cli -- process "C:\Videos\movie.mp4" --config .\video-optimiser.yaml

# Encode using the selected CRF. Original remains untouched.
dotnet run --project src/VideoOptimiser.Cli -- encode "C:\Videos\movie.mp4" --crf 44 --config .\video-optimiser.yaml

# Check the temporary AV1 and its savings.
dotnet run --project src/VideoOptimiser.Cli -- validate "C:\Videos\.video-optimiser\movie.encoding.mp4" --config .\video-optimiser.yaml

# Explicitly replace the original only after validation passes.
dotnet run --project src/VideoOptimiser.Cli -- finalize "C:\Videos\.video-optimiser\movie.encoding.mp4" --config .\video-optimiser.yaml
```

`encode` writes its output under:

```text
<source folder>\.video-optimiser\<file>.encoding.<extension>
```

Use `Ctrl+C` to stop `process` or `encode`.

## Safety

- `scan` only reads files.
- `process` only creates short temporary samples to calculate CRF.
- `encode` creates a separate temporary AV1 file.
- `validate` checks the temporary AV1 before replacement.
- `finalize` is explicit: it renames the original to a rollback file, installs the AV1, then deletes the rollback file.
