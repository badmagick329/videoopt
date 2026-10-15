# VideoOptimiser

Shrink big videos by converting them to AV1 at the same visual quality. An original is replaced only after you review the result and say so.

VideoOptimiser finds videos worth converting in your folders or your [Immich](https://immich.app) library, using rules you set for codec, resolution, bitrate and file size. [ab-av1](https://github.com/alexheretic/ab-av1) picks the smallest file that still meets your quality target (VMAF 95 by default), and a video that would not shrink enough is marked not worth converting. Every AV1 is checked (codec, duration, streams, a decode test, real savings) before it can replace anything.

Encoding runs on your PC, on a Hetzner worker it creates and deletes for you, or on any Linux server you reach over SSH.

Windows only. To have an AI assistant explain it to you, point the assistant at [docs/agent-guide.md](docs/agent-guide.md).

## How it works

```text
queue add  →  CRF search  →  encode  →  validate  →  you review  →  finalize
(queue)       (find the       (AV1 file)  (checks)     (queue list)   (replace the
              right quality)                                          original)
```

`queue add` queues videos. `run` takes every queued video through the three middle steps. Until `finalize`, the AV1 waits in a `.video-optimiser` folder beside the original, and the original is not touched.

Press `Ctrl+C` once and the run stops after the video it is converting. Press it again to stop now. The next `run` continues that video from its last finished step. A stopped run exits with `9`.

With a Hetzner worker up, the first press also asks `Delete the worker when the run stops? [y/N]`. `y` deletes the worker once the video is done; keep the window open until it says so. `N` keeps it. Stopping now on a remote run stops only your side: the encode keeps running on the worker, and the worker keeps billing, until you run `run` again or delete it with `worker delete`. If deleting the worker would lose work, stopping now asks again and says what it would lose.

## Requirements

Windows 11 (x64), plus these tools on your PC:

| Tool | Needed for | Where to get it |
| --- | --- | --- |
| FFmpeg and ffprobe | Every setup | The "full" build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), or `winget install Gyan.FFmpeg` |
| ab-av1 0.11.7 or newer | Encoding on your PC | `cargo install ab-av1 --locked`, or `ab-av1.exe` from its [releases](https://github.com/alexheretic/ab-av1/releases) |

To encode on your PC, FFmpeg needs libsvtav1, libvmaf and libdav1d; the full builds above have all three. A remote worker installs its own ab-av1 and FFmpeg, so your PC then needs only FFmpeg and ffprobe, with libdav1d for validation.

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
   .\video-optimiser.exe queue add --first --config .\video-optimiser.yaml
   .\video-optimiser.exe run --config .\video-optimiser.yaml
   ```

   The first line of `run` says where encoding runs, such as `Encoding on this PC.` If it names a worker you didn't expect, press `Ctrl+C`; nothing has been encoded yet.

5. Review the result, then replace the original:

   ```powershell
   .\video-optimiser.exe queue list --config .\video-optimiser.yaml
   .\video-optimiser.exe finalize --ready --config .\video-optimiser.yaml
   ```

   The converted file waits in a `.video-optimiser` folder beside the original, and `queue list --json` gives its path as `outputPath`. Watch it before you finalise. By default `finalize` deletes the original; it says what it will do to the originals and asks once.

When one video works, drop `--first` to queue everything that matches your rules. To queue a file you pick, name it: `queue add D:\Videos\clip.mp4`. It must sit in a watch folder and fit your rules, or you add `--force`.

At any point, run `.\video-optimiser.exe` with no command (or `status`) to see where things stand:

```text
Queue: 3 waiting, 1 ready to finalise.
Run: none.
Next:
  video-optimiser finalize --ready   finalises the ready video; review it first
  video-optimiser run               converts the 3 videos in the queue
```

The card shows the queue's counts, the run going now, the Hetzner worker if you use one (checked against Hetzner), and up to three commands worth running next. With no config found, it says so and names `config init`. To see each video, use `queue list`.

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

With Hetzner, `run` shows the hourly price and asks `Create the worker? [y/N]` before it creates one, and only once a video needs it. When the run finishes, it deletes the worker if `deleteAfterRun` is on (the default), failed videos included. If you stop the run with `Ctrl+C`, it asks what to do with the worker. `maxHourlyPrice` caps the price (€0.30 by default). `worker status` and `worker delete` manage a worker by hand, and `status` shows one that is still billing. Validation, review and finalisation always happen on your PC. [docs/remote-ssh.md](docs/remote-ssh.md) covers setup for both remote modes, including `remote setup` for Hetzner.

Add `--local` or `--remote` to `run` or `immich run` to use the other mode for one run without editing the config. A video that started in the other mode waits for a later run.

## Immich

VideoOptimiser can also convert old videos in your Immich library. Each AV1 is uploaded as a new asset with the original's albums, tags, favourite, description and rating, and the original goes to Immich's trash.

```powershell
.\video-optimiser.exe queue add immich --first --config .\video-optimiser.yaml
.\video-optimiser.exe run --config .\video-optimiser.yaml
.\video-optimiser.exe immich finalize --config .\video-optimiser.yaml
```

`queue add immich` downloads old videos, checks each against your rules and queues the ones that fit. `--first` adds one new video; `--limit <n>` adds `n`. Videos that already have a job don't count, so each repeat adds `n` more. `run` then converts the whole queue, folder videos and Immich videos together. `immich run` does both steps in one command. `finalize --ready` takes Immich videos too; `immich finalize` lists only them and shows their capture dates.

Read [docs/immich.md](docs/immich.md) before the first run. It covers the API key, the phone's "Free up space" setting, and why only videos older than 90 days are touched.

## Commands

All commands take `--config <path>`.

| Command | What it does |
| --- | --- |
| `config init [--quality archive\|standard\|compact]` | Write a starter config. Never overwrites a file. |
| `config validate`, `config show` | Check the config, or print it. |
| `doctor [--json]` | Check the config, tools, database, disk space, Immich and any remote worker. |
| `queue add [--first]` | Queue the videos in the watch folders that match your rules. |
| `queue add <file> [--force]` | Queue one file. `--force` skips the watch folders and the rules, but not the check that the file can save enough. |
| `queue add immich [--first \| --limit <n>]` | Download up to `n` new old videos from Immich, check them against your rules and queue the ones that fit. |
| `run [--local \| --remote]` | Convert every queued video, folder and Immich alike. Stops before replacing anything. |
| `status` (or no command) | A card: the queue's counts, the run going, the Hetzner worker (checked against Hetzner), and the commands worth running next. Add `--json` for scripts. |
| `queue list`, `history` | `queue list` shows the open videos in groups: converting now, the queue in run order, held, and ready to finalise. `history` shows the finished ones. Add `--json` for scripts. |
| `calibrate [--json]` | Show how your rules fit your library. Changes nothing. |
| `queue hold <job-id>`, `queue release <job-id>` | Make runs pass over a waiting or stopped video, or take it again. |
| `queue next <job-id>` | Convert a waiting or stopped video next, and release it if it was held. A `run` that is going takes it after the current video. |
| `queue drop <job-id>`, `queue drop --all` | Take one video, or every waiting, stopped and held one, out of the queue. Refuses while `cleanup` would then delete the only copy of a video. |
| `queue retry <job-id>` | Queue a failed, dropped or not worth converting video again. |
| `validate <job-id>` | Re-run the checks on the AV1 of a video that is ready to finalise. |
| `finalize <job-id>`, `finalize --ready` | Replace one original, or every ready one, Immich videos included. |
| `cleanup [--dry-run]` | Delete temporary files no video will use again. Keeps an AV1 that may be the only copy. |
| `immich run [--first \| --limit <n>]`, `immich finalize` | `immich run` is `queue add immich` plus `run`: it converts the whole queue. `--limit` counts new videos only. `immich finalize` uploads each AV1 to Immich and moves the original to Immich's trash. |
| `remote setup` | One-time Hetzner setup. Creates no worker. |
| `worker create`, `worker status`, `worker delete` | Manage the Hetzner worker by hand. |
| `version` | Print the version. |

Exit code `0` means success. The other exit codes are listed in [docs/agent-guide.md](docs/agent-guide.md#exit-codes).

## Safety

- Originals are only read until you finalise. `finalize` moves the original aside, puts the AV1 in place, and moves the original back if anything fails.
- A video that failed, was dropped or was not worth converting is not queued again until its file changes or you run `queue retry`.
- The job database (`jobs.db` by default) tracks every job, so a crash or `Ctrl+C` never loses track of a file. One run uses it at a time: a second `run` or `immich run` refuses and names the one running. A hold or a drop made while a run goes can't be undone by that run.
- AV1 files wait in a `.video-optimiser` folder beside each original. `cleanup` deletes the ones no video needs. When an AV1 may be the only copy of a video, because the original is missing or gone from Immich, `queue drop` refuses and `cleanup` keeps it.
- Every run that ends tells you what happened to the Hetzner worker, including a run you stopped.
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
