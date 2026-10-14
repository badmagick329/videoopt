# VideoOptimiser guide for agents

You are operating VideoOptimiser for a user. It is a Windows CLI that converts videos to AV1 and replaces an original only when told to. This guide gives the workflow, the commands that need the user's go-ahead, and how to read results. For exact syntax, run `video-optimiser --help`; it is the source of truth.

## The gate

Originals change only in `finalize`, `finalize --ready` and `immich finalize`. Every other step works on temporary copies. A **gate** is a point where you stop, show the user what will happen, and wait for their explicit go-ahead in this conversation before running the command. Pass each gate once per action: approval to finalise one batch does not cover the next.

| Class | Commands | What to do |
| --- | --- | --- |
| Read-only | `doctor`, `status`, `history`, `queue list`, `calibrate`, `config show`, `config validate`, `version`, `worker status`, `cleanup --dry-run`, `process --dry-run` | Run freely. Add `--json` where offered. `doctor` may create the job database and the Immich staging folder. |
| Working | `config init`, `queue discover`, `queue run`, `process`, `immich run`, `validate`, `queue cancel`, `queue retry` | Run when the user's request covers it. They change only the job database and temporary files, and `config init` writes a new config that never replaces an existing one. The one exception: `immich run`'s re-upload guard moves converted originals the phone uploaded again back to Immich's trash, which is the intended outcome. |
| Gated | `finalize`, `immich finalize`, `cleanup`, `worker create`, `worker delete`, `remote setup` | Gate first. They replace originals, delete files, create or delete a paid server, or upload an SSH key to Hetzner and rewrite the config. |

**Paid encoding.** When the config has `processing.mode: "remoteSsh"` and `remoteSsh.lifecycle: "hetzner"`, a `queue run`, `process`, `immich run` or `worker create` that needs a server asks before creating one, which bills by the hour:

```text
Will create cpx42 in hel1 at EUR 0.134/h incl. VAT.
If that placement fails, a fallback may cost up to EUR 0.266/h (ccx33 in hel1).
Create the server? [y/N]
```

Gate it with the run's own quote:

1. Run the command without piping anything. It prints these lines, creates nothing, prints `No server was created.` and exits `0`. Its jobs stay queued.
2. Show the user the price lines.
3. After the go-ahead, run it again with `'y'` piped. One `y` also covers the fallbacks, up to the second line's price.

A run that reuses a running server never asks; it is already billing.

A new server prints `Setting up the worker: ... takes several minutes.`, then `Worker setup: P%, <stage>...` as it builds its tools, and `Worker ready after <duration>.` SSH is refused for the first few minutes of a boot; only after 3 minutes does `Still waiting for the worker's SSH (...)` appear, at most once a minute. That is still waiting, not failing; the run stops by itself at `hetzner.bootstrapTimeout`.

`--local` and `--remote` on `queue run`, `process` and `immich run` set the mode for that run only. `--remote` on a Hetzner config is gated as above. `--local` encodes on this PC and needs no gate; use it when the user wants a free trial run on a remote config. Never edit `processing.mode` to switch for one run.

**Remote setup.** `remote setup` is the one-time Hetzner setup. It uploads an SSH key to the user's Hetzner project before it asks anything, so gate even its first run. Without input it then prints the `processing:` section it would write, changes no file and exits `0`. Show the user that section; after their go-ahead, run `'y' | video-optimiser remote setup`. A rerun reuses the uploaded key, rewrites only the `processing:` section and keeps the old file as `<config>.bak`. Exit `3` after `No Hetzner API token was found.` means the user must create the token and add it to `.env` themselves; never ask for its value. Exit `3` after `remote setup configures managed Hetzner workers only` means the config uses a manual SSH host; that setup is in [remote-ssh.md](remote-ssh.md#manual-ssh-lifecycle). Exit `4` means Hetzner's API or `ssh-keygen` failed; report the message.

**Where encoding runs.** Take the encoding location from VideoOptimiser, not from your reading of the config. `doctor` reports it as `EncodingLocation` under `Processing`, and `queue run`, `process` and `immich run` print the same `Encoding on ...` line first, before any download or encoding. Quote it at the paid-encoding gate. If a run's line differs from what the user expects, stop the run before its next line.

