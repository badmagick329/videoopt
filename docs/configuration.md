# Configuration

Settings live in the YAML file that `config init` writes. This page covers the ones you are most likely to change. Remote encoding is in [remote-ssh.md](remote-ssh.md), and Immich is in [immich.md](immich.md).

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
- `minimumVideoBitrate` is the video stream's bitrate as ffprobe reports it. A file without one never matches.
- Only files with an extension in `eligibility.extensions` are scanned. The default is `.mkv`, `.mp4`, `.mov` and `.m4v`.
- Discovery never queues a video too small to save `savings.minimumBytesSaved` (100 MiB by default).

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
- a typical file to test from the largest groups never tried.

A suggestion based on earlier runs needs at least 3 results, and only results at your current quality settings count, since a video that converts at VMAF 95 may not at 97. Videos converted before `calibrate` existed recorded no settings, so they are left out. Immich reports no codec until a video is downloaded, so Immich videos no run has opened are listed by resolution and bitrate only.

When there is nothing to suggest, the last line says why: no videos were found, every video is already AV1, there are no results yet at your quality settings, there are too few results to judge, or the results point to no change. If Immich cannot be searched, the report still covers your watch folders, and `calibrate` then prints an error saying Immich is missing from it and exits with the same code as any other Immich failure.

## When a video is not worth converting

Each video gets a size cap, whichever of these is stricter:

- keep at most `quality.maxEncodedPercent` of its size (default `80`);
- save at least `savings.minimumBytesSaved` (default `100MiB`).

When no quality setting meets both your VMAF target and the cap, the job ends as `Not worth converting: <file>. <why>` and names the best setting it found. That counts as a normal outcome: the run still exits `0`, and a managed Hetzner server is deleted without asking. A video too small to save `minimumBytesSaved` is skipped before its CRF search, so it is never uploaded or searched.

To let more videos through, follow the reason. If it names the size cap, raise `quality.maxEncodedPercent` or lower `savings.minimumBytesSaved`, whichever it names. If it says even the lowest allowed CRF missed the VMAF, size was not the problem, and only a lower `quality.minimumVmaf` or `quality.crfSearch.minCrf` helps.

A skipped video is checked again when its file changes, or when you change `quality.minimumVmaf`, `maxEncodedPercent`, `preset`, `encoder`, `pixelFormat`, `crfSearch.minCrf`, `crfSearch.maxCrf` or `savings.minimumBytesSaved`. To try one again now, run `queue retry <job-id>`.

## What finalize does to originals

`original.action` decides:

| Action | The original | The AV1 |
| --- | --- | --- |
| `delete` (default) | Deleted | Takes the original's name and place |
| `archive` | Moved to `original.archiveDirectory` | Takes the original's name and place |
| `keep` | Left where it is | Saved beside it as `<name>.av1.mp4` or `<name>.av1.mkv` |

`archiveDirectory` is either a folder name, such as `Archive`, created beside each original, or a full path that collects every original in one place. Nothing is overwritten: a taken name gets a number, as in `movie (2).mp4`. The AV1 takes the original's Date created and Date modified, so it sorts where the original did.

### File type of the AV1

FFmpeg cannot write AV1 into `.mov` or `.m4v`, so the AV1 is always an `.mp4` or `.mkv`:

| Original | AV1 |
| --- | --- |
| `.mp4`, `.mov`, `.m4v` | `.mp4` |
| Anything else | `.mkv`, which keeps any subtitles |

`IMG_0897.MOV` becomes `IMG_0897.MP4`, so anything that tracked the old path, such as an editing catalogue or a media server's watch history, loses it.

Audio is copied, except PCM, which is re-encoded to FLAC. FLAC is lossless and plays in browsers; 32-bit float PCM becomes 24-bit.
