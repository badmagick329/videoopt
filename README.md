# VideoOptimiser

Shrink big videos by converting them to AV1 at the same visual quality. Nothing replaces an original until you've watched the result and said yes.

It finds videos worth converting in your folders or your [Immich](https://immich.app) library, using rules you set. [ab-av1](https://github.com/alexheretic/ab-av1) picks the smallest file that still meets your quality target, and every AV1 is checked before it can replace anything. The app runs on Windows only (for now). It can encode on the same PC, or hand the encoding to a Hetzner server it creates and deletes for you, or to a Linux server you already have.

## Quick start

You need [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) (the "full" build, or `winget install Gyan.FFmpeg`) and [ab-av1](https://github.com/alexheretic/ab-av1/releases) 0.11.7 or newer, on `PATH` ([details](docs/configuration.md#tools)).

1. Download `video-optimiser-v<version>-win-x64.zip` from [Releases](https://github.com/badmagick329/videoopt/releases), unzip it, and open PowerShell in that folder.

2. Create a config. It asks for a quality target; Standard is a good start.

   ```powershell
   .\video-optimiser.exe config init --config .\video-optimiser.yaml
   ```

3. Open `video-optimiser.yaml` and add a folder of videos under `watch.roots`:

   ```yaml
   watch:
     roots:
       - path: "D:\\Videos"
         recursive: true
   ```

   The starter rules match only large H.264 files. Phone videos are usually HEVC: see [Choose which videos to convert](docs/configuration.md#choose-which-videos-to-convert).

4. Check the setup:

   ```powershell
   .\video-optimiser.exe doctor --config .\video-optimiser.yaml
   ```

5. Queue one video and convert it:

   ```powershell
   .\video-optimiser.exe queue add --first --config .\video-optimiser.yaml
   .\video-optimiser.exe run --config .\video-optimiser.yaml
   ```

   The first line of `run` says where encoding runs, such as `Encoding on this PC.` If that isn't what you expect, press `Ctrl+C`; nothing has been encoded yet.

6. Open the full-screen view, go to the Review pane with `tab`, and press `a` to watch the AV1:

   ```powershell
   .\video-optimiser.exe --config .\video-optimiser.yaml
   ```

7. If you like it, press `f` to replace the original. If you don't, press `r` to reject it: the AV1 is deleted and the original stays.

When one video works, run `queue add` without `--first` to queue everything that matches your rules.

## How it works

```text
queue add  →  run  →  you review  →  finalize
(queue)       (find the right quality,       (replace the
              encode, check)                 original)
```

- `queue add` queues videos. `run` converts the whole queue, one video at a time.
- Until you finalise, the AV1 waits in a `.video-optimiser` folder beside the original, and the original isn't touched.
- By default, `finalize` deletes the original. It can also move it to an archive folder or keep it. See [What finalize does to originals](docs/configuration.md#what-finalize-does-to-originals).
- A video that won't shrink enough is marked not worth converting and left alone.

## The full-screen view

`video-optimiser` with no command opens a view of the queue, the worker, the run's log and the videos ready to finalise. From it you can start a run in the background, stop it, hold or drop videos, and review, finalise or reject each AV1. Press `?` for every key. Quitting never stops a run. See [docs/tui.md](docs/tui.md).

If you prefer plain commands, every action has one, and `status` prints a short summary of where things stand.

## Stopping a run

In a terminal, press `Ctrl+C` once to stop after the video converting now, and again to stop at once. The next `run` picks up where it stopped. A background run stops with `x` or `X` in the full-screen view, or `run stop [--now]`.

## Where encoding runs

| Mode | Config | Good for |
| --- | --- | --- |
| Your PC | `processing.mode: "local"` (the default) | Trying it out, a few videos, a fast CPU |
| Hetzner, managed | `processing.mode: "remoteSsh"`, `remoteSsh.lifecycle: "hetzner"` | Large batches |
| Your own Linux server | `processing.mode: "remoteSsh"`, `remoteSsh.lifecycle: "manual"` | A spare machine you already run |

A Hetzner server bills by the hour. `run` shows the price and asks before it creates one, and deletes it when the run ends unless you say otherwise. `--local` or `--remote` on `run` switches the mode for one run. See [docs/remote-ssh.md](docs/remote-ssh.md).

## Immich

It can also convert old videos in your Immich library. Each AV1 goes back to Immich as a new asset with the original's albums, tags, favourite, description and rating, and the original goes to Immich's trash.

```powershell
.\video-optimiser.exe queue add immich --first --config .\video-optimiser.yaml
.\video-optimiser.exe run --config .\video-optimiser.yaml
```

Read [docs/immich.md](docs/immich.md) before the first run. It covers the API key, the phone's "Free up space" setting, and why only videos older than 90 days are touched.

## Safety

- Originals are only read until you finalise. `finalize` moves the original aside, puts the AV1 in place, and moves the original back if anything fails.
- Every AV1 is checked before it can be finalised: codec, duration, streams, a decode test, and the real saving.
- A video that failed, was dropped, was rejected or wasn't worth converting isn't queued again until its file changes or you run `queue retry`.
- The job database (`jobs.db`) tracks every video, so a crash or `Ctrl+C` never loses track of a file. One run uses it at a time.
- `cleanup` and `queue reject` won't delete an AV1 that may be the only copy of a video.
- The Hetzner token and Immich API key load from environment variables or a `.env` file, never from the YAML. Copy `.env.example` to `.env` to start.

## More documentation

- [docs/commands.md](docs/commands.md): every command, the status card, and how the config file is found.
- [docs/tui.md](docs/tui.md): the full-screen view, its keys, and background runs.
- [docs/configuration.md](docs/configuration.md): which videos are converted, tuning the rules with `calibrate`, why a video is not worth converting, and what happens to originals.
- [docs/remote-ssh.md](docs/remote-ssh.md): encoding on Hetzner or your own Linux server.
- [docs/immich.md](docs/immich.md): converting an Immich library.
- [docs/agent-guide.md](docs/agent-guide.md): point an AI assistant at this to have it explain the app to you, read your output, and tell you what to run next.

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
