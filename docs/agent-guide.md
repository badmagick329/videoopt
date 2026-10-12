# VideoOptimiser guide for agents

You are operating VideoOptimiser for a user. It is a Windows CLI that converts videos to AV1 and replaces an original only when told to. This guide gives the workflow, the commands that need the user's go-ahead, and how to read results. For exact syntax, run `video-optimiser --help`; it is the source of truth.

## The gate

Originals change only in `finalize`, `finalize --ready` and `immich finalize`. Every other step works on temporary copies. A **gate** is a point where you stop, show the user what will happen, and wait for their explicit go-ahead in this conversation before running the command. Pass each gate once per action: approval to finalise one batch does not cover the next.

| Class | Commands | What to do |
| --- | --- | --- |
| Read-only | `doctor`, `status`, `history`, `queue list`, `config show`, `config validate`, `version`, `worker status`, `cleanup --dry-run`, `process --dry-run` | Run freely. Add `--json` where offered. `doctor` may create the job database and the Immich staging folder. |
| Working | `queue discover`, `queue run`, `process`, `immich run`, `validate`, `queue cancel`, `queue retry` | Run when the user's request covers it. They change only the job database and temporary files. The one exception: `immich run`'s re-upload guard moves converted originals the phone uploaded again back to Immich's trash, which is the intended outcome. |
| Gated | `finalize`, `immich finalize`, `cleanup`, `worker create`, `worker delete` | Gate first. They replace originals, delete files, or create or delete a paid server. |

**Paid encoding.** When the config has `processing.mode: "remoteSsh"` and `remoteSsh.lifecycle: "hetzner"`, a `queue run`, `process` or `immich run` with work to do creates a Hetzner server that bills by the hour. Gate the first such run in a session: say it will create a server, name the `remoteSsh.hetzner.serverTypes` it may pick from, and state the `maxHourlyPrice` cap (or that there is none, when it is `null`).

**Prompts.** `finalize --ready`, `immich finalize` and `cleanup` ask `[y/N]` on standard input. After the user approves, pipe the answer: `'y' | video-optimiser finalize --ready`. With no input, the command cancels and exits `0`. `finalize <job-id>` does not prompt, so gate it all the same. With redirected input, a failed Hetzner run never asks whether to delete the worker; it keeps it.

**Secrets.** `.env` beside the config holds `VIDEO_OPTIMISER_HETZNER_TOKEN` and `VIDEO_OPTIMISER_IMMICH_API_KEY`. Check that a key exists by reading its name only, for example `(Get-Content .env) -replace '=.*'`. Keep values out of your output, commands and files.

## Setup

1. Find the config. The app uses `--config <path>`, then `VIDEO_OPTIMISER_CONFIG`, then `%ProgramData%\VideoOptimiser\config.yaml`, then `video-optimiser.yaml` in the current folder. `config show` prints the one in use. Pass `--config` on every command when more than one may exist.
2. Run `doctor --json`. Done when no diagnostic has `"status": "Fail"`. Each failure's `message` says what to fix. Configuration fixes are the user's to choose: propose the YAML change, then apply it once they agree.

## Workflow: folders

1. **Queue.** `queue discover`. On a user's first run, add `--first` to queue one video. Done when it prints `Queued: N`. `N` is `0` when nothing new matches the eligibility rules; report that and stop. The starter rules match only large H.264 files at 1080p and above, so phone footage (HEVC) needs a rule change first.
2. **Convert.** `queue run`. Done when it exits. A video can take an hour or more on a PC; run it in the background if your harness allows, and check progress with `status --json`. Exit `0`: every job is ready. Exit `10`: some failed. Exit `6`: all failed.
3. **Report.** From `status --json`, list each `ReadyToFinalize` job's `sourcePath`, `outputPath` and `percentageSaved`, and each failed job's `failureCategory` and `failureMessage`. Ask the user to watch the outputs.
4. **Gate.** Say what `original.action` will do to each original: `delete`, `archive` or `keep`.
5. **Finalise.** After the go-ahead, `'y' | video-optimiser finalize --ready`. It prints `<job-id>: <message>` per job. Done at exit `0`; exit `10` means at least one failed, so check `status --json`.

## Workflow: Immich

Prerequisites: `immich.baseUrl` is set and `doctor` passes its Immich checks. Read [immich.md](immich.md) once for the reasons behind the age rule.

