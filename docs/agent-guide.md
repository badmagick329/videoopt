# Explaining VideoOptimiser to a person

A person has asked you about VideoOptimiser, a Windows CLI that converts videos to AV1 and replaces an original only when told to. The person runs every command; you explain. Say what a command does and changes, **warn** before the ones that replace originals, delete files or bill money, read pasted output for them, and name the next command. For exact syntax, point them to `video-optimiser --help` and `video-optimiser <command> --help`; that is the source of truth. Show commands as text for them to run, and ask them to paste the output when you need it.

## What each command changes

Originals change only in `finalize`, `finalize --ready` and `immich finalize`. Every other command works on temporary copies, the job database and, for Immich, a staging folder.

| Class | Commands | What you tell the person |
| --- | --- | --- |
| Read-only | `status` (or no command), `history`, `queue list`, `calibrate`, `config show`, `config validate`, `version`, `worker status`, `cleanup --dry-run`, `doctor` | Safe to run. `--json` is offered on the ones that list. `doctor` may create the job database and the Immich staging folder. |
| Job database and temporary files | `config init`, `queue add`, `queue add immich`, `run`, `immich run`, `run stop`, `validate`, `queue hold`, `queue release`, `queue next`, `queue drop`, `queue retry` | Nothing of theirs is lost. `config init` writes a new config and never replaces one. `queue add immich` and `immich run` download videos to the staging folder, so they need disk space and bandwidth. Their re-upload guard moves converted originals that the phone uploaded again back to Immich's trash, which is intended. |
| **Warn** | `finalize`, `immich finalize`, `cleanup`, `worker create`, `worker delete`, `remote setup` | Say what it does first. They replace originals, delete files, create or delete a paid server, or upload an SSH key to Hetzner and rewrite the config. |

What to say for each **warn** command:

- `finalize`, `finalize --ready`: each ready AV1 takes its original's place. `original.action` decides the original: `delete`, `archive` (moved to `original.archiveDirectory`) or `keep` (AV1 installed beside it as `<name>.av1<ext>`). The prompt states which. Suggest playing the AV1s first (`queue list` shows each one's saving; `outputPath` in `queue list --json` is the file). `finalize <job-id>` asks nothing, so warn all the same.
- `immich finalize`: each AV1 is uploaded as a new Immich asset and each original goes to Immich's trash. A rerun after a stop continues where it stopped.
- `cleanup`: lists abandoned temporary outputs and downloaded Immich copies, then deletes them after `y`. `cleanup --dry-run` only lists and ends `Dry run: nothing was deleted.` A file it leaves alone prints `Kept  <path> (<reason>)`, such as an AV1 whose original is missing and which may be the only copy. Relay those lines; the person should not delete such a file by hand.
- `worker create`, `worker delete`: create or delete the Hetzner worker. Deleting loses outputs not yet downloaded.
- `queue drop`: keeps files; `cleanup` deletes them later. It refuses (exit `1`) when `cleanup` would then delete the only copy of a video.
- `remote setup`: uploads an SSH key to the person's Hetzner project before it asks anything. Without `y` it prints the `processing:` section it would write and changes no file; a rerun reuses the key, rewrites only `processing:` and keeps the old file as `<config>.bak`.

**Prompts.** `finalize --ready`, `immich finalize`, `cleanup`, `remote setup`, and `run`, `immich run` or `worker create` when they would create a Hetzner server ask `[y/N]`. Anything but `y` cancels, changes nothing and exits `0`.

**Secrets.** `.env` beside the config holds `VIDEO_OPTIMISER_HETZNER_TOKEN` and `VIDEO_OPTIMISER_IMMICH_API_KEY`. The person adds them there. Do not ask for a value or accept one pasted into chat; checking that a key name exists is enough.

## Paid encoding

With `processing.mode: "remoteSsh"` and `remoteSsh.lifecycle: "hetzner"`, a run that needs a worker asks before it creates one, and the server bills by the hour:

```text
Will create cpx42 in hel1 at EUR 0.134/h incl. VAT.
If that placement fails, a fallback may cost up to EUR 0.266/h (ccx33 in hel1).
Create the worker? [y/N]
```

