# VideoOptimiser guide for agents

You are operating VideoOptimiser for a user. It is a Windows CLI that converts videos to AV1 and replaces an original only when told to. This guide gives the workflow, the commands that need the user's go-ahead, and how to read results. For exact syntax, run `video-optimiser --help`; it is the source of truth.

## The gate

Originals change only in `finalize`, `finalize --ready` and `immich finalize`. Every other step works on temporary copies. A **gate** is a point where you stop, show the user what will happen, and wait for their explicit go-ahead in this conversation before running the command. Pass each gate once per action: approval to finalise one batch does not cover the next.

| Class | Commands | What to do |
| --- | --- | --- |
| Read-only | `doctor`, `status` (or no command), `history`, `queue list`, `calibrate`, `config show`, `config validate`, `version`, `worker status`, `cleanup --dry-run` | Run freely. Add `--json` where offered. `doctor` may create the job database and the Immich staging folder. |
| Working | `config init`, `queue add`, `queue add immich`, `run`, `immich run`, `validate`, `queue hold`, `queue release`, `queue next`, `queue drop`, `queue retry` | Run when the user's request covers it. They change only the job database and temporary files, and `config init` writes a new config that never replaces an existing one. The one exception: the re-upload guard of `immich run` and `queue add immich` moves converted originals the phone uploaded again back to Immich's trash, which is the intended outcome. |
| Gated | `finalize`, `immich finalize`, `cleanup`, `worker create`, `worker delete`, `remote setup` | Gate first. They replace originals, delete files, create or delete a paid server, or upload an SSH key to Hetzner and rewrite the config. |

**Paid encoding.** When the config has `processing.mode: "remoteSsh"` and `remoteSsh.lifecycle: "hetzner"`, a `run`, `immich run` or `worker create` that needs a server asks before creating one, which bills by the hour:

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

`--local` and `--remote` on `run` and `immich run` set the mode for that run only. `--remote` on a Hetzner config is gated as above. `--local` encodes on this PC and needs no gate; use it when the user wants a free trial run on a remote config. Never edit `processing.mode` to switch for one run.

**Remote setup.** `remote setup` is the one-time Hetzner setup. It uploads an SSH key to the user's Hetzner project before it asks anything, so gate even its first run. Without input it then prints the `processing:` section it would write, changes no file and exits `0`. Show the user that section; after their go-ahead, run `'y' | video-optimiser remote setup`. A rerun reuses the uploaded key, rewrites only the `processing:` section and keeps the old file as `<config>.bak`. Exit `3` after `No Hetzner API token was found.` means the user must create the token and add it to `.env` themselves; never ask for its value. Exit `3` after `remote setup configures managed Hetzner workers only` means the config uses a manual SSH host; that setup is in [remote-ssh.md](remote-ssh.md#manual-ssh-lifecycle). Exit `3` after `Updated <config>` and `[Code] message` lines on standard error means the edit was written but the config has other problems; report them, and expect no `Next:` line. Exit `4` means Hetzner's API or `ssh-keygen` failed; report the message.

**Where encoding runs.** Take the encoding location from VideoOptimiser, not from your reading of the config. `doctor` reports it as `EncodingLocation` under `Processing`, and `run` and `immich run` print the same `Encoding on ...` line first, before any download or encoding. Quote it at the paid-encoding gate. If a run's line differs from what the user expects, stop the run before its next line.

**Prompts.** `finalize --ready`, `immich finalize` and `cleanup` ask `[y/N]` on standard input, as do `run`, `immich run` and `worker create` when they would create a Hetzner server, and `remote setup` before it writes the config. After the user approves, pipe the answer: `'y' | video-optimiser finalize --ready`. With no input, the command cancels and exits `0`. `finalize <job-id>` does not prompt, so gate it all the same. With redirected input, a failed Hetzner run never asks whether to delete the worker; it keeps it. `cleanup` prints `Kept  <path> (<reason>)` for a file it leaves alone, such as an AV1 whose original is missing and may be the only copy; pass those lines to the user and never delete the file yourself. `cleanup --dry-run` ends with `Dry run: nothing was deleted.`