1. **Phone.** Ask the user whether they have run the Immich app's "Free up space" since the last round, with a cutoff no older than `immich.minimumAgeDays`, and emptied the phone's trash. Only the user can do this. Wait for their answer.
2. **Convert.** `immich run`, with `--first` on a user's first round. Done when it exits. If it lists re-uploaded originals, pass that list to the user exactly as printed: they must delete those files from the phone. Exit `10` also covers guard or search issues; quote those lines.
3. **Report.** As in the folders workflow; filter `status --json` to `"source": "immich"`. Each `outputPath` is under `immich.stagingDirectory`.
4. **Gate.** Each AV1 is uploaded as a new Immich asset and each original goes to Immich's trash.
5. **Finalise.** After the go-ahead, `'y' | video-optimiser immich finalize`. Done when it prints `Finalised: N. Failed: 0.` and exits `0`. If it stops partway, running it again continues; that rerun needs the gate too.

`finalize --ready` also finalises ready Immich jobs. Use `immich finalize` for Immich jobs, so the user sees capture dates before the prompt.

## Recovery

Match the job's `status` and `failureCategory` from `status --json` or `history --json`:

| What you see | Meaning | Action |
| --- | --- | --- |
| `Interrupted` | Stopped by `Ctrl+C`, a crash or a lost connection | Run the same command again (`queue run`, `immich run` or `process`). It resumes. |
| `RemoteUnavailable` | The remote worker could not be reached | Run the command again. A Hetzner worker is kept for the resume. |
| `ValidationFailed` | The AV1 failed a check, usually too little saving | Report it. The original stays as it is, and discovery won't queue it again until the file changes. |
| `StagingFailed`, `CrfSearchFailed`, `EncodeFailed`, `DownloadFailed`, `ProcessingFailed` | A processing step failed | Report `failureMessage`. Run `queue retry <id>` only if the user asks, once the cause is fixed. |
| `RemoteConfigurationChanged` | The job started under a different processing setup | Report it. `queue retry <id>` starts a fresh job under the current setup, if the user wants. |
| `Ineligible`, `SourceNotReady`, `SourceOutsideWatchRoot`, `SourceChanged` | The file did not qualify, or changed, when the job ran | Nothing. The next `queue discover` re-checks it. |
| `FinalizationIncomplete` | An Immich finalisation stopped partway | Gate, then `'y' | video-optimiser immich finalize`. |
| `FinalizationFailed`, `ManualInterventionRequired` | Replacing a file original failed or was cut off | Stop. Show the user the job, its `sourcePath` and `outputPath`, and let them decide. |

**Failed or cancelled jobs** stay settled: discovery skips them until the original changes. `queue retry <id>` queues one again; run it only at the user's request.

**Retained Hetzner worker.** After a remote run with any failed or interrupted job, the server stays up so the work can resume, and it keeps billing. With `deleteAfterRun: false` it stays after every run. `worker status` shows it. Tell the user; deleting it is gated, because outputs not yet downloaded are lost with it.

## Reference

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success, or a confirmation answered "no" |
| `1` | General failure, including a `doctor` failure outside configuration and dependencies |
| `2` | Invalid arguments |
| `3` | Invalid configuration, or an Immich API key that is missing, rejected or lacks a permission |
| `4` | Missing or unusable media tool (`doctor`) |
| `5` | `process` on a file the rules exclude; add `--force` to convert it anyway |
| `6` | Processing failed for every job, or an Immich request failed |
| `7` | The output failed validation (`validate`, `process`), or `finalize <job-id>` named a job that is not ready |
| `8` | `finalize <job-id>` failed, or `immich finalize` failed for every job |
| `9` | Cancelled with `Ctrl+C` |
| `10` | Partial success: some jobs failed (any failure in `finalize --ready`), or `queue discover`, `immich run` or `cleanup` hit issues; read the output |

### Job statuses

- **Open** (`status`, `queue list`): `Queued`, `Staging`, `CrfSearching`, `Encoding`, `Downloading`, `Validating`, `ReadyToFinalize`, `Finalizing`, `Interrupted`.
- **Finished** (`history`): `Completed`, `Failed`, `Cancelled`.

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
| `failureCategory`, `failureMessage` | Why the job failed or what it needs, or `null` |
| `updatedUtc` | Last change, ISO 8601 |

`doctor --json` prints `configurationPath`, `exitCode` and `diagnostics`, an array of `{ category, status, code, message }` where `status` is `Pass`, `Warning` or `Fail`.