- `y` covers the fallbacks too, up to the second line's price. `N` or Enter creates nothing, prints `No worker was created.`, exits `0` and leaves the jobs queued.
- A run that reuses a running worker never asks; it is already billing.
- A new worker prints `Bootstrapping the worker: ... takes several minutes.`, then `Bootstrapping: P%, <stage>...`, then `Worker ready after <duration>.`. `Still waiting for the worker's SSH (...)` appears only after 3 minutes. It is still waiting, not failing; the run stops by itself at `hetzner.bootstrapTimeout`.
- `--local` and `--remote` on `run` and `immich run` set the mode for that run only. `--local` encodes free on this PC. Tell the person to use the flag, not to edit `processing.mode`.
- The first line of a run, `Encoding on ...`, and `doctor`'s `EncodingLocation` say where encoding runs. Have the person read it before the run goes on; if it differs from what they expect, they stop it with `Ctrl+C`.

### The worker question

A worker left up keeps billing. When the person presses `Ctrl+C` during a run with a Hetzner worker up, the run asks `Delete the worker when the run stops? [y/N]` at once, while the video keeps converting. `y` deletes the worker after the video is done (they keep the window open until it says so); `N` keeps it. Stopping at once asks `Delete the worker now?` and names what deleting loses. Redirected input is never asked and keeps the worker.

A run that finishes follows `deleteAfterRun` alone, failed videos included. A kept worker ends the run with `The worker <name> stays up, billed hourly, until it is deleted.` and the `worker delete` command. `status` and `worker status` show a kept worker whatever `processing.mode` says; `doctor` warns (`ManagedWorkerBilling`) about one left up.

## Setup

1. The config is found by `--config <path>`, then `VIDEO_OPTIMISER_CONFIG`, then `%ProgramData%\VideoOptimiser\config.yaml`, then `video-optimiser.yaml` in the current folder. `config show` prints the one in use. If several may exist, suggest `--config` on every command.
2. With no config, `config init --config <path> --quality <level>`: `archive` (VMAF 97, indistinguishable from the original), `standard` (95, hard to tell apart; the default) or `compact` (93, small visible loss, bigger savings). It never overwrites a file.
3. `doctor` is done when no diagnostic has `Fail`; each `message` says what to fix. Config fixes are the person's to choose: propose the YAML. A config from an older version can fail with `Unknown setting '<path>' at line N, column M.`; the setting was removed, so propose deleting the line. The GitHub release notes list each one under **Breaking config change**.

## The rounds

Folders:

1. `queue add` queues eligible videos from the watch folders (`--first` queues one, for a first trial; `queue add <file>` queues one file, and `--force` skips the rules). It prints `Queued: N.` plus the other counts that are not zero (`Already queued`, `Held`, `Ready to finalise`, ...), and `Checked N files so far ...` every 10 seconds on a large folder, which is progress. The starter rules match only large H.264 files at 1080p to 1440p and at 4K or above, so phone footage (HEVC) needs a rule change first; `calibrate` shows what the rules leave out.
2. `run` converts every queued video, Immich ones included, and passes over held ones. A video can take an hour or more on a PC. The person can watch from a second window with `status` or `queue list`, and `queue hold`, `queue release`, `queue next` and `queue add` take effect from the run's next video.
3. They review the AV1s, then `finalize --ready` (**warn**).

Immich (read [immich.md](immich.md) once for the age rule behind step 1):

1. Ask whether they ran the Immich app's "Free up space" since the last round, with a cutoff no older than `immich.minimumAgeDays`, and emptied the phone's trash. Only they can.
2. `queue add immich [--first | --limit <n>]` downloads and checks videos and queues the ones that fit; `--limit <n>` adds `n` new videos, never counting those already queued. `immich run` is that, then `run`. Before a paid remote round, ask what size they want. If the guard lists re-uploaded originals, they must delete those files from the phone.
3. They review, then `immich finalize` (**warn**; it shows capture dates before the prompt). `finalize --ready` also finalises ready Immich videos.

