# Converting an Immich library

VideoOptimiser can convert old videos in your [Immich](https://immich.app) library to AV1. Each round runs in two steps that you start by hand:

1. `immich run` is `queue add immich` followed by `run`: it downloads matching videos, queues them and converts the whole queue, folder videos included. Immich keeps every original.
2. `immich finalize` uploads each AV1 as a new asset and moves the original to Immich's trash, after you confirm.

Requires Immich 3.

## One-time setup

### 1. Create an API key

In Immich, open **Account settings → API keys** and create a key with exactly these permissions:

`asset.read`, `asset.download`, `asset.upload`, `asset.update`, `asset.copy`, `asset.delete`, `tag.asset`, `user.read`

### 2. Store the key

Put it in the `.env` file beside your config (copy `.env.example` if you don't have one):

```dotenv
VIDEO_OPTIMISER_IMMICH_API_KEY=paste-your-key-here
```

An environment variable with the same name wins over the file. The key never goes in the YAML, and VideoOptimiser never prints or stores it. To use another variable or file, set `immich.apiKeyEnvironmentVariable` or `immich.apiKeyFile`; a relative file path is relative to the config file.

### 3. Configure

```yaml
immich:
  baseUrl: "http://IMMICH_HOST:2283"
  minimumAgeDays: 90
  skipFavorites: true
  stagingDirectory: "immich-staging"
```

- **`baseUrl`:** use the server's LAN address. A public hostname behind Cloudflare rejects uploads over 100 MB, so large AV1 uploads would fail. Leave it empty to turn Immich off; the `immich` commands then refuse to run.
- **`minimumAgeDays`:** only videos taken more than this many days ago are converted. See [The age rule](#the-age-rule).
- **`skipFavorites`:** leaves favourites alone, matching the phone's "Free up space", which keeps them by default.
- **`stagingDirectory`:** where full-size originals are downloaded while they convert. It needs room for the largest batch you run. Relative paths are relative to the config file.

Phones usually record HEVC, so make sure your [eligibility rules](configuration.md#choose-which-videos-to-convert) include `"hevc"`.

### 4. Check

```powershell
.\video-optimiser.exe doctor --config .\video-optimiser.yaml
```

`doctor` confirms that the server runs Immich 3, that it accepts the key, and that the staging directory is writable. It can't test every permission without changing your library. If one is missing, the error names it: `immich run` and `queue add immich` stop with exit code 3, and `immich finalize` fails that video, leaves it ready to finalise, and exits 8 (or 10 when other videos succeeded).

Using only Immich? You can leave `watch.roots` empty.

## Each round

### 1. Free up space on the phone

Do this first, so every video you convert has already left the phone.

1. In the Immich app, tap your avatar, then **Free up space**.
2. Under **Keep on device**, choose **All photos**, so only videos are removed.
3. Set the **Cutoff date** to `minimumAgeDays` ago (90 days by default) or more recent.
4. Check the review screen, then confirm.
5. Empty the phone's own trash (Google Photos or Files: **Bin → Empty bin**). Until you do, the space isn't freed.

### 2. Convert

Start with one video, encoded on this PC whatever `processing.mode` says, so the first round costs nothing:

```powershell
.\video-optimiser.exe immich run --first --local --config .\video-optimiser.yaml
```

`--first` is `--limit 1`. To add more, use `--limit <n>`, such as `--limit 5`, or drop the flag to add everything that matches. Both add at most `n` new videos. Videos that already have a job (waiting, stopped, held or ready to finalise) are not counted, so running `immich run --limit 5` again adds five more. A paid remote round is worth a few videos, since creating the worker takes several minutes. Drop `--local` to encode where the config says; `--remote` does the opposite for one run. How a remote run ends, and what happens to the worker, is in [remote-ssh.md](remote-ssh.md). `immich run` then works through these steps:

1. Prints where encoding runs, such as `Encoding on this PC (--local).`, before downloading anything.
2. Runs the [re-upload guard](#the-re-upload-guard), which prints something only when it acts.
3. Opens with `Looking in Immich for up to 5 new videos to convert.` (or `Looking in Immich for every video that fits your rules.` without a limit), then searches Immich, oldest first, for timeline and archived videos taken before the age cutoff. It leaves out the locked folder, the trash, external libraries, favourites if configured, and anything already converted, skipped or failed. Without a download it also passes over a video whose extension is not in `eligibility.extensions`, one smaller than every rule's `minimumFileSize`, and one no bigger than `savings.minimumBytesSaved` (default `1MiB`), which cannot save that much. A video whose conversion would not save enough (`quality.maxEncodedPercent`, default `80`, or `savings.minimumBytesSaved`) ends as `Not worth converting`, and later runs count it as `handled in earlier runs`.
4. Downloads each candidate to the staging directory, checks it against your eligibility rules, and queues it. Before the first download it says that each video is downloaded and checked, and that a download that doesn't fit is deleted. A video that doesn't match is deleted at once, so most downloads you see go by without leaving a file. Each downloaded candidate gets one line with its number, capture date, codec, resolution, video bitrate, size and outcome, so the outcome reads against what was measured: `  1. clip.mp4  2024-03-04  hevc 4K 33 Mbps  319.4 MiB  queued: fits rule 3 (1 of 1)`, `h264 4K 42 Mbps  495.1 MiB  doesn't fit: rule 1 needs 2GiB` (what the closest rule asks for, as written in your config) or `issue: ...`. The rule numbers count the entries of `eligibility.rules` from 1, and appear only when you have more than one rule.
5. Ends with what it did: `Downloaded and checked 12 videos: 3 queued, 9 don't fit your rules (...)`, a count for everything it left alone without a download (`Not downloaded: ...`), `Immich has no more videos that fit your rules.` when a limit was not reached, and the queue's counts, such as `Queued: 3. Already queued: 8. Held: 1. Ready to finalise: 2.`
6. Converts the whole queue on your PC or remote worker, exactly like `run`: folder videos too, and the Immich videos queued by earlier rounds. It downloads again the copy of any Immich video that is missing one (`Downloading again: ...`), such as a video `queue retry` queued after `cleanup` deleted its copy. If Immich no longer has that video, it fails as `SourceMissing`. The run ends by naming the videos ready to finalise, such as `2 videos are ready to finalise: review them, then run 'video-optimiser finalize --ready'.`

`queue add immich [--first | --limit <n>]` does steps 2 to 5 and stops, ending with `Next: run 'video-optimiser run'.` Use it to queue Immich videos now and convert them later, together with the videos in your watch folders. A file in the current folder named `immich` is given to `queue add` as `.\immich`.

`run` converts the queue the same way without searching Immich for new videos. When `immich.baseUrl` isn't set, or Immich can't be reached for the download, it says how many Immich videos stay queued (`2 Immich videos stay queued: <reason>.`) and converts the rest. A run reads the queue again before each video, so `queue hold <job-id>`, `queue release <job-id>` and `queue next <job-id>` count from its next video. A held video is passed over until you release it.

Apart from the guard's work, it doesn't change Immich. The first `Ctrl+C` stops after the video being converted and prints `To convert the rest: video-optimiser run`. A second one, or one during the search, stops at once and prints `To resume:` with the same command. The resume command is always `run`, never `immich run`: the videos `immich run` queued are already in the queue, so repeating `immich run --limit 5` would add five more. After a stop during the search, run `queue add immich` again to look for more.

### 3. Review

The converted files are in the staging directory, under `<asset ID>\.video-optimiser\`. `queue list` shows the videos that are ready to finalise, and `queue list --json` gives each one's exact path as `outputPath`. Watch a few. In the [full-screen view](tui.md), the Review pane opens each one with `a`, and `r` rejects one you don't want.

### 4. Finalise

```powershell
.\video-optimiser.exe immich finalize --config .\video-optimiser.yaml
```

It lists each ready video with its capture date, so you can find it in Immich, plus its sizes and savings, then asks once. For each video it:

1. Uploads the AV1 as a new asset with the original's capture date.
2. Copies the original's albums, tags, favourite, description, rating, shared links, stack and sidecar to it.
3. Moves the original to Immich's trash.
4. Deletes the video's staging folder.

If it stops partway, run it again: it continues where it left off. Until step 3, the original is untouched.

`immich finalize` finalises Immich videos only. `finalize --ready`, the command a run's closing line names, finalises folder and Immich videos together and asks once for both.

A video that cannot be finalised yet stays ready, and the others carry on. The message says what to do:

- **Immich can't find the original.** It was deleted for good or moved to the Locked Folder; Immich gives the same answer for both. Move it out of the Locked Folder and run `immich finalize` again. If it was deleted, the message names the copies that are left: the AV1 already in Immich (restore it first if it is in the trash), or the download and the AV1 on this PC. Move local copies somewhere safe before you `queue drop` the job, because `cleanup` deletes a dropped job's files; until then `queue drop` refuses.
- **The original or the AV1 is in Immich's trash.** Restore it, then run `immich finalize` again.
- **The local AV1 is missing.** Put it back, or drop and retry the job to convert it again. Once the AV1 is uploaded, the local file is no longer needed.
- **The download in the staging directory is missing.** Nothing is wrong: finalising compares the original in Immich with the one that was converted, not with the download.

## What you should know

- **Immich's trash must stay on.** VideoOptimiser never deletes from Immich; it moves originals to the trash, which is your undo. With the trash off, Immich would delete them for good, so `doctor` fails and `immich run`, `queue add immich` and `immich finalize` stop with exit code 3 until you turn it back on under **Administration → Settings → Trash Settings**. `doctor` warns when the trash keeps videos for fewer than 7 days.
- **Space comes back later.** Immich keeps trashed originals until it purges its trash (30 days by default, set in Immich's server settings). Offsite backups of Immich keep them until their own retention removes them.
- **Not carried over:** face names, comments and likes in shared albums, and memories. Immich detects faces again on the new asset.
- **Changing `eligibility.rules`** makes every earlier "doesn't fit" decision stale. The next search says how many videos are checked again, and downloads each one once more as it reaches it.
- **Changing the quality bar checks skipped videos again.** A video that ended `Not worth converting` is left alone while `quality.minimumVmaf`, `maxEncodedPercent`, `preset`, `encoder`, `pixelFormat`, `crfSearch.minCrf`/`maxCrf` and `savings.minimumBytesSaved` stay the same; changing any of them, or the video's file, downloads it again on the next run. It is not a failure: it does not change the exit code, and `immich run` counts it in its closing tally as `Not worth converting: N.`
- **iPhone 16 spatial audio is left out.** FFmpeg can't copy it, so the AV1 keeps the stereo track and the video gets a note saying so. To leave those videos unconverted instead, set `quality.unsupportedAudio` to `skip` (see [configuration.md](configuration.md#audio-ffmpeg-cant-copy)). An Immich video skipped this way isn't downloaded again until you set it back to `drop`.
- **`calibrate` includes Immich without downloading anything.** It searches the same videos `immich run` would. Immich reports no codec, so a video no run has downloaded is listed by resolution and by a bitrate worked out from its size and length, which includes the audio. `calibrate` can name one of those videos for you to test, but no command converts a single Immich video.
- **Rejecting a ready video** (`queue reject <job-id>`) deletes its AV1 and its download at once and leaves the original in Immich. `immich run` then passes over that asset until it changes in Immich, and says `rejected by you` in its `Not downloaded` line. `queue retry` undoes it, and the next `run` downloads the video again. A video whose finalisation stopped partway is refused: `immich finalize` finishes it.
- **Failed, dropped or skipped videos keep their download** in the staging directory until you run `cleanup`, which deletes the download with the job's temporary files and asset folder. A download an open job still needs is never deleted, and `cleanup` finds downloads even after you empty `immich.baseUrl`. `cleanup` also keeps a failed, dropped or skipped video's download and AV1 while either may be the only copy: Immich is not configured, can't be reached, or says the original is gone (deleted, in the trash or replaced). It prints `Kept  <path> (<reason>)` for each, and deletes them once Immich shows the original again. After `queue retry`, the next `run` or `immich run` downloads the video again.
- **Exit codes after `immich run`:** `6` means videos failed and none is ready to finalise. `10` means some failed while others are ready, or something else needs a look: a guard or search issue, or an asset Immich returned that its filter should have excluded. `9` means `Ctrl+C` stopped the run. Videos not worth converting change none of these. `queue add immich` exits `10` for the same guard and search issues, and `0` otherwise. Read the output.

## The age rule

The phone decides whether a video is already backed up by its checksum. While a converted original sits in Immich's trash, its checksum still counts. Once Immich purges it, a copy left on the phone looks new and uploads again.

So convert only videos the phone no longer has. Keep the phone's "Free up space" cutoff at `minimumAgeDays` or more recent, never older.

## The re-upload guard

Every `immich run` and `queue add immich` starts by checking whether the phone has uploaded a converted original again. If it has, the guard moves that copy back to Immich's trash and lists the files, with their capture dates, for you to delete from the phone. It only touches assets whose checksum matches an original VideoOptimiser converted.

If you restore a converted original from Immich's trash yourself, the guard leaves it alone and reports it as restored.