**Secrets.** `.env` beside the config holds `VIDEO_OPTIMISER_HETZNER_TOKEN` and `VIDEO_OPTIMISER_IMMICH_API_KEY`. Check that a key exists by reading its name only, for example `(Get-Content .env) -replace '=.*'`. Keep values out of your output, commands and files.

## Setup

1. Find the config. The app uses `--config <path>`, then `VIDEO_OPTIMISER_CONFIG`, then `%ProgramData%\VideoOptimiser\config.yaml`, then `video-optimiser.yaml` in the current folder. `config show` prints the one in use. Pass `--config` on every command when more than one may exist. With no config yet, ask the user for a quality level: `archive` (VMAF 97, indistinguishable from the original), `standard` (95, hard to tell apart) or `compact` (93, small visible loss, bigger savings). Then run `config init --config <path> --quality <level>`. Without `--quality`, redirected input gets `standard`. It never overwrites a file.
2. Run `doctor --json`. Done when no diagnostic has `"status": "Fail"`. Each failure's `message` says what to fix. Configuration fixes are the user's to choose: propose the YAML change, then apply it once they agree. A config written by an older version can fail with `Unknown setting '<path>' at line N, column M.` when a later version removed that setting. Propose deleting the line; the release notes on GitHub list each removed setting, and any replacement, under **Breaking config change**.

## Workflow: folders

1. **Queue.** `queue add`. On a user's first run, add `--first` to queue one video. Done when it prints its `Queued: N.` summary, which leaves out zero counts, so a missing `Queued` means none. `Held: N.` and `Ready to finalise: N.` count videos a run will not take; its last lines name what to run next for each. With nothing queued, report that and stop. On a large folder it prints `Checked N files so far ...` every 10 seconds; that is progress, not a hang. The starter rules match only large H.264 files at 1080p–1440p and at 4K or above, so phone footage (HEVC) needs a rule change first; `calibrate` shows what the rules leave out.
2. **Convert.** `run`. It converts every queued video, Immich ones included, and passes over held ones, which it counts first (`2 videos are held; ...`). Done when it exits. A video can take an hour or more on a PC; run it in the background if your harness allows, and check progress with `status --json` (its `run.converting`) or `queue list --json`. Exit `0`: no job failed; each is ready or not worth converting. Exit `10`: some failed and some are ready. Exit `6`: some failed and none is ready.
3. **Report.** From `queue list --json`, list each `ReadyToFinalize` job's `sourcePath`, `outputPath` and `percentageSaved`, and each failed job's `failureCategory` and `failureMessage`. Ask the user to watch the outputs.
4. **Gate.** Say what `original.action` will do to each original: `delete`, `archive` or `keep`.
5. **Finalise.** After the go-ahead, `'y' | video-optimiser finalize --ready`. It prints `Done    <file>: <message>` per job to standard output, or `Failed  <file>: <message>` to standard error, then the tally, such as `Finalised: 3. Failed: 1.` (zero counts are left out), then each failed job's ID, file and message. Done at exit `0`; exit `10` means at least one failed: find each `Failed` line in *Recovery*.

## Workflow: Immich

Prerequisites: `immich.baseUrl` is set and `doctor` passes its Immich checks. A `doctor` failure `ImmichTrashDisabled`, or exit `3` with `Immich's trash is off` from `immich run`, `queue add immich` or `immich finalize`, means the user must turn Immich's trash back on; only they can. Read [immich.md](immich.md) once for the reasons behind the age rule.

1. **Phone.** Ask the user whether they have run the Immich app's "Free up space" since the last round, with a cutoff no older than `immich.minimumAgeDays`, and emptied the phone's trash. Only the user can do this. Wait for their answer.
2. **Convert.** `immich run`, with `--first` on a user's first round and `--limit <n>` when the user names a round size; `--limit` counts new videos only, never the ones already queued. Before a paid remote round, ask the user for that size. `immich run` converts the whole queue, so videos queued from the watch folders convert too. To queue Immich videos without converting, use `queue add immich` with the same flags; it ends with the `Next:` line. Done when it exits. If it lists re-uploaded originals, pass that list to the user exactly as printed: they must delete those files from the phone. Exit `10` also covers guard or search issues; quote those lines.
   Discovery prints many lines before the first encode; *Immich discovery lines* under *Reading run output* says which are normal and which summary to report.
