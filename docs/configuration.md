# Configuration

Settings live in the YAML file that `config init` writes. This page covers the ones you are most likely to change, and the tools it needs. Remote encoding is in [remote-ssh.md](remote-ssh.md), and Immich is in [immich.md](immich.md).

## Choose which videos to convert

A video is queued when it matches any rule. Within a rule, every condition must match.

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

- `codecs` takes any codec name ffprobe reports, such as `h264`, `hevc`, `mpeg4`, `vp9` or `prores`.
- `resolution` is `1080p-1440p`, `1440p-4k` or `4k+`, measured by total pixels.
- `minimumVideoBitrate` is the video stream's bitrate as ffprobe reports it. Many MKV files don't record one, so the whole file's bitrate is used instead. That figure includes the audio, so it reads slightly high.
- Only files with an extension in `eligibility.extensions` are scanned. The default is `.mkv`, `.mp4`, `.mov` and `.m4v`.
- `queue add` never queues a video too small to save `savings.minimumBytesSaved` (1 MiB by default), even with `--force`.

`queue add` scans the watch folders. `queue add <file>` queues one file after the same checks. A file outside your watch roots or rules is refused with exit `5`, and the message gives the command to queue it anyway with `--force`. `--force` skips only those two checks, and `queue retry` keeps it.

The starter config has two H.264 rules, one for `4k+` and one for `1080p-1440p`. Phones usually record HEVC, so add `hevc` rules before converting phone videos.

## Tune your rules

`calibrate` shows how well your rules fit your library. It reads the watch folders, Immich and the job history, and changes nothing.

```powershell
.\video-optimiser.exe calibrate --config .\video-optimiser.yaml
```

For each rule, it lists the videos the rule admits now and what earlier runs found for similar videos: how many converted and what they saved, and how many were not worth converting and at what bitrate. Videos no rule admits are grouped by codec, resolution and bitrate.

It can suggest three kinds of change:

- a higher `minimumVideoBitrate`, when a rule's lower-bitrate videos were never worth converting;
- a new rule, when videos the rules keep out converted well;
- a typical file to test from the largest groups never tried. The suggestion ends with `Test one:` and a `queue add "<file>" --force` command. Run it, then `run`. If other videos are waiting, `queue next <job-id>` puts the test file first.

A suggestion based on earlier runs needs at least 3 results, and only results at your current quality settings count, since a video that converts at VMAF 95 may not at 97. Videos converted before `calibrate` existed recorded no settings, so they are left out. Immich reports no codec until a video is downloaded, so Immich videos no run has opened are listed by resolution and bitrate only.

When there is nothing to suggest, the last line says why: no videos were found, every video found is already AV1, no video has been converted at your quality settings yet, there are too few results to judge, or the results call for no change. `calibrate --json` gives the same reason in `noSuggestions`.

If Immich cannot be searched, the report still covers your watch folders. `calibrate` then prints `Immich couldn't be searched, so its videos are not in this report: <reason>.` on standard error and exits with the same code as any other Immich failure.

## When a video is not worth converting

Each video gets a size cap, whichever of these is stricter:

- keep at most `quality.maxEncodedPercent` of its size (default `80`);
- save at least `savings.minimumBytesSaved` (default `1MiB`).

When no quality setting meets both your VMAF target and the cap, the video ends as `Not worth converting: <why>` and the reason names the best setting it found. That counts as a normal outcome: the run still exits `0`, and a Hetzner worker is handled as after any finished run. A video too small to save `minimumBytesSaved` is skipped before its CRF search, so it is never uploaded or searched. `history` lists these videos.

To let more videos through, follow the reason. If it names the size cap, raise `quality.maxEncodedPercent` or lower `savings.minimumBytesSaved`, whichever it names. If it says even the lowest allowed CRF missed the VMAF, size was not the problem, and only a lower `quality.minimumVmaf` or `quality.crfSearch.minCrf` helps.

The same two limits apply to the finished AV1. If it is not smaller than the original while `savings.requireSmallerOutput` is on, or it saves less than `savings.minimumBytesSaved`, the video also ends as not worth converting. Its AV1 and validation record are deleted, and the original stays where it is.

A video not worth converting is checked again when its file changes, or when you change `quality.minimumVmaf`, `maxEncodedPercent`, `preset`, `encoder`, `pixelFormat`, `crfSearch.minCrf`, `crfSearch.maxCrf` or `savings.minimumBytesSaved`. To try one again now, run `queue retry <job-id>`; `history` shows the ID.

## What finalize does to originals

`finalize --ready` says what will happen to the originals and asks once before it changes anything. `original.action` decides:

| Action | The original | The AV1 |
| --- | --- | --- |
| `delete` (default) | Deleted | Takes the original's name and place |
| `archive` | Moved to `original.archiveDirectory` | Takes the original's name and place |
| `keep` | Left where it is | Saved beside it as `<name>.av1.mp4` or `<name>.av1.mkv` |

This table is for videos from your watch folders. An Immich video's original always goes to Immich's trash (see [immich.md](immich.md)).

`archiveDirectory` is either a folder name, such as `Archive`, created beside each original, or a full path that collects every original in one place. Nothing is overwritten: a taken name gets a number, as in `movie (2).mp4`. The AV1 takes the original's Date created and Date modified, so it sorts where the original did.

### File type of the AV1

FFmpeg cannot write AV1 into `.mov` or `.m4v`, so the AV1 is always an `.mp4` or `.mkv`:

| Original | AV1 |
| --- | --- |
| `.mp4`, `.mov`, `.m4v` | `.mp4` |
| Anything else | `.mkv`, which keeps any subtitles |

`IMG_0897.MOV` becomes `IMG_0897.MP4`, so anything that tracked the old path, such as an editing catalogue or a media server's watch history, loses it.

Audio is copied, except PCM, which is re-encoded to FLAC. FLAC is lossless and plays in browsers; 32-bit float PCM becomes 24-bit.

## Tools

| Tool | Needed for | Where to get it |
| --- | --- | --- |
| FFmpeg and ffprobe | Every setup | The "full" build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), or `winget install Gyan.FFmpeg` |
| ab-av1 0.11.7 or newer | Encoding on your PC | `cargo install ab-av1 --locked`, or `ab-av1.exe` from its [releases](https://github.com/alexheretic/ab-av1/releases) |

To encode on your PC, FFmpeg needs libsvtav1, libvmaf and libdav1d; the full builds above have all three. A remote worker installs its own ab-av1 and FFmpeg, so your PC then needs only FFmpeg and ffprobe, with libdav1d for validation.

Put the tools on `PATH`, or set their paths under `tools:` in the config. `doctor` checks them.