**Prompts.** `finalize --ready`, `immich finalize` and `cleanup` ask `[y/N]` on standard input, as do `queue run`, `process`, `immich run` and `worker create` when they would create a Hetzner server, and `remote setup` before it writes the config. After the user approves, pipe the answer: `'y' | video-optimiser finalize --ready`. With no input, the command cancels and exits `0`. `finalize <job-id>` does not prompt, so gate it all the same. With redirected input, a failed Hetzner run never asks whether to delete the worker; it keeps it.

**Secrets.** `.env` beside the config holds `VIDEO_OPTIMISER_HETZNER_TOKEN` and `VIDEO_OPTIMISER_IMMICH_API_KEY`. Check that a key exists by reading its name only, for example `(Get-Content .env) -replace '=.*'`. Keep values out of your output, commands and files.

## Setup

1. Find the config. The app uses `--config <path>`, then `VIDEO_OPTIMISER_CONFIG`, then `%ProgramData%\VideoOptimiser\config.yaml`, then `video-optimiser.yaml` in the current folder. `config show` prints the one in use. Pass `--config` on every command when more than one may exist. With no config yet, ask the user for a quality level: `archive` (VMAF 97, indistinguishable from the original), `standard` (95, hard to tell apart) or `compact` (93, small visible loss, bigger savings). Then run `config init --config <path> --quality <level>`. Without `--quality`, redirected input gets `standard`. It never overwrites a file.
2. Run `doctor --json`. Done when no diagnostic has `"status": "Fail"`. Each failure's `message` says what to fix. Configuration fixes are the user's to choose: propose the YAML change, then apply it once they agree. A config written by an older version can fail with `Unknown setting '<path>' at line N, column M.` when a later version removed that setting. Propose deleting the line; the release notes on GitHub list each removed setting, and any replacement, under **Breaking config change**.

## Workflow: folders

1. **Queue.** `queue discover`. On a user's first run, add `--first` to queue one video. Done when it prints its `Queued: N.` summary, which leaves out zero counts, so a missing `Queued` means none. With nothing queued, report that and stop. On a large folder it prints `Checked N files so far ...` every 10 seconds; that is progress, not a hang. The starter rules match only large H.264 files at 1080p–1440p and at 4K or above, so phone footage (HEVC) needs a rule change first; `calibrate` shows what the rules leave out.
2. **Convert.** `queue run`. Done when it exits. A video can take an hour or more on a PC; run it in the background if your harness allows, and check progress with `status --json`. Exit `0`: no job failed; each is ready or not worth converting. Exit `10`: some failed and some are ready. Exit `6`: some failed and none is ready.
3. **Report.** From `status --json`, list each `ReadyToFinalize` job's `sourcePath`, `outputPath` and `percentageSaved`, and each failed job's `failureCategory` and `failureMessage`. Ask the user to watch the outputs.
4. **Gate.** Say what `original.action` will do to each original: `delete`, `archive` or `keep`.
5. **Finalise.** After the go-ahead, `'y' | video-optimiser finalize --ready`. It prints `Done    <file>: <message>` per job to standard output, or `Failed  <file>: <message>` to standard error, then the tally, such as `Finalised: 3. Failed: 1.` (zero counts are left out), then each failed job's ID, file and message. Done at exit `0`; exit `10` means at least one failed, so check `status --json`.

## Workflow: Immich

Prerequisites: `immich.baseUrl` is set and `doctor` passes its Immich checks. A `doctor` failure `ImmichTrashDisabled`, or exit `3` with `Immich's trash is off` from `immich run` or `immich finalize`, means the user must turn Immich's trash back on; only they can. Read [immich.md](immich.md) once for the reasons behind the age rule.

1. **Phone.** Ask the user whether they have run the Immich app's "Free up space" since the last round, with a cutoff no older than `immich.minimumAgeDays`, and emptied the phone's trash. Only the user can do this. Wait for their answer.
2. **Convert.** `immich run`, with `--first` on a user's first round and `--limit <n>` when the user names a round size. Before a paid remote round, ask the user for that size. Done when it exits. If it lists re-uploaded originals, pass that list to the user exactly as printed: they must delete those files from the phone. Exit `10` also covers guard or search issues; quote those lines.
   Discovery prints many lines before the first encode; *Immich discovery lines* under *Reading run output* says which are normal and which summary to report.