A `doctor` failure `ImmichTrashDisabled`, or exit `3` with `Immich's trash is off`, means the person must turn Immich's trash back on.

Tuning the rules, when they ask why videos are not converted: `calibrate` writes nothing and downloads nothing. Per rule it gives `videos`, `bytes` and `history`; then the largest `keptOut` groups. Quote each suggestion's `text`; the person decides on rule changes. A `testOne` suggestion's `command` is a `queue add "<file>" --force`, which queues one file outside the rules; its `run` stops before replacing anything. `codecUnknown` groups are Immich videos no run has downloaded. A large `historyLeftOut.otherSettings` means conversions under other or unrecorded quality settings are not counted. Exit `1` with `... is from an older version of video-optimiser`: run `queue list` once, then `calibrate`.

## Reading output

### The status card

`status` prints the card, and so does a bare `video-optimiser` when its input or output is redirected; in a terminal a bare `video-optimiser` opens the full-screen TUI instead. The card is read-only and answers at once, even during a run. Example:

```text
Queue: 1 converting, 2 waiting, 1 held, 3 ready to finalise.
Run: video-optimiser run, started 14:02 (process 1234), converting clip.mp4.
Worker: video-optimiser-1 (cpx42 in hel1), up 1h 12m, billing EUR 0.134/h incl. VAT.
Next:
  video-optimiser finalize --ready   finalises the 3 ready videos; review them first
```

- `Queue:` counts open videos; `Queue: empty.` when none. `waiting` and `stopped` are what a run takes; `held` are passed over; `ready to finalise` are waiting for review.
- `Run: none.` or the running command (`background run` for one with no terminal), its start time and process ID, and what it converts.
- `Worker:` appears only with the Hetzner lifecycle: the server, its age and hourly price, `Worker: none.`, a server that is `gone from Hetzner, but its IPv4 address still bills`, or `as recorded on this PC; Hetzner wasn't checked: <reason>.` Any billing worker deserves a mention.
- `Next:` holds up to three commands in priority order. `worker delete` appears there whenever a worker bills and no run is going, so **warn** before the person follows it.
- With no config found, the bare command says so and names `config init`.

### `queue list`

It groups the open videos, each group only when it has videos: `Converting now`, `Queue, in run order` (numbered), `Held` and `Ready to finalise`. Each line starts with the job ID that `queue hold`, `release`, `next`, `drop`, `retry`, `validate` and `finalize` take. A stopped video says what the next run does with it, for example `Stopped  clip.mp4, CRF 30: picks up the encode on the worker, or encodes again on a new one`. Immich videos show `(Immich)`. `Nothing is queued or ready to finalise.` means none. `history` lists the ended ones.

### The run

One block per video, then a tally of non-zero outcomes (`Ready to finalise: 4. Not worth converting: 1.`, or `Nothing to finalise.`), a `Failed jobs:` list with plain phrases, and, when any video is ready, including ones earlier runs left, `N videos are ready to finalise: review them, then run 'video-optimiser finalize --ready'.` Held videos are counted first (`2 videos are held; runs pass over them until they're released.`).

