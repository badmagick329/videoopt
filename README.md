# VideoOptimiser

Shrink big videos by converting them to AV1 at the same visual quality. An original is replaced only after you review the result and say so.

VideoOptimiser finds videos worth converting in your folders or your [Immich](https://immich.app) library, using rules you set for codec, resolution, bitrate and file size. [ab-av1](https://github.com/alexheretic/ab-av1) picks the smallest file that still meets your quality target (VMAF 95 by default), and a video that would not shrink enough is skipped. Every output is checked (codec, duration, streams, a decode test, real savings) before it can replace anything.

Encoding runs on your PC, on a Hetzner server it creates and deletes for you, or on any Linux server you reach over SSH.

Windows only. If an AI agent runs it for you, point the agent at [docs/agent-guide.md](docs/agent-guide.md).

## How it works

```text
discover  →  CRF search  →  encode  →  validate  →  you review  →  finalize
(queue)      (find the       (temporary  (checks)     (status)        (replace the
             right quality)  AV1 file)                                 original)
```

Every step before `finalize` works on a temporary file. Press `Ctrl+C` once and the run stops after the video it is converting; press it again to stop at once, and the next run resumes that video from a safe stage. On a remote worker, stopping at once stops only your side: the encode keeps running there, and the worker keeps billing, until you rerun the command or delete the worker.

## Requirements

Windows 11 (x64), plus these tools on your PC:

| Tool | Needed for | Where to get it |
| --- | --- | --- |
| FFmpeg and ffprobe | Every setup | The "full" build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), or `winget install Gyan.FFmpeg` |
| ab-av1 0.11.7 or newer | Encoding on your PC | `cargo install ab-av1 --locked`, or `ab-av1.exe` from its [releases](https://github.com/alexheretic/ab-av1/releases) |

To encode on your PC, FFmpeg needs libsvtav1, libvmaf and libdav1d; the full builds above have all three. A remote server installs its own ab-av1 and FFmpeg, so your PC then needs only FFmpeg and ffprobe, with libdav1d for validation.

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

   It asks for a quality target and writes it as `quality.minimumVmaf`. Archive (97) is indistinguishable from the original, Standard (95, the default) is hard to tell apart, and Compact (93) shows a small loss for bigger savings. A higher target leaves fewer videos worth converting.

2. Open `video-optimiser.yaml` and add a folder under `watch.roots`:

   ```yaml
   watch:
     roots:
       - path: "D:\\Videos"
         recursive: true
   ```

   The starter rules match only large H.264 files. Phone videos are usually HEVC, so for those, edit the rules first ([docs/configuration.md](docs/configuration.md#choose-which-videos-to-convert)).

3. Check the setup:

   ```powershell
   .\video-optimiser.exe doctor --config .\video-optimiser.yaml
   ```

4. Queue one video and convert it:

   ```powershell
   .\video-optimiser.exe queue discover --first --config .\video-optimiser.yaml
   .\video-optimiser.exe queue run --config .\video-optimiser.yaml
   ```

   The first line says where encoding runs, such as `Encoding on this PC.` If it names a server you didn't expect, press `Ctrl+C`; nothing has been encoded yet.

5. Review the result, then replace the original:

   ```powershell
   .\video-optimiser.exe status --config .\video-optimiser.yaml
   .\video-optimiser.exe finalize --ready --config .\video-optimiser.yaml
   ```

   The converted file waits in a `.video-optimiser` folder beside the original, and `status --json` gives its path as `outputPath`. Watch it before you finalise. By default `finalize` deletes the original; it says what it will do to each one and asks once.

When one video works, drop `--first` to queue everything that matches your rules.

Set `VIDEO_OPTIMISER_CONFIG` to the config's full path and you can leave out `--config`. Without either, the app looks for `%ProgramData%\VideoOptimiser\config.yaml`, then `video-optimiser.yaml` in the current folder.

## Configure it

[docs/configuration.md](docs/configuration.md) covers the settings you are most likely to change:

- which videos are converted, and tuning the rules with `calibrate`;
- why a video is skipped as not worth converting, and what to change;
- what `finalize` does to originals (delete, archive or keep), and why a `.mov` comes back as `.mp4`.

## Where encoding runs

| Mode | Config | Good for |
| --- | --- | --- |
| Your PC | `processing.mode: "local"` (the default) | Trying it out, a few videos, a fast CPU |
| Hetzner, managed | `processing.mode: "remoteSsh"`, `remoteSsh.lifecycle: "hetzner"` | Large batches |
| Your own Linux server | `processing.mode: "remoteSsh"`, `remoteSsh.lifecycle: "manual"` | A spare machine you already run |

With Hetzner, the app shows the hourly price and asks before it creates a server, then deletes the server after a clean run. `maxHourlyPrice` caps the price (€0.30 by default). Validation, review and finalisation always happen on your PC. [docs/remote-ssh.md](docs/remote-ssh.md) covers setup for both remote modes, including `remote setup` for Hetzner.

Add `--local` or `--remote` to `queue run`, `process` or `immich run` to use the other mode for one run without editing the config.

## Immich

VideoOptimiser can also convert old videos in your Immich library. Each AV1 is uploaded as a new asset with the original's albums, tags, favourite, description and rating, and the original goes to Immich's trash.

```powershell
.\video-optimiser.exe immich run --first --config .\video-optimiser.yaml
.\video-optimiser.exe immich finalize --config .\video-optimiser.yaml
```

Read [docs/immich.md](docs/immich.md) before the first run. It covers the API key, the phone's "Free up space" setting, and why only videos older than 90 days are touched.

## Commands

All commands take `--config <path>`.

| Command | What it does |
| --- | --- |
| `config init [--quality archive\|standard\|compact]` | Write a starter config. Never overwrites a file. |
| `config validate`, `config show` | Check the config, or print it. |
| `doctor [--json]` | Check the config, tools, database, disk space, Immich and any remote worker. |
| `queue discover [--first]` | Queue the videos in the watch folders that match your rules. |
| `queue run [--local \| --remote]` | Convert every queued video. Stops before replacing anything. |
| `process <file> [--force] [--dry-run]` | Convert one file now. `--force` skips the rules. |
| `status`, `history`, `queue list` | Open jobs, finished jobs, or open jobs only. Add `--json` for scripts. |
| `calibrate [--json]` | Show how your rules fit your library. Changes nothing. |
| `queue hold <job-id>`, `queue release <job-id>` | Make runs pass over a waiting or stopped video, or take it again. |
| `queue next <job-id>` | Convert a waiting or stopped video next. A `queue run` that is going takes it after the current video. |
| `queue drop <job-id>`, `queue drop --all` | Take one video, or every waiting, stopped and held one, out of the queue. Refuses while `cleanup` would then delete the only copy of a video. |
| `queue retry <job-id>` | Queue a failed, dropped or skipped video again. |
| `validate <job-id>` | Re-run the checks on a job's output. |
| `finalize <job-id>`, `finalize --ready` | Replace one original, or every ready one. |
| `cleanup [--dry-run]` | Delete temporary files no job will use again. |
| `immich run [--first \| --limit <n>]`, `immich finalize` | The same flow for an Immich library. |
| `remote setup` | One-time Hetzner setup. Creates no server. |
| `worker create`, `worker status`, `worker delete` | Manage the Hetzner server by hand. |
| `version` | Print the version. |

Exit code `0` means success. The other exit codes are listed in [docs/agent-guide.md](docs/agent-guide.md#exit-codes).

## Safety

- Originals are only read until you finalise. `finalize` moves the original aside, puts the AV1 in place, and moves the original back if anything fails.
- A video that failed, was dropped or was skipped is not queued again until its file changes or you run `queue retry`.
- The job database (`jobs.db` by default) tracks every job, so a crash or `Ctrl+C` never loses track of a file. One run uses it at a time: a second `queue run`, `immich run` or `process` refuses and names the one running.
- Temporary files live in a `.video-optimiser` folder beside each original. `cleanup` deletes the ones no job needs.
- The Hetzner token and the Immich API key load from environment variables or a `.env` file, never from the YAML. Copy `.env.example` to `.env` to start.

## Build from source

You need the .NET 10 SDK. From the repository root:

```powershell
.\scripts\build.ps1
dotnet test tests/VideoOptimiser.UnitTests
```

`build.ps1` builds and smoke-tests `artifacts\local\video-optimiser.exe`. For quick development runs, use `dotnet run --project src/VideoOptimiser.Cli -- <arguments>`.

CI also runs an end-to-end test that encodes, validates and finalises generated clips with pinned ab-av1 and FFmpeg builds. To run it locally:

```powershell
$env:PATH = ((.\scripts\install-media-tools.ps1 -Destination .\artifacts\media-tools) -join ';') + ";$env:PATH"
.\scripts\e2e-test.ps1 -Executable .\artifacts\local\video-optimiser.exe
```

## License

[MIT](LICENSE)
