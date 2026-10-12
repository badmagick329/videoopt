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

An environment variable with the same name wins over the file. The key never goes in the YAML, and VideoOptimiser never prints or stores it.

### 3. Configure

```yaml
immich:
  baseUrl: "http://IMMICH_HOST:2283"
  minimumAgeDays: 90
  skipFavorites: true
  stagingDirectory: "immich-staging"
```

- **`baseUrl`:** use the server's LAN address. A public hostname behind Cloudflare rejects uploads over 100 MB, so large AV1 uploads would fail.
- **`minimumAgeDays`:** only videos taken more than this many days ago are converted. See [The age rule](#the-age-rule).
- **`skipFavorites`:** leaves favourites alone, matching the phone's "Free up space", which keeps them by default.
- **`stagingDirectory`:** where full-size originals are downloaded while they convert. It needs room for the largest batch you run. Relative paths are relative to the config file.

Phones usually record HEVC, so make sure your [eligibility rules](../README.md#choose-which-videos-to-convert) include `"hevc"`.

### 4. Check

```powershell
.\video-optimiser.exe doctor --config .\video-optimiser.yaml
```

`doctor` confirms that the server runs Immich 3, that it accepts the key, and that the staging directory is writable. It can't test every permission without changing your library: if one is missing, the command that needs it stops with exit code 3 and names it.

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

Start with one video:

```powershell
.\video-optimiser.exe immich run --first --config .\video-optimiser.yaml
```

Then drop `--first` to convert everything that matches. `immich run`:

1. Runs the [re-upload guard](#the-re-upload-guard).
2. Searches Immich for videos taken before the age cutoff, skipping favourites if configured, and anything already converted, skipped or failed.
3. Downloads each candidate to the staging directory, checks it against your eligibility rules, and queues it.
4. Converts the queue on your PC or remote worker, exactly like `queue run`.

Apart from the guard's work, it doesn't change Immich. `Ctrl+C` is safe; the next run resumes. If a `--first` run stops before converting, run it again: it converts the video it already downloaded and downloads no other.

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

## What you should know

- **Space comes back later.** Immich keeps trashed originals until it purges its trash (30 days by default, set in Immich's server settings). Offsite backups of Immich keep them until their own retention removes them.
- **Not carried over:** face names, comments and likes in shared albums, and memories. Immich detects faces again on the new asset.
- **Changing `eligibility.rules`** makes every earlier "ineligible" decision stale, so each of those videos is downloaded once more on the next run.
- **Failed or cancelled videos keep their download** in the staging directory. Delete those asset folders by hand when you no longer need them.
- **Exit code 10** after `immich run` means some videos failed to convert, or something else needs a look: a guard or search issue, or an asset Immich returned that its filter should have excluded. Read the output.

## The age rule

The phone decides whether a video is already backed up by its checksum. While a converted original sits in Immich's trash, its checksum still counts. Once Immich purges it, a copy left on the phone looks new and uploads again.

So convert only videos the phone no longer has. Keep the phone's "Free up space" cutoff at `minimumAgeDays` or more recent, never older.

## The re-upload guard

Every `immich run` starts by checking whether the phone has uploaded a converted original again. If it has, the guard moves that copy back to Immich's trash and lists the files, with their capture dates, for you to delete from the phone. It only touches assets whose checksum matches an original VideoOptimiser converted.

If you restore a converted original from Immich's trash yourself, the guard leaves it alone and reports it as restored.
