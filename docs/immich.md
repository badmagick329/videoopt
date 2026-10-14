# Converting an Immich library

VideoOptimiser can convert old videos in your [Immich](https://immich.app) library to AV1. Each round runs in two steps that you start by hand:

1. `immich run` downloads matching videos and converts them. Immich keeps every original.
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

`doctor` confirms that the server runs Immich 3, that it accepts the key, and that the staging directory is writable. It can't test every permission without changing your library. If one is missing, the error names it: `immich run` stops with exit code 3, and `immich finalize` fails that video, leaves it ready to finalise, and exits 8 (or 10 when other videos succeeded).

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

Then use `--limit <n>` for a round of at most `n` videos, such as `--limit 5`, or drop the flag to convert everything that matches. A paid remote round is worth a few videos, since creating the server takes several minutes. Drop `--local` to encode where the config says; `--remote` does the opposite for one run. `immich run`:

1. Prints where encoding runs, such as `Encoding on this PC (--local).`, before downloading anything.
2. Runs the [re-upload guard](#the-re-upload-guard), which prints something only when it acts.
3. Downloads again the staged copy of any open job that is missing one, such as a job `queue retry` queued after `cleanup` deleted its copy. If Immich no longer has that video, the job fails as `SourceMissing`.
4. Searches Immich, oldest first, for timeline and archived videos taken before the age cutoff. It leaves out the locked folder, the trash, external libraries, favourites if configured, and anything already converted, skipped or failed. Without a download it also passes over a video whose extension is not in `eligibility.extensions`, one smaller than every rule's `minimumFileSize`, and one no bigger than `savings.minimumBytesSaved` (default `100MiB`), which cannot save that much. A video whose conversion would not save enough (`quality.maxEncodedPercent`, default `80`, or `savings.minimumBytesSaved`) ends as `Not worth converting`, and later runs count it as `handled in earlier runs`.
5. Downloads each candidate to the staging directory, checks it against your eligibility rules, and queues it. A video that doesn't match is deleted at once, so most downloads you see go by without leaving a file. Each downloaded candidate gets one line with its capture date, codec, resolution, video bitrate, size and outcome, so the outcome reads against what was measured: `hevc 4K 33 Mbps  319.4 MiB  queued: fits rule 3 (1 of 1)`, `h264 4K 42 Mbps  495.1 MiB  skip: rule 1 needs 2GiB` (what the closest rule asks for, as written in your config) or `issue: ...`. The rule numbers count the entries of `eligibility.rules` from 1. The search ends with how many videos it found, why the others were skipped, and a count for everything it left alone without a download.
6. Converts the queue on your PC or remote worker, exactly like `queue run`.

Apart from the guard's work, it doesn't change Immich. `Ctrl+C` is safe; the next run resumes. If a `--first` or `--limit` run stops before converting, run it again with the same flag: videos already downloaded count toward the limit, so it converts those first and downloads only enough new ones to reach it.

### 3. Review

The converted files are in the staging directory, under `<asset ID>\.video-optimiser\`. `status --json` gives each one's exact path as `outputPath`. Watch a few.

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

A video that cannot be finalised yet stays ready, and the others carry on. The message says what to do:

- **Immich can't find the original.** It was deleted for good or moved to the Locked Folder; Immich gives the same answer for both. Move it out of the Locked Folder and run `immich finalize` again. If it was deleted, the message names the copies that are left: the AV1 already in Immich (restore it first if it is in the trash), or the download and the AV1 on this PC. Move local copies somewhere safe before you `queue drop` the job, because `cleanup` deletes a dropped job's files; until then `queue drop` refuses.
- **The original or the AV1 is in Immich's trash.** Restore it, then run `immich finalize` again.
- **The local AV1 is missing.** Put it back, or drop and retry the job to convert it again. Once the AV1 is uploaded, the local file is no longer needed.
- **The download in the staging directory is missing.** Nothing is wrong: finalising compares the original in Immich with the one that was converted, not with the download.

## What you should know

- **Immich's trash must stay on.** VideoOptimiser never deletes from Immich; it moves originals to the trash, which is your undo. With the trash off, Immich would delete them for good, so `doctor` fails and `immich run` and `immich finalize` stop with exit code 3 until you turn it back on under **Administration → Settings → Trash Settings**. `doctor` warns when the trash keeps videos for fewer than 7 days.
- **Space comes back later.** Immich keeps trashed originals until it purges its trash (30 days by default, set in Immich's server settings). Offsite backups of Immich keep them until their own retention removes them.
- **Not carried over:** face names, comments and likes in shared albums, and memories. Immich detects faces again on the new asset.
- **Changing `eligibility.rules`** makes every earlier "ineligible" decision stale, so each of those videos is downloaded once more on the next run.
- **Changing the quality bar checks skipped videos again.** A video that ended `Not worth converting` is left alone while `quality.minimumVmaf`, `maxEncodedPercent`, `preset`, `encoder`, `pixelFormat`, `crfSearch.minCrf`/`maxCrf` and `savings.minimumBytesSaved` stay the same; changing any of them, or the video's file, downloads it again on the next run. It is not a failure: it does not change the exit code, and `immich run` counts it in its closing tally as `Not worth converting: N.`
- **`calibrate` includes Immich without downloading anything.** It searches the same videos `immich run` would. Immich reports no codec, so a video no run has downloaded is listed by resolution and by a bitrate worked out from its size and length, which includes the audio. `calibrate` can name one of those videos for you to test, but no command converts a single Immich video.
- **Failed, dropped or skipped videos keep their download** in the staging directory until you run `cleanup`, which deletes the download with the job's temporary files and asset folder. A download an open job still needs is never deleted. After `queue retry`, the next `immich run` downloads the video again.
- **Exit codes after `immich run`:** `6` means videos failed and none is ready to finalise. `10` means some failed while others are ready, or something else needs a look: a guard or search issue, or an asset Immich returned that its filter should have excluded. Videos not worth converting change neither. Read the output.

## The age rule

The phone decides whether a video is already backed up by its checksum. While a converted original sits in Immich's trash, its checksum still counts. Once Immich purges it, a copy left on the phone looks new and uploads again.

So convert only videos the phone no longer has. Keep the phone's "Free up space" cutoff at `minimumAgeDays` or more recent, never older.

## The re-upload guard

Every `immich run` starts by checking whether the phone has uploaded a converted original again. If it has, the guard moves that copy back to Immich's trash and lists the files, with their capture dates, for you to delete from the phone. It only touches assets whose checksum matches an original VideoOptimiser converted.

If you restore a converted original from Immich's trash yourself, the guard leaves it alone and reports it as restored.