3. **Report.** As in the folders workflow; filter `status --json` to `"source": "immich"`. Each `outputPath` is under `immich.stagingDirectory`.
4. **Gate.** Each AV1 is uploaded as a new Immich asset and each original goes to Immich's trash.
5. **Finalise.** After the go-ahead, `'y' | video-optimiser immich finalize`. Done when it prints `Finalised: N.` with no `Failed:` count and exits `0`. If it stops partway, running it again continues; that rerun needs the gate too.

`finalize --ready` also finalises ready Immich jobs. Use `immich finalize` for Immich jobs, so the user sees capture dates before the prompt.

## Workflow: tuning the rules

Use this when the user asks why videos are not converted, or whether their rules fit their library.

1. **Report.** `calibrate --json`. It writes nothing and downloads nothing; with Immich configured it searches the library, which takes seconds. Exit `1` with `... is from an older version of video-optimiser` means run `status` once, then `calibrate` again.
2. **Explain.** Per rule, give `videos`, `bytes` and `history`. Then give the largest `keptOut` groups. `codecUnknown` groups are Immich videos no run has downloaded; their bitrate includes audio. When `historyLeftOut.otherSettings` is large, say that conversions from other or unrecorded quality settings are not counted.
3. **Propose.** Quote each suggestion's `text`. Rule changes are the user's to choose: propose the YAML, then apply it once they agree. A `testOne` suggestion's `command` is a `process --force`, a Working command that converts one file and stops before replacing it; on a paid remote config it passes the paid-encoding gate. An Immich `testOne` has no command; name the file to the user.

## Reading run output

`queue run`, `process` and `immich run` print one block per job. `queue run` and `immich run` then print a tally of the non-zero outcomes (`Ready to finalise: 4. Not worth converting: 1.`, or `Nothing to finalise.`) and, when something is ready, a `Next:` line naming `finalize --ready` or `immich finalize`. `process` prints no tally; when its job is ready, its `Next:` line names `finalize <job-id>`. A job block:

```text
[2/5] PXL_20250531_041434717.mp4 (1002.4 MiB)
      Uploading 1002.4 MiB to the worker...
      Uploaded 1002.4 MiB in 23s, checksum verified.
      Finding the best CRF for VMAF 97 (2 test clips of 20s each)...
        CRF 34   VMAF 95.02   size  19%   below target
        CRF 29   VMAF 97.13   size  29%   passes, chosen
      Chose CRF 29 in 7m 36s. Expected output: 284.9 MiB (29% of the original).
      Encoding at CRF 29, expected to take about 5 minutes...
        20%, 54 fps, about 1m 06s left
      Encoded 285.1 MiB (29% of the original) in 5m 02s.
      Downloading 285.1 MiB from the worker...
      Downloaded 285.1 MiB in 30s, checksum verified.
      Checking the output: streams, duration, savings and decode test...
      Ready to finalise: 1002.4 MiB -> 287.3 MiB, 71.3% saved.
```

The upload and download lines appear only on a remote worker. When `savings.minimumBytesSaved` sets the video's size cap, the search line names it: `Finding the best CRF for VMAF 97 that saves 100MiB (...)`. On a terminal the output is coloured; redirected output is plain text. If your harness runs commands in a terminal and you see escape codes such as `ESC[32m`, set `NO_COLOR=1`. The last line of each block is the job's outcome:

| Outcome line | Job status |
| --- | --- |
| `Ready to finalise: …` | `ReadyToFinalize` |
| `Not worth converting: …` | `Skipped` |
| `Failed validation: …` | `Failed`, `ValidationFailed` |
| `Failed during the upload`, `the CRF search`, `encoding` or `the download: …`, then `Full log: <path>` | `Failed`; the full ab-av1 log stays at that path |
| `Worker unreachable: … Run the same command again to resume.` | `Interrupted` |

**Slow or stuck.** A CRF search prints one `CRF` line per attempt, usually 4 to 7 attempts of about 2 minutes each for a 1 GB phone video on a Hetzner cpx42, longer on a PC. An encode prints a progress line about every 15 seconds and states its expected length when it starts; its progress lines add `about <time> left` only after 30 seconds and 5 percentage points, so early lines without it are normal. A running job that prints nothing new for 15 minutes is stuck: report it with the job's last lines and `status --json`.

### Immich discovery lines

`immich run` downloads each candidate to check it, so before the first job block it prints one line per video checked:

- `[n] <file>  <date>  <codec> <resolution> <bitrate>  <size>  skip: <reason>` is normal. The rejected download is deleted from the staging folder, and the video is skipped in future runs.
- `queued: fits rule N` and `skip: rule N needs ...` number the rules from 1 in `eligibility.rules` order; with a single rule, the number is left out.
- `issue: ...` means a video failed its checksum or probe. It is tried again on the next run.
- `N videos were skipped under different eligibility rules; ...` (or `1 video was ...`) is normal after a rules change, and returns on later runs until the search has reached each of them.
- The re-upload guard prints only when it finds a converted original in Immich again.

Report the summary to the user: `Found X of N after checking M.` with `--first` or `--limit`, `Found N videos to convert after checking M.` without, followed by `Skipped K: <reasons>.` and, when present, `Not downloaded: ...`.

## Recovery

Match the job's `status` and `failureCategory` from `status --json` or `history --json`:

| What you see | Meaning | Action |
| --- | --- | --- |
| A `Skipped <file>: started on ...` line | The job started in the other mode, and `--local` or `--remote` chose this one | Nothing is lost. Run the command without the flag to resume it; a remote resume is gated as paid encoding. |
| `Interrupted` | Stopped by `Ctrl+C`, a crash or a lost connection. A remote CRF search or encode keeps running on the worker | Run the same command again (`queue run`, `immich run` or `process`). It resumes, following a remote stage that is still running or using its result. |
| `RemoteUnavailable` | The remote worker could not be reached | Run the command again. A Hetzner worker is kept for the resume. |
| `Skipped`, `NotWorthConverting` | No CRF met both `quality.minimumVmaf` and the video's size cap: the stricter of `quality.maxEncodedPercent` and `savings.minimumBytesSaved`. A video too small to save `savings.minimumBytesSaved` is skipped before its CRF search, with nothing uploaded. A normal outcome, not a failure | Report `failureMessage`. It names the setting that ruled the video out and the best CRF found, or, when even `quality.crfSearch.minCrf` missed the VMAF, that CRF's score. Discovery checks the video again once the file, those quality settings or `savings.minimumBytesSaved` change. |
| `ValidationFailed` | The AV1 failed a check, such as saving less than `savings.minimumBytesSaved` when the encode came out larger than the search predicted | Report it. The original stays as it is, and discovery won't queue it again until the file changes. |
| `StagingFailed`, `CrfSearchFailed`, `EncodeFailed`, `DownloadFailed`, `ProcessingFailed` | A processing step failed | Report `failureMessage`. Run `queue retry <id>` only if the user asks, once the cause is fixed. |
| `RemoteConfigurationChanged` | The job started under a different processing setup | Report it. `queue retry <id>` starts a fresh job under the current setup, if the user wants. |
| `Cancelled` | `queue cancel` stopped it | Nothing. `queue retry <id>` queues it again, only if the user asks. |
| `Ineligible`, `SourceNotReady`, `SourceOutsideWatchRoot`, `SourceChanged`, `SourceMissing` | The file did not qualify, changed, or was gone when the job ran | Nothing. The next `queue discover` or `immich run` re-checks it. |
| `FinalizationIncomplete` | An Immich finalisation stopped partway | Gate, then `'y' | video-optimiser immich finalize`. |
| `FinalizationFailed`, `ManualInterventionRequired` | Replacing a file original failed or was cut off | Stop. Show the user the job, its `sourcePath` and `outputPath`, and let them decide. |

**Failed, cancelled or skipped jobs** stay settled: discovery skips them until the original changes, and a skipped one also once the quality settings change. `queue retry <id>` queues one again; run it only at the user's request.

**Retained Hetzner worker.** After a remote run with any failed or interrupted job (a skipped one does not count), the server stays up so the work can resume, and it keeps billing. After `Ctrl+C` or a killed command, a CRF search or encode keeps running on it too: the rerun picks it up. With `deleteAfterRun: false` it stays after every run. `worker status` shows it, whatever `processing.mode` says. Tell the user; deleting it is gated, because outputs not yet downloaded are lost with it.