```text
[2/5] clip.mp4 (1002.4 MiB)
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

Upload and download lines appear only with a remote worker. Colour appears on a terminal only; escape codes in pasted text mean they can set `NO_COLOR=1`. The last line is the outcome:

| Outcome line | Job status |
| --- | --- |
| `Ready to finalise: ...` | `ReadyToFinalize` |
| `Not worth converting: ...` | `Skipped` |
| `Failed validation: ...` | `Failed`, `ValidationFailed` |
| `Failed during the upload`, `the CRF search`, `encoding` or `the download: ...`, then `Full log: <path>` | `Failed`; the ab-av1 log stays at that path |
| `Worker unreachable: ...` | `Interrupted`, `RemoteUnavailable`; the run stops here |

**Slow or stuck.** A CRF search prints one `CRF` line per attempt, usually 4 to 7 attempts of about 2 minutes each for a 1 GB phone video on a Hetzner cpx42, longer on a PC. An encode prints progress about every 15 seconds; `about <time> left` appears only after 30 seconds and 5 percentage points. A job that prints nothing new for 15 minutes is stuck: ask for its last lines and `queue list --json`.

**`Ctrl+C`.** The first press stops after the video being converted (`Stopping after this video ...`), keeps its work, prints `Stopped after <file>; <N> videos are still waiting.` and `To convert the rest: video-optimiser run`, and exits `9`. A second press stops at once (`Stopped now. <file> continues from its last finished step on the next run.`). `run stop` does the same for a background run (one with no terminal; `status` shows `Run: background run, ...`): `run stop` is the first press, `run stop --now` the second. Its worker fate was chosen when it started, so it never asks. For a run in a terminal, `run stop` exits `1` and says to press `Ctrl+C` in its window; with no run going it prints `No run is going.` and exits `0`.

### Immich discovery lines

`queue add immich` and `immich run` download each candidate to check it, so they print one line per video before the first job block:

- `  n. <file>  <date>  <codec> <resolution> <bitrate>  <size>  doesn't fit: <reason>` is normal. The download is deleted, and the video is not checked again until the rules change. `queued: fits rule N` marks a keeper; rules number from 1 in `eligibility.rules` order.
- `issue: ...` is a failed checksum or probe, tried again next run. `Left alone: <file> (Immich's search should not have returned it: ...)` is a video that was not downloaded.
- `N videos that didn't fit your earlier rules are checked again ...` is normal after a rules change.
- The summary to relay: `Downloaded and checked X videos: Y queued, Z don't fit your rules (<reasons>).`, `Not downloaded: ...`, `Immich has no more videos that fit your rules.` when a limit was not reached, and the counts line (`Queued: 3. Already queued: 8. Held: 1. Ready to finalise: 2.`).
- `Downloading again: ...` in a run is a missing copy being fetched. `N Immich videos stay queued: <reason>.` means `immich.baseUrl` isn't set or Immich can't be used; the run converted the rest.

## When something fails

Match the job's `status` and `failureCategory` (from `queue list --json` or `history --json`; the console shows plain phrases):

| What they see | Meaning | Suggest |
| --- | --- | --- |
| `<file> is left for a later run: it started on ...` | The job started in the other mode, and `--local` or `--remote` chose this one | Nothing is lost. The line ends with the resume command: plain `run`, `run --local` or `run --remote`; or, when the config points at another worker, set `processing.remoteSsh` back (or `workingDirectory`), or `queue drop` the video. A remote resume is paid encoding. |
| `Interrupted` | Stopped by `Ctrl+C`, a crash or a lost connection. A remote stage keeps running on the worker | Run `run` again. It resumes and follows a running remote stage. |
| `RemoteUnavailable` | The worker could not be reached. The run stopped at this job and left the rest `Queued`, printing `Stopped: the worker <name> can't be reached, ...` | The `To resume:` command it printed (`run`). A Hetzner worker stays up for it, billed hourly. Its `To delete it instead:` command is a **warn**, and the line names the work deleting loses. |
| `Skipped`, `NotWorthConverting` | No CRF met both `quality.minimumVmaf` and the video's size cap, the stricter of `quality.maxEncodedPercent` and `savings.minimumBytesSaved`. A video too small to save `savings.minimumBytesSaved` is skipped before its search, with nothing uploaded. Not a failure | Relay `failureMessage`: the setting that ruled it out and the best CRF found. Discovery checks the video again when the file or those settings change. |
| `ValidationFailed` | The AV1 failed a check, for example it saved less than `savings.minimumBytesSaved` | The original is untouched. Discovery won't queue it again until the file changes. |
| `StagingFailed`, `CrfSearchFailed`, `EncodeFailed`, `DownloadFailed`, `ProcessingFailed` | A step failed | Relay `failureMessage`. After the cause is fixed, `queue retry <id>` queues it again. |
| `RemoteConfigurationChanged` | The job started under a different processing setup | `queue retry <id>` starts a fresh job under the current setup. |
| `Cancelled` (shown as Dropped) | `queue drop` dropped it | `queue retry <id>` queues it again. Dropping a video whose remote stage was stopped prints `Its encode may still be running on the worker; the next run on that worker stops it.` (or `Its CRF search ...`): it keeps billing until a later run reaches that worker, or the worker is deleted. |
| `Job <id> changed while this command was using it. Run the command again.` (exit `1`) | Another command changed the job first; its change stands | Check `queue list`, then run the command again if it still applies. |
| `Another run is using this database: ...` (exit `1`) | One run at a time | Wait for it, or check `status`. |
| `Ineligible`, `SourceNotReady`, `SourceOutsideWatchRoot`, `SourceChanged`, `SourceMissing` | The file did not qualify, changed, or was gone when the job ran | Nothing. The next `queue add` re-checks it. |
| `Failed  <file>: <message>` from `finalize` or `immich finalize`, job still `ReadyToFinalize` | A file is missing (original, AV1 or validation record), the person must act in Immich first, or an Immich request failed partway. The original was not replaced | Relay the message word for word; it names each copy left and the next command. `queue drop` only after they have moved those copies: `cleanup` deletes a dropped job's files, and they may be the last ones. A message ending `again to resume.` needs only a rerun. |
| `FinalizationIncomplete` | An Immich finalisation stopped partway, or waits on the person | Relay `failureMessage`. After they do any Immich step it names, `immich finalize` again. |
| `FinalizationFailed`, `ManualInterventionRequired` | Replacing a file original failed, or a cut-off one could not be shown to have left both files untouched | Stop. Have them look at `sourcePath` and `outputPath` and decide. `cleanup` keeps that AV1. `finalize <id>` (not `--ready`) takes back a job that failed this way with nothing moved. |

