# VideoOptimiser

VideoOptimiser is a Windows-first command-line application for safely converting large H.264 videos to AV1. This repository currently implements **Phase 1: the operational foundation**. It does not watch folders, probe media, encode video, alter source files, or install a Windows service yet.

## Prerequisites

- Windows 11
- .NET SDK 9
- `ab-av1`, `ffmpeg`, and `ffprobe`, either on `PATH` or configured with executable paths

Build and run:

```powershell
dotnet build VideoOptimiser.sln
dotnet run --project src/VideoOptimiser.Cli -- config init --config .\video-optimiser.yaml
```

`config init` never overwrites a file. Its template deliberately contains an empty `watch.roots` list and archive directory. Edit both before validation; this avoids silently watching or archiving into an unexpected user directory.

## Configuration resolution

For commands that load configuration, the first available source wins:

1. `--config <path>`
2. `VIDEO_OPTIMISER_CONFIG`
3. `%ProgramData%\VideoOptimiser\config.yaml`
4. `video-optimiser.yaml` in the current directory

Relative database, log, archive, temporary, and watch-root paths are interpreted relative to the configuration file. Relative executable paths containing a directory separator are also resolved there; executable names without one are left for `PATH` resolution.

## Phase 1 commands

```powershell
video-optimiser config init [--config <path>]
video-optimiser config show [--config <path>]
video-optimiser config validate [--config <path>]
video-optimiser doctor [--config <path>] [--json]
video-optimiser scan [--config <path>] [--path <folder>] [--recursive] [--json]
```

`config validate` checks configuration semantics without probing tools or writing a database. `doctor` checks configuration, makes configured log/archive/temp directories when possible, verifies write access and free-space visibility, initialises the SQLite migration ledger, and runs each tool's version command with safe process argument lists.

`scan` is safe and read-only: it discovers configured files, skips links and excluded files, checks that a candidate is stable, asks `ffprobe` for media information, and reports whether the primary video stream is eligible. It never queues work, encodes media, moves files, archives files, or deletes files. By default, the configured two-minute age and four stable observations mean an eligible file can take at least 45 seconds to be reported.

The most relevant current exit codes are `0` (success), `2` (invalid arguments), `3` (invalid configuration), `4` (missing dependency), and `1` (another diagnostics failure).

## Safety boundary

Phases 1–2 only create configuration, logs, diagnostic probe files, and the SQLite migration ledger. They can inspect media during `scan`, but never queue work, encode, create temporary media output, move an original, archive a video, or delete a source.