3. **Report.** As in the folders workflow; filter `queue list --json` to `"source": "immich"`. Each `outputPath` is under `immich.stagingDirectory`.
4. **Gate.** Each AV1 is uploaded as a new Immich asset and each original goes to Immich's trash.
5. **Finalise.** After the go-ahead, `'y' | video-optimiser immich finalize`. Done when it prints `Finalised: N.` with no `Failed:` count and exits `0`. Otherwise find each `Failed` line in *Recovery*. If it stops partway, running it again continues; that rerun needs the gate too.

`finalize --ready` also finalises ready Immich jobs. Use `immich finalize` for Immich jobs, so the user sees capture dates before the prompt.

## Workflow: tuning the rules

Use this when the user asks why videos are not converted, or whether their rules fit their library.

1. **Report.** `calibrate --json`. It writes nothing and downloads nothing; with Immich configured it searches the library, which takes seconds. Exit `1` with `... is from an older version of video-optimiser` means run `queue list` once, then `calibrate` again.
2. **Explain.** Per rule, give `videos`, `bytes` and `history`. Then give the largest `keptOut` groups. `codecUnknown` groups are Immich videos no run has downloaded; their bitrate includes audio. When `historyLeftOut.otherSettings` is large, say that conversions from other or unrecorded quality settings are not counted.
3. **Propose.** Quote each suggestion's `text`. Rule changes are the user's to choose: propose the YAML, then apply it once they agree. A `testOne` suggestion's `command` is a `queue add "<file>" --force`, a Working command that queues one file outside the rules; the `run` that converts it stops before replacing it, and on a paid remote config passes the paid-encoding gate. An Immich `testOne` has no command; name the file to the user.

## Reading run output

`run` and `immich run` print one block per job, then a tally of the non-zero outcomes (`Ready to finalise: 4. Not worth converting: 1.`, or `Nothing to finalise.` when nothing is ready anywhere) and, when any video is ready to finalise, the ones earlier runs left included, a last line that counts them and names `finalize --ready`, which finalises folder and Immich videos alike. A job block:

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
| `Worker unreachable: …` | `Interrupted`, `RemoteUnavailable`; the run stops here (see *Recovery*) |

**Slow or stuck.** A CRF search prints one `CRF` line per attempt, usually 4 to 7 attempts of about 2 minutes each for a 1 GB phone video on a Hetzner cpx42, longer on a PC. An encode prints a progress line about every 15 seconds and states its expected length when it starts; its progress lines add `about <time> left` only after 30 seconds and 5 percentage points, so early lines without it are normal. A running job that prints nothing new for 15 minutes is stuck: report it with the job's last lines and `queue list --json`.

### Immich discovery lines

`immich run` and `queue add immich` download each candidate to check it, so before the first job block they print one line per video checked:

- `  n. <file>  <date>  <codec> <resolution> <bitrate>  <size>  doesn't fit: <reason>` is normal. The rejected download is deleted from the staging folder, and the video is not checked again until the rules change.
- `queued: fits rule N` and `doesn't fit: rule N needs ...` number the rules from 1 in `eligibility.rules` order; with a single rule, the number is left out. With a limit, `queued (2 of 3)` counts the new videos queued.
- `issue: ...` means a video failed its checksum or probe. It is tried again on the next run. `Issue: <file>: the name Immich gives it can't be saved safely on this PC; left alone.` and `Left alone: <file> (Immich's search should not have returned it: ...)` name a video that was not downloaded.
- `N videos that didn't fit your earlier rules are checked again ...` is normal after a rules change, and returns on later runs until the search has reached each of them.
- The re-upload guard prints only when it finds a converted original in Immich again.

Report the summary to the user: `Downloaded and checked X videos: Y queued, Z don't fit your rules (<reasons>).`, `Not downloaded: ...` when present, `Immich has no more videos that fit your rules.` when a limit was not reached, and the counts line that ends discovery (`Queued: 3. Already queued: 8. Held: 1. Ready to finalise: 2.`). Already queued, held and ready to finalise count every open Immich video, not only those the search reached.