Failed, dropped and skipped jobs stay settled: discovery skips them until the original changes, and a skipped one also once the quality settings change. `queue retry <id>` is the way back.

## Reference

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success, or a confirmation answered "no" |
| `1` | General failure, including a `doctor` failure outside configuration and dependencies, `calibrate` on a database an older version wrote, a run or finalise refused because another is using the database, `run stop` on a run started in a terminal, a queue verb refused (the video is in a state it does not apply to, converting now, or being finalised; or a drop would leave `cleanup` the only copy), and a command that lost a race for the same job |
| `2` | Invalid arguments, including a malformed or unknown job ID for `queue hold`, `release`, `next` and `drop`, a removed command name, which says what replaced it (`'queue run' is now 'run'.`; also `queue discover`, `queue cancel` and `process`), or `queue retry` on a job it cannot retry (not found, not failed, dropped or skipped, or the original already has an open job) |
| `3` | Invalid configuration, including `queue add` with no watch roots, `worker create` or `worker delete` without `remoteSsh.lifecycle: "hetzner"`, or an Immich API key that is missing, rejected or lacks a permission |
| `4` | A failed dependency check in `doctor`: a media tool, the Immich server (unreachable, not Immich 3, its key, or its trash off) or the Hetzner API. Also `remote setup` when it cannot run `ssh-keygen` or list or upload the Hetzner key |
| `5` | `queue add <file>` refused the file: outside the watch roots or the rules (the line gives the `--force` command), too small to save `savings.minimumBytesSaved`, or a file video-optimiser made or archived (`--force` lifts neither) |
| `6` | Processing failed and nothing is ready; `queue add <file>` on a file that is missing or unreadable; an Immich request failed; ffprobe could not read a file; Hetzner had no server capacity; `validate` or `finalize <job-id>` named a job that does not exist; or `validate` named a job that is not ready to finalise, is held (`queue release` first), or whose original, AV1 or validation record is missing or unreadable |
| `7` | The output failed validation, including an output ffprobe cannot read (`validate`, `run`), or `finalize <job-id>` named a job that is not ready |
| `8` | `finalize <job-id>` failed, or `immich finalize` failed for every job |
| `9` | Stopped with `Ctrl+C` |
| `10` | Partial success: `run` or `immich run` with some failed and some ready, any failure in `finalize --ready`, or `queue add`, `queue add immich`, `immich run`, `cleanup` or `calibrate` hit issues; read the output |

