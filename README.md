# VideoOptimiser

Shrink big videos by converting them to AV1 at the same visual quality. Nothing is replaced until you review the result and say so.

- **Finds** videos worth shrinking in your folders or your [Immich](https://immich.app) library, using rules you set: codec, resolution, bitrate and file size. Any codec FFmpeg can read works.
- **Converts** each one with [ab-av1](https://github.com/alexheretic/ab-av1), which picks the smallest file that still scores VMAF 95 or higher (you can change the target).
- **Checks** every output (codec, duration, streams, a decode test, real savings) before it can replace anything.
- **Waits** for you: the original stays untouched until you run `finalize`.

Encoding runs on your PC, on a Hetzner server it creates and deletes for you, or on any Linux server you reach over SSH.

Windows only. Using an AI agent to run it? Point the agent at [docs/agent-guide.md](docs/agent-guide.md).

## How it works

```text
discover  →  CRF search  →  encode  →  validate  →  you review  →  finalize
(queue)      (find the       (temporary  (checks)     (status)        (replace the
             right quality)  AV1 file)                                 original)
```

Every step before `finalize` works on a separate temporary file. Stop it at any point with `Ctrl+C`; the next run resumes from a safe stage.

## Requirements

Windows 11 (x64), plus these tools on your PC:

| Tool | Needed for | Where to get it |
| --- | --- | --- |
| FFmpeg and ffprobe | Every setup. ffprobe reads each video during discovery, and FFmpeg decodes each output during validation. | The "full" build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), or `winget install Gyan.FFmpeg` |
| ab-av1 0.11.7 or newer | Encoding on your PC only | `cargo install ab-av1 --locked`, or `ab-av1.exe` from its [releases](https://github.com/alexheretic/ab-av1/releases) |

- **Encoding on your PC:** FFmpeg needs libsvtav1, libvmaf and libdav1d. The full builds above have all three.
- **Encoding on a remote server:** the server installs its own ab-av1 and FFmpeg. Your PC needs only FFmpeg and ffprobe, with libdav1d for validation.

Put the tools on `PATH`, or set their paths under `tools:` in the config. `doctor` checks them.

## Install

1. Download `video-optimiser-v<version>-win-x64.zip` from [Releases](https://github.com/badmagick329/videoopt/releases).
2. Unzip it into a folder of your choice.
3. Open PowerShell in that folder.

The commands below assume you run them there, with the config file beside the exe.

## Quick start

1. Create a config file:

   ```powershell
   .\video-optimiser.exe config init --config .\video-optimiser.yaml
   ```

2. Open `video-optimiser.yaml` and add a folder under `watch.roots`:

   ```yaml
   watch:
     roots:
       - path: "D:\\Videos"
         recursive: true
   ```

   The starter rules only pick large H.264 files at 1080p and above. For phone videos, which are usually HEVC, edit them first: see [Choose which videos to convert](#choose-which-videos-to-convert).

3. Check everything is set up:

   ```powershell
   .\video-optimiser.exe doctor --config .\video-optimiser.yaml
   ```

4. Queue one video and convert it:

   ```powershell
   .\video-optimiser.exe queue discover --first --config .\video-optimiser.yaml
   .\video-optimiser.exe queue run --config .\video-optimiser.yaml
   ```

5. Look at the result, then replace the original:

   ```powershell
   .\video-optimiser.exe status --config .\video-optimiser.yaml
   .\video-optimiser.exe finalize --ready --config .\video-optimiser.yaml
   ```

   The converted file waits in a `.video-optimiser` folder beside the original; `status --json` gives its exact path as `outputPath`. Watch it before you finalise. `finalize` says what it will do to each original and asks once.

When one video works, drop `--first` to queue everything that matches your rules.

> **Tip:** set `VIDEO_OPTIMISER_CONFIG` to the config's full path and you can leave out `--config`. Without either, the app looks for `%ProgramData%\VideoOptimiser\config.yaml`, then `video-optimiser.yaml` in the current folder.

## Choose where encoding runs

| Mode | Config | Good for |
| --- | --- | --- |
| Your PC | `processing.mode: "local"` (the default) | Trying it out, a few videos, a fast CPU |
| Hetzner, managed | `processing.mode: "remoteSsh"` with `remoteSsh.lifecycle: "hetzner"` | Large batches. The app creates a server, sets it up, and deletes it after a clean run. You pay per hour. |
| Your own Linux server | `processing.mode: "remoteSsh"` with `remoteSsh.lifecycle: "manual"` | A spare machine you already run |

In both remote modes, validation, review and finalisation still happen on your PC. Setup for both is in [docs/remote-ssh.md](docs/remote-ssh.md).

## Immich

VideoOptimiser can also convert old videos in your Immich library. Each AV1 is uploaded as a new asset carrying over the original's albums, tags, favourite, description and rating, and the original goes to Immich's trash.

```powershell
.\video-optimiser.exe immich run --first --config .\video-optimiser.yaml
.\video-optimiser.exe immich finalize --config .\video-optimiser.yaml
```

Read [docs/immich.md](docs/immich.md) before the first run. It covers the API key, the phone's "Free up space" setting, and why only videos older than 90 days are touched.

## Choose which videos to convert

A video is queued when it matches **any** rule. Within a rule, **every** condition must match.

```yaml
eligibility:
  rules:
    - codecs: ["h264", "hevc"]
      resolution: "4k+"
      minimumVideoBitrate: "20Mbps"
      minimumFileSize: "2GiB"
    - codecs: ["h264"]
      resolution: "1080p-1440p"
      minimumVideoBitrate: "8Mbps"
      minimumFileSize: "800MiB"
```

- `resolution` is one of `1080p-1440p`, `1440p-4k` or `4k+`, measured by total pixels.
- `minimumVideoBitrate` is the video stream's bitrate as ffprobe reports it. A file without one never matches.
- `codecs` takes any codec name ffprobe reports, such as `h264`, `hevc`, `mpeg4`, `vp9` or `prores`. Phones usually record HEVC.
- Only files whose extension is in `eligibility.extensions` are scanned: `.mkv`, `.mp4`, `.mov` and `.m4v` by default.
- Small files rarely save enough to pass validation (`savings.minimumBytesSaved`, 100 MiB by default), so keep a size floor.

## Choose what happens to originals

`original.action` decides what `finalize` does:

| Action | The original | The AV1 |
| --- | --- | --- |
| `delete` (default) | Deleted | Takes the original's name and place |
| `archive` | Moved to `original.archiveDirectory` | Takes the original's name and place |
| `keep` | Left where it is | Saved beside it as `<name>.av1<extension>` |

`archiveDirectory` can be a folder name, such as `Archive`, created beside each original, or a full path that collects every original in one place. Nothing is ever overwritten: a taken name gets a number, as in `movie (2).mp4`.

## Commands

All commands take `--config <path>`.

| Command | What it does |
| --- | --- |
| `config init` | Write a starter config. Never overwrites an existing file. |
| `config validate`, `config show` | Check the config, or print its path and contents. |
| `doctor` | Check the config, tools, database, disk space, and any Immich server or remote worker. |
| `queue discover [--first]` | Scan the watch folders and queue matching videos. `--first` queues one. |
| `queue run` | Convert every queued video. Stops before replacing anything. |
| `process <file> [--force]` | Convert one file now. `--force` skips the rules and the watch-folder check. |
| `status`, `history` | Open jobs, and finished jobs. Add `--json` for scripts. |
| `queue list [--json]` | Open jobs only. |
| `queue cancel <job-id>`, `queue cancel --all` | Cancel one job, or every queued and interrupted job. |
| `queue retry <job-id>` | Queue a failed or cancelled video again. |
| `validate <job-id>` | Re-run the checks on a job's output. |
| `finalize <job-id>`, `finalize --ready` | Replace one original, or every ready one after a single confirmation. |
| `cleanup [--dry-run]` | Delete temporary files no job will use again. |
| `immich run [--first]`, `immich finalize` | The same flow for an Immich library. See [docs/immich.md](docs/immich.md). |
| `worker create`, `worker status`, `worker delete` | Manage the Hetzner server by hand. Normally the run does this. |
| `version` | Print the version. |

Exit code `0` means success. Scripts and agents can find every exit code in [docs/agent-guide.md](docs/agent-guide.md#exit-codes).

## Safety

- Originals are only read until you finalise.
- `finalize` moves the original aside first, puts the AV1 in place, and moves the original back if anything fails.
- A video that failed or was cancelled is not queued again until the file changes, or you run `queue retry`.
- Temporary outputs live in a `.video-optimiser` folder beside each original. `cleanup` removes the ones no job needs.
- The job database (`jobs.db` by default) tracks every job, so a crash or `Ctrl+C` never loses track of a file.
- Secrets (the Hetzner token, the Immich API key) load from environment variables or a `.env` file, never from the YAML. Copy `.env.example` to `.env` to start.

## Build from source

You need the .NET 10 SDK. Run these from the repository root; `scripts\` is the repository's own folder.

```powershell
.\scripts\build.ps1
```

This builds and smoke-tests `artifacts\local\video-optimiser.exe`. Use that path in place of `.\video-optimiser.exe` above. For quick development runs: `dotnet run --project src/VideoOptimiser.Cli -- <arguments>`.

Run the tests:

```powershell
dotnet test tests/VideoOptimiser.UnitTests
```

CI also runs an end-to-end test that encodes, validates and finalises generated clips with pinned ab-av1 and FFmpeg builds. To run it locally:

```powershell
$env:PATH = ((.\scripts\install-media-tools.ps1 -Destination .\artifacts\media-tools) -join ';') + ";$env:PATH"
.\scripts\e2e-test.ps1 -Executable .\artifacts\local\video-optimiser.exe
```

## License

[MIT](LICENSE)