## Reference

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success, or a confirmation answered "no" |
| `1` | General failure, including a `doctor` failure outside configuration and dependencies, and `calibrate` on a database an older version wrote |
| `2` | Invalid arguments, including an invalid job ID, or `queue retry` on a job it cannot retry (not found, not failed, cancelled or skipped, or the original already has an open job) |
| `3` | Invalid configuration, including `worker create` or `worker delete` without `remoteSsh.lifecycle: "hetzner"`, or an Immich API key that is missing, rejected or lacks a permission |
| `4` | A failed dependency check in `doctor`: a media tool, the Immich server (unreachable, not Immich 3, its key, or its trash off) or the Hetzner API. Also `remote setup` when it cannot run `ssh-keygen` or list or upload the Hetzner key |
| `5` | `process` on a file the rules exclude; add `--force` to convert it anyway |
| `6` | Processing failed and nothing is ready, including a `process` whose job failed or whose file is missing; an Immich request failed; ffprobe could not read a file; Hetzner had no server capacity; or `validate` or `finalize <job-id>` named a job that does not exist |
| `7` | The output failed validation, including an output ffprobe cannot read (`validate`, `process`), or `finalize <job-id>` named a job that is not ready |
| `8` | `finalize <job-id>` failed, or `immich finalize` failed for every job |
| `9` | Cancelled with `Ctrl+C` |
| `10` | Partial success: some jobs failed (any failure in `finalize --ready`), or `queue discover`, `immich run`, `cleanup` or `calibrate` hit issues; read the output |

### Job statuses

- **Open** (`status`, `queue list`): `Queued`, `Staging`, `CrfSearching`, `Encoding`, `Downloading`, `Validating`, `ReadyToFinalize`, `Finalizing`, `Interrupted`.
- **Finished** (`history`): `Completed`, `Failed`, `Cancelled`, `Skipped`.

`Staging` and `Downloading` are transfers to and from a remote worker. `ReadyToFinalize` jobs can be finalised, and so can an Immich job whose finalisation was cut off (`Finalizing` or `Interrupted`).

### JSON output

`status --json`, `history --json` and `queue list --json` print an array of jobs:

| Field | Value |
| --- | --- |
| `id` | Job ID, 32 hex characters; commands take it as `<job-id>` |
| `status` | A job status above |
| `sourcePath` | The original; for Immich jobs, its staged copy |
| `source` | `file` or `immich` |
| `immichAssetId`, `immichNewAssetId` | The original's and the AV1's Immich asset IDs, or `null` |
| `crf` | The CRF chosen by the search, or `null` |
| `outputPath` | The temporary AV1, or `null` before encoding starts; the file exists once the job reaches `Validating` |
| `percentageSaved` | Size saving in percent, or `null` |
| `failureCategory`, `failureMessage` | Why the job failed or was skipped, or what it needs, or `null` |
| `updatedUtc` | Last change, ISO 8601 |

`doctor --json` prints `configurationPath`, `exitCode` and `diagnostics`, an array of `{ category, status, code, message }` where `status` is `Pass`, `Warning` or `Fail`.

`calibrate --json` prints one object. Bitrates are whole Mbps; sizes are bytes.

| Field | Value |
| --- | --- |
| `minimumVmaf` | The quality target the history was filtered to |
| `rules` | Per rule in config order: `number`, `codecs`, `resolution`, `minimumVideoBitrate`, `minimumFileSize`, `videos`, `bytes`, `history` |
| `keptOut` | Groups no rule admits, largest first: `codec`, `resolution`, `minimumMbps`, `maximumMbps`, `videos`, `bytes`, `history`. `resolution` `null` is unknown; `minimumMbps` `null` is an unknown bitrate; `maximumMbps` `null` alone is the 100+ band |
| `codecUnknown` | Immich videos never downloaded, grouped the same way without `codec` or `history`; their bitrate includes audio |
| `alreadyAv1` | `{ videos, bytes }` already AV1 or already converted |
| `historyLeftOut` | `{ otherSettings, noVideoFacts }`: results not counted |
| `suggestions` | Each has `kind` and `text`. `raiseMinimumBitrate`: `rule`, `minimumVideoBitrate`. `addRule`: `codec`, `resolution`, `minimumVideoBitrate`. `testOne`: `codec`, `resolution`, `minimumMbps`, `maximumMbps`, `path` and `command` for a file, or `immichAssetId` for an Immich video |

`history` is `null` when nothing was tried, or `{ converted, convertedBitrateMbps, savedPercent, notWorthIt, notWorthItBitrateMbps }` with each range `{ min, max }` or `null`. It counts only completed and skipped jobs whose quality settings match the current config.