## Recovery

Match the job's `status` and `failureCategory` from `queue list --json` or `history --json`:

| What you see | Meaning | Action |
| --- | --- | --- |
| A `<file> is left for a later run: it started on ...` line | The job started in the other mode, and `--local` or `--remote` chose this one | Nothing is lost. The line ends with the command that resumes it. That is the plain `run` when the config matches where the video started, `run --local` or `run --remote` when the config uses the other mode, and neither when the config points at another worker: the line then says to set `processing.remoteSsh` back to the worker it started on (or `workingDirectory` back, when only that differs), or to `queue drop` the video. A remote resume is gated as paid encoding. |
| `Interrupted` | Stopped by `Ctrl+C`, a crash or a lost connection. A remote CRF search or encode keeps running on the worker | Run the same command again (`run` or `immich run`). It resumes, following a remote stage that is still running or using its result. |
| `RemoteUnavailable` | The remote worker could not be reached. The run stopped at this job and left the rest `Queued`; `run` and `immich run` then print `Stopped: the worker <name> can't be reached, ...` | Run the `To resume:` command it printed (`run`, also after an `immich run`). A Hetzner worker stays up for the resume, billed hourly. Its `To delete it instead:` command is gated; the line names the work that deleting loses. |
| `Skipped`, `NotWorthConverting` | No CRF met both `quality.minimumVmaf` and the video's size cap: the stricter of `quality.maxEncodedPercent` and `savings.minimumBytesSaved`. A video too small to save `savings.minimumBytesSaved` is skipped before its CRF search, with nothing uploaded. A normal outcome, not a failure | Report `failureMessage`. It names the setting that ruled the video out and the best CRF found, or, when even `quality.crfSearch.minCrf` missed the VMAF, that CRF's score. Discovery checks the video again once the file, those quality settings or `savings.minimumBytesSaved` change. |
| `ValidationFailed` | The AV1 failed a check, such as saving less than `savings.minimumBytesSaved` when the encode came out larger than the search predicted | Report it. The original stays as it is, and discovery won't queue it again until the file changes. |
| `StagingFailed`, `CrfSearchFailed`, `EncodeFailed`, `DownloadFailed`, `ProcessingFailed` | A processing step failed | Report `failureMessage`. Run `queue retry <id>` only if the user asks, once the cause is fixed. |
| `RemoteConfigurationChanged` | The job started under a different processing setup | Report it. `queue retry <id>` starts a fresh job under the current setup, if the user wants. |
| `Cancelled` | `queue drop` dropped it | Nothing. `queue retry <id>` queues it again, only if the user asks. |
| A `N Immich videos stay queued: <reason>.` line from `run` | `immich.baseUrl` isn't set, or Immich can't be used (unreachable, a missing or rejected key) to download a missing copy again. The run converted the other videos | Report the reason. Once the user has fixed it, `run` again converts them. |
| `Job <id> changed while this command was using it. Run the command again.` (exit `1`) | Another command, such as a run that started the video, changed the job first; its change stands | Check the job with `queue list --json`, then run the command again if it still applies. |
| `Ineligible`, `SourceNotReady`, `SourceOutsideWatchRoot`, `SourceChanged`, `SourceMissing` | The file did not qualify, changed, or was gone when the job ran | Nothing. The next `queue add` or `immich run` re-checks it. |
| A `Failed  <file>: <message>` line from `finalize` or `immich finalize`, with the job still `ReadyToFinalize` | A file is missing (the original, the AV1 or its validation record), the user must act in Immich first, or an Immich request failed partway. The original was not replaced | Pass the message to the user word for word; it names each copy left and the next command. Run a `queue drop` it names only at the user's request, after they have moved those copies: `cleanup` deletes a dropped job's files, and they may be the last ones. While they are still in place and the original is gone, `queue drop` refuses with exit `1` and names them. A message ending `again to resume.` needs only the gate and the rerun. |
| `FinalizationIncomplete` | An Immich finalisation stopped partway, or waits on the user | Show the user `failureMessage`. Once they have done any step it names in Immich, gate, then `'y' | video-optimiser immich finalize`. |
| `FinalizationFailed`, `ManualInterventionRequired` | Replacing a file original failed, or a cut-off one could not be shown to have left both files untouched | Stop. Show the user the job, its `sourcePath` and `outputPath`, and let them decide. `cleanup` keeps the AV1 of a `ManualInterventionRequired` job. |