`run` and `immich run` exit `0` when no job failed, `10` when some failed and some are ready, `6` when some failed and none is ready.

### Job statuses

The console uses plain words; `--json` keeps the enum names.

- **Open** (`queue list`): `Queued` (waiting), `Staging` (uploading), `CrfSearching` (finding CRF), `Encoding`, `Downloading`, `Validating` (checking), `ReadyToFinalize`, `Finalizing`, `Interrupted` (stopped).
- **Finished** (`history`): `Completed` (finalised), `Failed`, `Cancelled` (dropped), `Skipped` (not worth converting).

A held job keeps its status, `Queued` or `Interrupted`; runs pass over it until `queue release` or `queue next`. `Staging` and `Downloading` are transfers to and from a worker. A job whose finalisation was cut off (`Finalizing`, or `Interrupted` after it) is finalised like a `ReadyToFinalize` one. An Immich one continues where it stopped. A file one continues only if its original and AV1 are where they started and the original is unchanged, and fails as `ManualInterventionRequired` otherwise; `run` leaves it alone and says so. `Ctrl+C` during `finalize` leaves the job ready.

### JSON output

`history --json` and `queue list --json` print a flat array of jobs:

| Field | Value |
| --- | --- |
| `id` | Job ID, 32 hex characters |
| `status` | A job status above |
| `held` | `true` while `queue hold` keeps runs from taking the job |
| `sourcePath` | The original; for Immich jobs, its downloaded copy |
| `source` | `file` or `immich` |
| `immichAssetId`, `immichNewAssetId` | The original's and the AV1's Immich asset IDs, or `null` |
| `crf` | The CRF chosen by the search, or `null` |
| `outputPath` | The temporary AV1, or `null` before encoding starts; the file exists once the job reaches `Validating` |
| `percentageSaved` | Size saving in percent, or `null` |
| `failureCategory`, `failureMessage` | Why the job failed or was skipped, or what it needs, or `null` |
| `updatedUtc` | Last change, ISO 8601 |

`status --json` prints the card as one object; every field is present, `null` where it has no value:

| Field | Value |
| --- | --- |
| `queue` | `{ converting, waiting, stopped, held, readyToFinalise }` |
| `run` | `null` when no run is going. Otherwise `{ command, startedUtc, processId, background, converting }`; `background` is true for a run with no terminal; `converting` is an array of `{ id, file, status }`. |
| `worker` | `null` unless `processing.remoteSsh.lifecycle` is `hetzner`. Otherwise `{ exists, hetznerChecked, notCheckedReason, name, serverId, status, serverType, location, createdUtc, uptimeSeconds, hourlyGrossEur }`. `exists` is `true` for a server, a retained IPv4 address (`status` `server-missing-primary-ip-retained`) or a worker recorded on this PC. `hetznerChecked` is `false` with `notCheckedReason` set when Hetzner wasn't asked or didn't answer within 15 seconds; the fields are then the recorded ones (`status` `recorded`) with no uptime or price. `hourlyGrossEur` includes VAT. |
| `next` | Up to three commands worth running now, in order |

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
| `noSuggestions` | `null` when `suggestions` has entries; otherwise `{ reason, text }`. `reason` is `noVideos`, `allAlreadyAv1`, `noResultsAllAdmitted`, `noResultsNothingTestable`, `tooFewResults` or `resultsNeedNoChange` |

A rule's or group's `history` is `null` when nothing was tried, or `{ converted, convertedBitrateMbps, savedPercent, notWorthIt, notWorthItBitrateMbps }` with each range `{ min, max }` or `null`; it counts only completed and skipped jobs whose quality settings match the current config.

If Immich cannot be searched, `calibrate` still prints the report for the watch folders (the JSON too), then writes `Immich couldn't be searched, so its videos are not in this report: ...` to stderr and exits with the Immich failure's code (`3` for an API key problem, `6` otherwise). Treat that report as partial.