**Failed, dropped or skipped jobs** stay settled: discovery skips them until the original changes, and a skipped one also once the quality settings change. `queue retry <id>` queues one again; run it only at the user's request.

**Kept Hetzner worker.** A remote run that finishes deletes the worker when `deleteAfterRun` is `true`, failed videos included; with `false`, or when the run never used a worker kept from an earlier one, it ends with `The worker <name> stays up, billed hourly, until it is deleted.` A worker that stops answering, an error once the worker exists, and a run stopped with `Ctrl+C` under redirected input keep it too, and say so with the `worker delete` command. After a stop now or a killed command, a CRF search or encode keeps running on it: the rerun picks it up. Interactively, `Ctrl+C` asks the user whether to delete the worker; under redirected input it never asks. `status` and `worker status` show a kept worker, whatever `processing.mode` says; both check it against Hetzner without waiting for a run that is creating one (`worker status` says `Hetzner wasn't checked: <reason>.` when it can't ask), and `status` shows what it bills per hour. `doctor` also checks the Hetzner token and API whenever `remoteSsh.lifecycle` is `"hetzner"`, in any mode, and warns (`ManagedWorkerBilling`) about a worker left up. `worker create` and `worker delete` print `Waiting for another video-optimiser command that is using the worker; Ctrl+C stops waiting.` while a run holds the worker; `Ctrl+C` in `worker create` after the server exists exits `9` with the two `stays up` lines. Tell the user it bills; deleting it is gated, because outputs not yet downloaded are lost with it.

## Reference

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success, or a confirmation answered "no" |
| `1` | General failure, including a `doctor` failure outside configuration and dependencies, `calibrate` on a database an older version wrote, a run or finalise refused because another one is using the database (`Another run is using this database: ...`), a queue verb refused (the video is in a state it does not apply to, converting now, or being finalised; or a drop would leave `cleanup` the only copy of a video), and a command that lost a race with another for the same job |
| `2` | Invalid arguments, including a malformed or unknown job ID for `queue hold`, `release`, `next` and `drop`, a removed command name, which says what replaced it (`'queue run' is now 'run'.`; also `queue discover`, `queue cancel` and `process`), or `queue retry` on a job it cannot retry (not found, not failed, dropped or skipped, or the original already has an open job) |
| `3` | Invalid configuration, including `worker create` or `worker delete` without `remoteSsh.lifecycle: "hetzner"`, or an Immich API key that is missing, rejected or lacks a permission |
| `4` | A failed dependency check in `doctor`: a media tool, the Immich server (unreachable, not Immich 3, its key, or its trash off) or the Hetzner API. Also `remote setup` when it cannot run `ssh-keygen` or list or upload the Hetzner key |
| `5` | `queue add <file>` refused the file: outside the watch roots or the rules (the line gives the `--force` command), too small to save `savings.minimumBytesSaved`, or a file video-optimiser made or archived (`--force` lifts neither) |
| `6` | Processing failed and nothing is ready; `queue add <file>` on a file that is missing or that it or ffprobe cannot read; an Immich request failed; ffprobe could not read a file; Hetzner had no server capacity; `validate` or `finalize <job-id>` named a job that does not exist; or `validate` named a job that is not ready to finalise, or whose original or AV1 is missing (the job stays as it was) |
| `7` | The output failed validation, including an output ffprobe cannot read (`validate`, `run`), or `finalize <job-id>` named a job that is not ready |
| `8` | `finalize <job-id>` failed, or `immich finalize` failed for every job |
| `9` | Cancelled with `Ctrl+C` |
| `10` | Partial success: some jobs failed (any failure in `finalize --ready`), or `queue add`, `queue add immich`, `immich run`, `cleanup` or `calibrate` hit issues (`queue add` counts each `Issue:` line it prints); read the output |

### Job statuses

- **Open** (`queue list`): `Queued`, `Staging`, `CrfSearching`, `Encoding`, `Downloading`, `Validating`, `ReadyToFinalize`, `Finalizing`, `Interrupted`.
- **Finished** (`history`): `Completed`, `Failed`, `Cancelled`, `Skipped`.

A held job (`queue hold`) keeps its status, `Queued` or `Interrupted`; runs pass over it until `queue release` or `queue next`. `Staging` and `Downloading` are transfers to and from a remote worker. A job whose finalisation was cut off (`Finalizing`, or `Interrupted` after it) is finalised like a `ReadyToFinalize` one, and `finalize --ready` lists it. An Immich one continues where it stopped. A file one continues only if its original and AV1 are where they started and the original is unchanged, and fails as `ManualInterventionRequired` otherwise; `run` leaves it alone and says so. `Ctrl+C` during `finalize` leaves the job ready.

### JSON output

`history --json` and `queue list --json` print a flat array of jobs; the human `queue list` groups them instead (converting now, the queue in run order, held, ready to finalise) and leaves out asset IDs and failure categories:

| Field | Value |
| --- | --- |
| `id` | Job ID, 32 hex characters; commands take it as `<job-id>` |
| `status` | A job status above |
| `held` | `true` while `queue hold` keeps runs from taking the job |
| `sourcePath` | The original; for Immich jobs, its staged copy |
| `source` | `file` or `immich` |
| `immichAssetId`, `immichNewAssetId` | The original's and the AV1's Immich asset IDs, or `null` |
| `crf` | The CRF chosen by the search, or `null` |
| `outputPath` | The temporary AV1, or `null` before encoding starts; the file exists once the job reaches `Validating` |
| `percentageSaved` | Size saving in percent, or `null` |
| `failureCategory`, `failureMessage` | Why the job failed or was skipped, or what it needs, or `null` |
| `updatedUtc` | Last change, ISO 8601 |

`status --json` prints the card as one object, with every field present and `null` where it has no value:

| Field | Value |
| --- | --- |
| `queue` | `{ converting, waiting, stopped, held, readyToFinalise }`: counts of the open videos in the groups `queue list` shows. `waiting` and `stopped` are the ones a run takes. |
| `run` | `null` when no run is going. Otherwise `{ command, startedUtc, processId, converting }`: `command` is how the run named itself, and `converting` is an array of `{ id, file, status }`. |
| `worker` | `null` unless `processing.remoteSsh.lifecycle` is `hetzner`. Otherwise `{ exists, hetznerChecked, notCheckedReason, name, serverId, status, serverType, location, createdUtc, uptimeSeconds, hourlyGrossEur }`. `exists` is `true` for a server, a retained IPv4 address (`status` `server-missing-primary-ip-retained`) or a worker recorded on this PC; the server fields are `null` when none exists. `hetznerChecked` is `false` with `notCheckedReason` set when Hetzner wasn't asked or didn't answer within 15 seconds; the fields are then the recorded ones (`status` `recorded`) with no uptime or price. `hourlyGrossEur` is EUR per hour including VAT. |
| `next` | Up to three commands worth running now, in order, as the text card prints them. |

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
| `noSuggestions` | `null` when `suggestions` has entries; otherwise `{ reason, text }`. `reason` is `noVideos`, `allAlreadyAv1`, `noResultsAllAdmitted`, `noResultsNothingTestable`, `tooFewResults` or `resultsNeedNoChange`; `text` is the sentence the console prints |

If Immich cannot be searched, `calibrate` still prints the report for the watch folders (the JSON too), then writes `Immich couldn't be searched, so its videos are not in this report: ...` to stderr and exits with the code of any Immich failure (`3` for an API key problem, `6` otherwise). Treat that report as partial.

`history` is `null` when nothing was tried, or `{ converted, convertedBitrateMbps, savedPercent, notWorthIt, notWorthItBitrateMbps }` with each range `{ min, max }` or `null`. It counts only completed and skipped jobs whose quality settings match the current config.
