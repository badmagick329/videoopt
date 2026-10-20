# Explaining VideoOptimiser to a person

A person has asked you about VideoOptimiser, a Windows CLI that converts videos to AV1 and replaces an original only when told to. The person runs every command; you explain. Say what a command does and changes, **warn** before the ones that replace originals, delete files or bill money, read pasted output for them, and name the next command. For exact syntax, point them to `video-optimiser --help` and `video-optimiser <command> --help`; that is the source of truth. Show commands as text for them to run, and ask them to paste the output when you need it.

## What each command changes

Originals change only in `finalize`, `finalize --ready` and `immich finalize`. Every other command works on temporary copies, the job database and, for Immich, a staging folder.

| Class | Commands | What you tell the person |
| --- | --- | --- |
| Read-only | `status` (or no command with `--json` or with output redirected; otherwise it opens the [full-screen view](#the-full-screen-view), whose keys act), `history`, `queue list`, `calibrate`, `cost`, `config show`, `config validate`, `version`, `worker status`, `cleanup --dry-run`, `doctor` | Safe to run. `--json` is offered on the ones that list. `doctor` may create the job database and the Immich staging folder. |
| Job database and temporary files | `config init`, `queue add`, `queue add immich`, `run`, `immich run`, `run stop`, `run stop --undo`, `validate`, `queue hold`, `queue release`, `queue next`, `queue drop`, `queue retry`, `queue dismiss` | Nothing of theirs is lost. `run` and `immich run` **warn** when the config uses Hetzner: they may create a paid server ([Paid encoding](#paid-encoding)). `validate` deletes an AV1 it finds not worth converting; the original stays. `config init` writes a new config and never replaces one. `queue add immich` and `immich run` download videos to the staging folder, so they need disk space and bandwidth. Their re-upload guard moves converted originals that the phone uploaded again back to Immich's trash, which is intended. |
| **Warn** | `finalize`, `immich finalize`, `queue reject`, `cleanup`, `worker create`, `worker delete`, `remote setup` | Say what it does first. They replace originals, delete files, create or delete a paid server, or upload an SSH key to Hetzner and rewrite the config. |

What to say for each **warn** command:

- `finalize`, `finalize --ready`: each ready AV1 takes its original's place. `original.action` decides the original: `delete`, `archive` (moved to `original.archiveDirectory`) or `keep` (AV1 installed beside it as `<name>.av1<ext>`). The prompt states which. Suggest playing the AV1s first (`queue list` shows each one's saving; `outputPath` in `queue list --json` is the file). `finalize <job-id>` on a video without warnings asks nothing, so warn all the same. Bulk finalise (`finalize --ready`, `immich finalize`, the view's `F`) leaves out every ready video with validation warnings and names each with its `finalize <job-id>`; the person watches each one and finalises it alone. `finalize <job-id>` on a warned video lists its warnings and asks `[y/N]`. A finalisation already under way (stopped partway) stays in bulk, warnings or not.
- `immich finalize`: each AV1 is uploaded as a new Immich asset and each original goes to Immich's trash. A rerun after a stop continues where it stopped.
- `cleanup`: lists abandoned temporary outputs and downloaded Immich copies, then deletes them after `y`. `cleanup --dry-run` only lists and ends `Dry run: nothing was deleted.` A file it leaves alone prints `Kept  <path> (<reason>)`, such as an AV1 whose original is missing and which may be the only copy. Relay those lines; the person should not delete such a file by hand.
- `worker create`, `worker delete`: create or delete the Hetzner worker. Deleting loses outputs not yet downloaded.
- `queue drop`, `queue drop --all` (every waiting, stopped and held video): keeps files; `cleanup` deletes them later. It refuses (exit `1`) when `cleanup` would then delete the only copy of a video.
- `queue reject`: for a video that is ready to finalise. It deletes the AV1 at once, and an Immich video's download too; the original stays. It refuses (exit `1`) when the AV1 may be the only copy, and for an Immich video whose finalisation stopped partway (`finalize` finishes that one). An AV1 that is open in a player stays and is reported; `cleanup` deletes it later. `queue retry <id>` undoes it.
- `remote setup`: creates its SSH key pair in `%USERPROFILE%\.ssh` when there is none and uploads the key to the person's Hetzner project, both before it asks anything. Without `y` it prints the `processing:` section it would write and leaves the config alone; a rerun reuses the key, rewrites only `processing:` and keeps the old file as `<config>.bak`.

**Prompts.** `finalize --ready`, `immich finalize`, `finalize <job-id>` on a warned video, `cleanup`, `remote setup`, and `run`, `immich run` or `worker create` when they would create a Hetzner server ask `[y/N]`. Anything but `y` cancels and exits `0`; the worker creation and `remote setup` also take `yes`. A cancel changes nothing, apart from the key `remote setup` already made and uploaded. `config init` without `--quality` asks the quality level instead (below).

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
- A new worker prints `Bootstrapping the worker: ... takes several minutes.`, then `Bootstrapping: P%, <stage>.`, then `Worker ready after <duration>.`. `Still waiting for the worker's SSH (...)` appears only after 3 minutes. It is still waiting, not failing; the run stops by itself at `hetzner.bootstrapTimeout` (exit `6`). A bootstrap script that fails stops the run within about 15 seconds: `The Hetzner worker <name> failed to bootstrap while <stage>: <cause>.`, then `The end of its cloud-init log:` and up to 5 indented log lines, exit `6`. Diagnose from those lines; the worker's fate follows [The worker question](#the-worker-question).
- `--local` and `--remote` on `run` and `immich run` set the mode for that run only. `--local` encodes free on this PC. Tell the person to use the flag, not to edit `processing.mode`.
- The first line of a run, `Encoding on ...`, and `doctor`'s `EncodingLocation` say where encoding runs. Have the person read it before the run goes on; if it differs from what they expect, they stop it with `Ctrl+C`.

### The worker question

A worker left up keeps billing. When the person presses `Ctrl+C` during a run with a Hetzner worker up, the run asks `Delete the worker when the run stops? [y/N]` at once, while the video keeps converting. `y` deletes the worker after the video is done (they keep the window open until it says so); `N` keeps it. Stopping at once asks `Delete the worker now?` and names what deleting loses. Redirected input is never asked and keeps the worker.

A run that finishes follows `deleteAfterRun` alone, failed videos included. A run that ends with an error keeps the worker, except one that never got ready: a bootstrap that failed or hit `bootstrapTimeout`, or a Hetzner error on a worker this run created. In a terminal the run then asks `Delete the worker now? [y/N]`; with redirected input it deletes the worker unasked. A background run deletes a worker whose bootstrap failed or timed out whatever fate it was started with; after a Hetzner error it applies that fate. A kept worker ends the run with `The worker <name> stays up, billed hourly, until it is deleted.` and the `worker delete` command. `status` and `worker status` show a kept worker whatever `processing.mode` says; `doctor` warns (`ManagedWorkerBilling`) about one left up.

### What the freed space cost

`cost` answers "what did the savings cost?" as EUR per TB freed (TB = 10^12 bytes), for all time and per run, newest first; `cost --json` adds each video. Once the config loads it always exits `0`, and it writes nothing apart from upgrading a job database an older version wrote; with no job database it prints the nothing-recorded line and creates none. Only Hetzner workers VideoOptimiser created count: runs on this PC or a manual SSH host are left out, and runs before this version were never recorded. `Nothing to show yet: ...` means no worker is recorded.

- Freed means finalised (`Completed`), archived and kept originals included. A ready video's saving is *waiting for review* and is not in the figure until it is finalised. Failed, skipped, rejected and dropped videos add their time and no bytes.
- The three levels do not add up, by design: a video pays only its own conversion time, a run pays every minute it overlapped its worker (bootstrap and gaps between videos), and all time is what Hetzner billed (each worker rounded up to whole hours).
- `-` in place of EUR per TB means nothing was freed yet, or the worker has no price. The lines `A worker is still up: ...` (its bill counts to now), `A worker was deleted outside video-optimiser, ...` (its end is the last time it was seen) and `Hetzner listed no price for a worker, ...` (its bill and videos are left out; its runs say `no price`) qualify the all-time figure.

## Setup

1. The config is found by `--config <path>`, then `VIDEO_OPTIMISER_CONFIG`, then `%ProgramData%\VideoOptimiser\config.yaml`, then `video-optimiser.yaml` in the current folder. `config show` prints the one in use. If several may exist, suggest `--config` on every command.
2. With no config, `config init --config <path> --quality <level>`: `archive` (VMAF 97, indistinguishable from the original), `standard` (95, hard to tell apart; the default) or `compact` (93, small visible loss, bigger savings). It never overwrites a file.
3. `doctor` is done when no diagnostic has `Fail`; each `message` says what to fix. Config fixes are the person's to choose: propose the YAML. A config from an older version can fail with `Unknown setting '<path>' at line N, column M.`; the setting was removed, so propose deleting the line. The GitHub release notes list each one under **Breaking config change**. A config that fails with `<path> can't be empty.` has that setting or list item set to `~`, `null` or nothing; propose a value, or deleting the line to get the default. A config that fails with `Duplicate setting '<path>' at line N, column M. It is already set at line K; keep only one.` sets that key twice in one section; names that differ only in case count as the same key. Ask which value the person meant, then propose deleting the other line.

## The rounds

Folders:

1. `queue add` queues eligible videos from the watch folders (`--first` queues one, for a first trial; `queue add <file>` queues one file, and `--force` skips the rules). It prints `Queued: N.` plus the other counts that are not zero (`Already queued`, `Held`, `Ready to finalise`, ...), and `Checked N files so far (M probed with ffprobe).` every 10 seconds on a large folder, which is progress. The starter rules match only large H.264 files at 1080p to 1440p and at 4K or above, so phone footage (HEVC) needs a rule change first; `calibrate` shows what the rules leave out.
2. `run` converts every queued video, Immich ones included, and passes over held ones. A video can take an hour or more on a PC. The person can watch from a second window with `status` or `queue list`, and `queue hold`, `queue release`, `queue next` and `queue add` take effect from the run's next video.
3. They review the AV1s, then `finalize --ready` (**warn**). In the full-screen view's Review pane, `a` opens each AV1, `f` / `F` finalise and `r` rejects one they don't want.

Immich (read [immich.md](immich.md) once for the age rule behind step 1):

1. Ask whether they ran the Immich app's "Free up space" since the last round, with a cutoff no older than `immich.minimumAgeDays`, and emptied the phone's trash. Only they can.
2. `queue add immich [--first | --limit <n>]` downloads and checks videos and queues the ones that fit; `--limit <n>` adds `n` new videos, never counting those already queued. `immich run` is that, then `run`. Before a paid remote round, ask what size they want. If the guard lists re-uploaded originals, they must delete those files from the phone.
3. They review, then `immich finalize` (**warn**; it shows capture dates before the prompt). `finalize --ready` also finalises ready Immich videos.

A `doctor` failure `ImmichTrashDisabled`, or exit `3` with `Immich's trash is off`, means the person must turn Immich's trash back on.

Tuning the rules, when they ask why videos are not converted: `calibrate` writes nothing and downloads nothing. Per rule it gives `videos`, `bytes` and `history`; then the largest `keptOut` groups. Quote each suggestion's `text`; the person decides on rule changes. A `testOne` suggestion's `command` is a `queue add "<file>" --force`, which queues one file outside the rules; its `run` stops before replacing anything. `codecUnknown` groups are Immich videos no run has downloaded. A large `historyLeftOut.otherSettings` means conversions under other or unrecorded quality settings are not counted. Exit `1` with `... is from an older version of video-optimiser`: run `status` once to upgrade it, then `calibrate`.

## The full-screen view

A bare `video-optimiser` in a terminal opens the full-screen view (the TUI; [tui.md](tui.md) is the person's page). It reads the same database as the commands, refreshes about once a second, and follows any run, foreground or background. Quitting (`q`, `Ctrl+C`) never stops a run; with an action of theirs still going it asks first. You cannot see the screen: ask the person what a pane or the footer says. `?` lists the action keys, not every move: `↑`/`↓` select a row (`pgup`/`pgdn` by 5, `home`/`end` to either end), in Activity they scroll the log and `end` follows it again, a `y/N` question takes `y` and treats any other key as no, and a list takes the arrows, `enter` and `esc`.

Panes, switched with `tab` / `shift+tab`: **Queue** (converting, waiting in run order, held, ready to finalise, recent failures), **Worker** (the Hetzner worker, checked every 30 seconds), **Activity** (the run's log, the same lines a terminal run prints), **Review** (ready videos with sizes and saving).

Each key is the command of the same name, with the same rules and refusals; classify it as that command when the person asks what it does:

| Key | Pane | Same as | Class |
| --- | --- | --- | --- |
| `h`, `r`, `n` | Queue | `queue hold`, `queue release`, `queue next` | Job database |
| `d`, `t` | Queue | `queue drop`, `queue retry` (failed videos only); asks first | Job database |
| `d` on a failed video | Queue | `queue dismiss <job-id>`, or `queue dismiss --all` when the person picks all; asks first | Job database |
| `s` | any | starts a background run (below) | **Warn** when it would create a Hetzner server |
| `x`, `X` | any | `run stop`, `run stop --now`; `x` again while a stop after this video is asked is `run stop --undo` (footer: `x carry on`); `X` asks first, and a stop now is final | Job database |
| `c`, `d` | Worker | `worker create` (lists servers with prices), `worker delete` (asks first) | **Warn** |
| `r` | Worker | `worker status` | Read-only |
| `w` | Activity | sets the background run's worker fate: delete or keep when it ends | **Warn** for keep: the worker bills |
| `o`, `a` | Review | opens the original (an Immich video's download) or the AV1 in the default app | Read-only |
| `f`, `F` | Review | `finalize <job-id>`, `finalize --ready`; same question first, `f` listing a warned video's warnings; `F` leaves warned videos out | **Warn** |
| `r` | Review | `queue reject <job-id>`; asks first | **Warn** |
| `esc` | any | stops a finalise of several videos after the one in progress, which always finishes; the rest stay ready. Asks nothing | Job database |
| `$` | any | `cost`, as a page (below) | Read-only |

**While `f` or `F` finalises.** Review's title reads `finalising 3 of 8` and its last line `Finalising 3 of 8, 1.2 GiB saved so far.` (batches of two or more). Both Review and Queue tag the video in progress `finalising` and the waiting ones `queued`. Each video's result reaches the log as it ends. Until the batch ends, `f`, `F` and `r` give a footer notice and open no question. `r` waits on every row, since `queue reject` needs the finalise lock the batch holds. `o`, `a` and the arrows keep working. After `esc`, the tally reads `Finalised 3; stopped with 5 still ready.`; finalise those later with `f`/`F` or `finalize --ready`.

The same letter means different things per pane (`r` is release, check-worker or reject), so name the pane with the key.

**Starting a run with `s`.** It launches a background run, which survives closing the terminal and is shown by `status` as `Run: background run, ...`. Without `remoteSsh.lifecycle: "hetzner"` it starts at once, encoding where the config says. With it, a list comes first: which worker converts (`Use <worker>` when one is up; otherwise the servers with hourly prices incl. VAT; always `No worker: convert on this PC`, which starts the run at once on this PC). A worker or server picked there opens a second list, `When the run ends`: `Delete the worker` or `Keep the worker`. A server picked from the list is created before the run starts and bills from then; if the create fails, no run starts. While the worker pane is deleting or creating the worker, `s` opens no list and shows `The worker is being deleted. Start the run once it's gone.` or `The worker is being created. Start the run once it's up.`. The background run asks nothing afterwards. If the worker picked for it is gone by the time the run needs it, the run creates none and ends; the Activity pane shows `The run failed: The worker chosen for this run is gone, so no new one was created. Start the run again to choose a worker.` `x` / `X` and `run stop [--now | --undo]` act only on a background run; a run started with `run` in a terminal stops only with `Ctrl+C` in its window.

**The cost page (`$`).** It replaces the panes with `cost`'s figures: the all-time EUR per TB, what Hetzner billed and what was freed, and its notes (`still billing`, `end estimated` tags); the runs newest first (when, worker, EUR, freed, EUR per TB, plus a waiting-for-review column only when some run has videos waiting); the selected run's videos (worker time, EUR, saved or `nothing saved: <why>`, EUR per TB); and a footnote that the three levels don't add up. `↑`/`↓` (or `home`/`end`) select a run, `pgup`/`pgdn` scroll its videos, `$` or `esc` closes it, and every other key does nothing while it is open (`Ctrl+C` still quits). It rereads the report every 5 seconds while open; runs and finalising go on underneath. `Nothing to show yet: this page fills in after a run on a Hetzner worker.` is `cost`'s nothing-recorded case. `Couldn't read the cost: ...` on its first line keeps the last figures below it.

**What it does not do.** It does not add videos; the person uses `queue add` or `queue add immich` in a terminal and the view shows them within a second.

## Reading output

### The status card

`status` prints the card, and so does a bare `video-optimiser` when its input or output is redirected, and a bare `video-optimiser --json` prints `status --json` even in a terminal; otherwise a bare `video-optimiser` opens the full-screen TUI instead. The card is read-only and answers at once, even during a run. Example:

```text
Queue: 1 converting, 2 waiting, 1 held, 3 ready to finalise.
Run: video-optimiser run, started 14:02 (process 1234), converting clip.mp4.
Worker: video-optimiser-4f1c9a2e7b30 (cpx42 in hel1), up 1h 12m, billing EUR 0.134/h incl. VAT.
Next:
  video-optimiser finalize --ready   finalises the 3 ready videos; review them first
```

- `Queue:` counts open videos; `Queue: empty.` when none. `waiting` and `stopped` are what a run takes; `held` are passed over; `ready to finalise` are waiting for review, `(N to check)` of them with validation warnings. With warned videos, `Next:` carries `queue list`, which prints each one's warnings, and `finalize --ready` counts only the others.
- `Run: none.` or the running command (`background run` for one with no terminal), its start time and process ID, and what it converts.
- `Worker:` appears only with the Hetzner lifecycle: the server, its age and hourly price, `Worker: none.`, a server that is `gone from Hetzner, but its IPv4 address still bills`, or `as recorded on this PC; Hetzner wasn't checked: <reason>.` Any billing worker deserves a mention.
- `Next:` holds up to three commands in priority order. `worker delete` appears there whenever a worker bills and no run is going, so **warn** before the person follows it.
- With no config found, the bare command says so and names `config init`.

### `queue list`

It groups the open videos, each group only when it has videos: `Converting now`, `Queue, in run order` (numbered), `Held` and `Ready to finalise`. Each line starts with the job ID that `queue hold`, `release`, `next`, `drop`, `retry`, `validate` and `finalize` take. A stopped video says what the next run does with it, for example `Stopped  clip.mp4, CRF 30: picks up the encode on the worker, or encodes again on a new one`. Immich videos show `(Immich)`. A warned ready video ends `, N warnings: <each warning>`, and a noted one `, N notes: <each note>` after that. `Nothing is queued or ready to finalise.` means none. `history` lists the ended ones.

### The run

One block per video, then a tally of non-zero outcomes (`Ready to finalise: 4. Not worth converting: 1.`, or `Nothing to finalise.`), a `Failed jobs:` list with plain phrases, and, when any video is ready, including ones earlier runs left, `N videos are ready to finalise: review them, then run 'video-optimiser finalize --ready'.` Held videos are counted first (`2 videos are held; runs pass over them until they're released.`).

Before those, a run checks again the AV1s of `ValidationFailed` jobs that an earlier version's checks failed, when the AV1, its validation record and the unchanged original are all on disk and no later job exists for the original. Nothing is uploaded or encoded, and no worker is asked about. It prints `Checking the AV1s of 3 videos that failed validation earlier.`, one block per video ending in an outcome line from the table below, then `Checked again: 2 ready to finalise (2 to check), 1 still failed.` A job the current checks fail stays `Failed` with the new message, and later runs leave it alone. With no such job, none of these lines appear.

```text
[2/5] clip.mp4 (1002.4 MiB)
      Uploading 1002.4 MiB to the worker.
      Uploaded 1002.4 MiB in 23s, checksum verified.
      Finding the best CRF for VMAF 97 (2 test clips of 20s each).
        CRF 34   VMAF 95.02   size  19%   below target
        CRF 29   VMAF 97.13   size  29%   passes, chosen
      Chose CRF 29 in 7m 36s. Expected output: 284.9 MiB (29% of the original).
      Encoding at CRF 29, expected to take about 5 minutes.
        20%, 54 fps, about 1m 06s left
      Encoded 285.1 MiB (29% of the original) in 5m 02s.
      Downloading 285.1 MiB from the worker.
      Downloaded 285.1 MiB in 30s, checksum verified.
      Checking the output: streams, duration, savings and decode test.
      Ready to finalise: 1002.4 MiB -> 287.3 MiB, 71.3% saved.
```

Upload and download lines appear only with a remote worker. Colour appears on a terminal only; escape codes in pasted text mean they can set `NO_COLOR=1`. The last line is the outcome:

| Outcome line | Job status |
| --- | --- |
| `Ready to finalise: ...` | `ReadyToFinalize` |
| `Ready to finalise: ... Check it before finalising:`, then one line per warning | `ReadyToFinalize`, with `validationWarnings` |
| `Note: ...`, after either line above | `ReadyToFinalize`, with `notes` |
| `Not worth converting: ...` | `Skipped` |
| `Skipped: ...` | `Skipped`, `UnsupportedAudio` |
| `Failed validation: ...` | `Failed`, `ValidationFailed` |
| `Failed during the upload`, `the CRF search`, `encoding` or `the download: ...`, then `Full log: <path>` | `Failed`; the ab-av1 log stays at that path |
| `Worker unreachable: ...` | `Interrupted`, `RemoteUnavailable`; the run stops here |

A warning names something the AV1 check doubted: a shorter or longer video, a different number of audio or subtitle streams, audio that starts at another point relative to the video, audio sync that could not be checked, or a failed decode test. The video is ready all the same; the person watches it and decides whether to finalise it alone (`finalize <job-id>`, or `f` in the view) or `queue reject` it. To explain a warning, say what to watch for: a length or stream warning means the encode may have stopped short or dropped a track, so check the end of the video and its audio; an audio-sync warning means watching lips against speech; a decode-test warning means FFmpeg hit frames it could not decode, so play the video through. The run's tally counts warned videos within the ready ones: `Ready to finalise: 4 (1 to check).`

A note records what the encode left out on purpose, and it asks nothing of the person: bulk finalise includes a noted video, and the `to check` tally leaves it out. Today the only note is an audio track FFmpeg can't copy (`Audio track 2 (apac) was left out ...`), almost always iPhone 16 spatial audio. The stereo track beside it is kept, and it is the one Immich, browsers, Android and Windows play. The person loses head-tracked playback on Apple devices and Audio Mix editing in Apple Photos, iMovie and Final Cut. A person who wants those kept sets `quality.unsupportedAudio: skip` (see `docs/configuration.md`); once a video is finalised, only its original still holds the track: archived, kept beside the AV1, or in Immich's trash until it is purged. Notes appear on `queue list`, `validate`, the finalise question and the view's review pane, each under `Note`/`Notes`.

**Slow or stuck.** A CRF search prints one `CRF` line per attempt, usually 4 to 7 attempts of about 2 minutes each for a 1 GB phone video on a Hetzner cpx42, longer on a PC. An encode prints progress about every 15 seconds at first and less often as it goes, a minute or more apart near the end; `about <time> left` appears only after 30 seconds and 5 percentage points. A job that prints nothing new for 15 minutes is stuck: ask for its last lines and `queue list --json`.

**`Ctrl+C`.** The first press stops after the video being converted (`Stopping after this video ...`), keeps its work, prints `Stopped after <file>; <N> videos are still waiting.` and `To convert the rest: video-optimiser run`, and exits `9`. A second press stops at once (`Stopped now. <file> continues from its last finished step on the next run.`). `run stop` does the same for a background run (one with no terminal; `status` shows `Run: background run, ...`): `run stop` is the first press, `run stop --now` the second. Its worker fate was chosen when it started, so it never asks. For a run in a terminal, `run stop` exits `1` and says to press `Ctrl+C` in its window; with no run going it prints `No run is going.` and exits `0`. `run stop --undo` withdraws a background run's stop after this video while that video is still converting, and the run carries on through the queue: it prints `Asked the run to carry on after this video.` and exits `0`. A run that already read the stop between videos ends anyway, and nothing reports that. With no stop asked it prints `No stop is asked of the run.` and exits `0`. A stop now is final: after `run stop --now`, `--undo` exits `1` with `The run is already stopping now, and that can't be undone.` `--now` and `--undo` together exit `2`.

### Immich discovery lines

`queue add immich` and `immich run` download each candidate to check it, so they print one line per video before the first job block:

- `  n. <file>  <date>  <codec> <resolution> <bitrate>  <size>  doesn't fit: <reason>` is normal. The download is deleted, and the video is not checked again until the rules change. `queued: fits rule N` marks a keeper (plain `queued` with a single rule; ` (k of <n>)` follows with `--limit`); rules number from 1 in `eligibility.rules` order.
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
| `Skipped`, `NotWorthConverting` | No CRF met both `quality.minimumVmaf` and the video's size cap, the stricter of `quality.maxEncodedPercent` and `savings.minimumBytesSaved`. A video too small to save `savings.minimumBytesSaved` is skipped before its search, with nothing uploaded. A finished AV1 no smaller than the original (`savings.requireSmallerOutput`) or saving less than `savings.minimumBytesSaved` ends here too, and its AV1 is deleted. Not a failure | Relay `failureMessage`: the setting that ruled it out, and the best CRF found or the AV1's size. Discovery checks the video again when the file or those settings change. |
| `Skipped`, `UnsupportedAudio` | The video has audio FFmpeg can't copy (iPhone spatial audio, `apac`), and either `quality.unsupportedAudio` is `skip` or no other audio track would be left. Checked before anything is uploaded or searched. Not a failure | Relay `failureMessage`. To convert it with the track left out, set `quality.unsupportedAudio` back to `drop`; discovery then queues it again. A video with no other audio stays skipped: converting it would make a silent AV1. |
| `ValidationFailed` | The AV1 left nothing to review: missing, empty, unreadable by ffprobe, or not AV1. A job that failed under an earlier version's stricter checks may hold a good AV1 | The original is untouched. A job failed by an earlier version's checks is checked again by the next run (see *The run*), so steer the person away from `queue retry`, the view's `t` and `cleanup` until then: each throws its AV1 away. Otherwise discovery won't queue it again until the file changes; `queue retry <id>` converts it again. |
| `StagingFailed`, `CrfSearchFailed`, `EncodeFailed`, `DownloadFailed`, `ProcessingFailed` | A step failed | Relay `failureMessage`. After the cause is fixed, `queue retry <id>` queues it again. |
| `StageStalled` | A CRF search or encode on the worker was alive but wrote nothing to its log for longer than both `processing.remoteSsh.stageSilenceTimeout` (default `30m`) and 1.5 times its age at its last line. The run stopped it, kept its log and went on to the next video. `failureMessage` is `The encode on the worker wrote nothing to its log for <duration>, so it was stopped.` (or `The CRF search ...`), then `Full log: <path>` | Relay `failureMessage`; the log's last lines show where it hung. `queue retry <id>` queues it again. Suggest a higher `stageSilenceTimeout` only when the log shows the stage was still progressing, slowly. |
| `RemoteConfigurationChanged` | The job started under a different processing setup | `queue retry <id>` starts a fresh job under the current setup. |
| `Cancelled` (shown as Dropped) | `queue drop` dropped it | `queue retry <id>` queues it again. Dropping a video whose remote stage was stopped prints `Its encode may still be running on the worker; the next run on that worker stops it.` (or `Its CRF search ...`): it keeps billing until a later run reaches that worker, or the worker is deleted. |
| `Rejected` | `queue reject` ended a ready video; its AV1 is deleted and the original is untouched | `queue retry <id>` queues it again from the start, and an Immich video's download is fetched again by the next run. |
| `Job <id> changed while this command was using it. Run the command again.` (exit `1`) | Another command changed the job first; its change stands | Check `queue list`, then run the command again if it still applies. |
| `Another run is using this database: ...` (exit `1`) | One run at a time | Wait for it, or check `status`. |
| `Ineligible`, `SourceNotReady`, `SourceOutsideWatchRoot`, `SourceChanged`, `SourceMissing` | The file did not qualify, changed, or was gone when the job ran. An original that is already AV1 fails as `Ineligible` even when it was queued with `--force` (`failureMessage`: `The video is already AV1, so converting it again would only lose quality.`) | Nothing. The next `queue add` re-checks it. |
| `Failed  <file>: <message>` from `finalize` or `immich finalize`, job still `ReadyToFinalize` | A file is missing (original, AV1 or validation record), another program has the original or the AV1 open (nothing moved), the person must act in Immich first, or an Immich request failed partway. The original was not replaced | Relay the message word for word; it names each copy left and the next command. `queue drop` only after they have moved those copies: `cleanup` deletes a dropped job's files, and they may be the last ones. A message ending `again to resume.` needs only a rerun. |
| `FinalizationIncomplete` | An Immich finalisation stopped partway, or waits on the person; or a file finalisation could not move a file, such as one open in a player, and moved nothing. The job is `ReadyToFinalize` | Relay `failureMessage`. After they do the step it names (an Immich step, or closing the file), the command it names again. |
| `FinalizationFailed`, `ManualInterventionRequired` | Replacing a file original failed in a way that may have moved a file, or a cut-off one could not be shown to have left both files untouched | Stop. Have them look at `sourcePath` and `outputPath`, and at the path a `failureMessage` ending `The original is at <path>.` names (where finalisation moved the original), and decide. `cleanup` keeps that AV1. `finalize <id>` (not `--ready`) takes back a job that failed this way with nothing moved. |

Failed, dropped, rejected and skipped jobs stay settled: discovery skips them until the original changes, and a skipped one also once the quality settings change. `queue retry <id>` is the way back. `queue dismiss` only hides a failure from the full-screen view's failed list. The job stays `Failed` in `history` and `--json`, and stays settled.

## Reference

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success, or a confirmation answered "no" |
| `1` | General failure, including a `doctor` failure outside configuration and dependencies, `calibrate` on a database an older version wrote, a run or finalise refused because another is using the database, `run stop` (any form) on a run started in a terminal, `run stop --undo` after a stop now, a queue verb refused (the video is in a state it does not apply to, converting now, or being finalised; or a drop or reject would leave the AV1 the only copy; `queue next` on a converting video only says so and exits `0`), `config init` onto an existing file, a run whose worker delete failed at its end, and a command that lost a race for the same job |
| `2` | Invalid arguments, including an unknown option, refused before the command does anything and naming the closest known option when one is close (`Unknown option '--forse'. Did you mean '--force'?`), a malformed or unknown job ID for `queue hold`, `release`, `next`, `drop`, `reject` and `dismiss` (a malformed one for `validate` and `finalize <job-id>` too), a removed command name, which says what replaced it (`'queue run' is now 'run'.`; also `queue discover`, `queue cancel` and `process`), or `queue retry` on a job it cannot retry (not found, not failed, dropped, rejected or skipped, or the original already has an open job) |
| `3` | Invalid configuration, including no config found, `queue add` with no watch roots, `worker create` or `worker delete` without `remoteSsh.lifecycle: "hetzner"`, `queue add immich`, `immich run` or `immich finalize` without `immich.baseUrl`, an Immich API key that is missing, rejected or lacks a permission, an Immich server that redirects (`ImmichRedirected`; the message names the `immich.baseUrl` to set), Immich's trash off (`ImmichTrashDisabled`), and `remote setup` with a missing or unusable Hetzner token or on a manual host |
| `4` | A failed dependency check in `doctor`: a media tool, the Immich server (unreachable, redirecting, not Immich 3, its key, or its trash off), the Hetzner API or SSH to the worker (`RemoteSshUnavailable`). Also `remote setup` when it cannot run `ssh-keygen` or list or upload the Hetzner key |
| `5` | `queue add <file>` refused the file: outside the watch roots or the rules (the line gives the `--force` command), too small to save `savings.minimumBytesSaved`, already AV1 (`<file> is already AV1, so it can't be queued.`), or a file video-optimiser made or archived (`--force` lifts none of these) |
| `6` | Processing failed and nothing is ready; `queue add <file>` on a file that is missing or unreadable; an Immich request failed; ffprobe could not read a file; Hetzner had no server capacity; Hetzner didn't answer a request in time (`Hetzner didn't answer <method> <path> within 100 seconds.`); a new worker's bootstrap script failed or ran out `bootstrapTimeout`; a worker's create request got no answer and Hetzner couldn't be asked whether it made the server (the output names `worker status` and `worker delete`); `validate` or `finalize <job-id>` named a job that does not exist; or `validate` named a job that is not ready to finalise, is held (`queue release` first), or whose original, AV1 or validation record is missing or unreadable |
| `7` | `validate` failed the output: missing, empty, unreadable by ffprobe, or not AV1 (a run counts such a video as failed: `6` or `10`); or `finalize <job-id>` named a job that is not ready. Warnings and a not-worth-converting verdict exit `0` |
| `8` | `finalize <job-id>` failed, or `finalize --ready` or `immich finalize` failed for every job it tried |
| `9` | Stopped with `Ctrl+C`; a Hetzner request that times out is `6`, never `9` |
| `10` | Partial success: `run` or `immich run` with some failed and some ready, `finalize --ready` or `immich finalize` with some jobs failed and some finalised, or `queue add`, `queue add immich`, `immich run`, `cleanup` or `calibrate` hit issues; read the output |

`run` and `immich run` exit `0` when no job failed, `10` when some failed and some are ready, `6` when some failed and none is ready; a worker that stopped answering counts as one failure. `9` and `1` (a failed worker delete) override those, and `immich run` also exits `10` with no failure when its re-upload guard or discovery had issues or left videos out.

`finalize --ready` and `immich finalize` share one rule: `0` when every job finalised, when nothing was ready or every ready job was left out for its warnings, or when the answer was "no"; `10` when some failed and some finalised; `8` when every job failed. `Ctrl+C` part-way exits `9` whatever already finalised.

### Job statuses

The console uses plain words; `--json` keeps the enum names.

- **Open** (`queue list`): `Queued` (waiting), `Staging` (uploading), `CrfSearching` (finding CRF), `Encoding`, `Downloading`, `Validating` (checking), `ReadyToFinalize`, `Finalizing`, `Interrupted` (stopped).
- **Finished** (`history`): `Completed` (finalised), `Failed`, `Cancelled` (dropped), `Skipped` (not worth converting, or skipped for its audio: `history` shows `Skipped` for those), `Rejected`.

A held job keeps its status, `Queued` or `Interrupted`; runs pass over it until `queue release` or `queue next`. `Staging` and `Downloading` are transfers to and from a worker. A job whose finalisation was cut off (`Finalizing`, or `Interrupted` after it) is finalised like a `ReadyToFinalize` one. An Immich one continues where it stopped. A file one continues only if its original and AV1 are where they started and the original is unchanged. If it was cut off right after moving the original aside, it moves the unchanged original back first and continues. Otherwise it fails as `ManualInterventionRequired`, and the message names where the original was moved when it is still there; `run` leaves it alone and says so. `Ctrl+C` during `finalize` leaves the job ready.

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
| `validationWarnings` | What the last AV1 check asks the person to watch for before finalising, one sentence each; empty when it found nothing |
| `notes` | What the encode left out on purpose, one sentence each; empty when nothing. Unlike `validationWarnings`, never a reason to hold the video back |
| `updatedUtc` | Last change, ISO 8601 |

`status --json` prints the card as one object; every field is present, `null` where it has no value:

| Field | Value |
| --- | --- |
| `queue` | `{ converting, waiting, stopped, held, readyToFinalise, toCheck }`; `toCheck` counts the ready videos with validation warnings, which bulk finalise leaves out |
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

`cost --json` prints `{ lifetime, runs }`. Bytes are integers, EUR include VAT and are decimals (`billedEur`, `eur` and `eurPerTb` rounded to 4 places; `hourlyGrossEur` unrounded, as recorded at the worker's creation), times are ISO 8601 UTC, and `eurPerTb` is `null` where the text shows `-`.

| Field | Value |
| --- | --- |
| `lifetime` | `{ billedEur, freedBytes, waitingBytes, eurPerTb, workers, stillBilling, endEstimated, priceMissing }`. `billedEur` is every priced worker's started hours × its hourly price; `freedBytes` and `waitingBytes` count videos converted on those workers; `workers` counts every recorded worker; `stillBilling` is true while one is up, `endEstimated` when one was deleted outside VideoOptimiser, `priceMissing` when one has no price and is left out |
| `runs` | Runs that used a worker, newest first: `{ id, startedUtc, endUtc, workerServerId, workerName, hourlyGrossEur, workerSeconds, eur, freedBytes, waitingBytes, eurPerTb, videoCount, videos }`. When the run never recorded its own end (its process was killed), `endUtc` is the last end of any span the run recorded; with none, the next run's start or the worker's end, else now. `workerSeconds` is the run's overlap with the worker, and `eur` is that × `hourlyGrossEur`, `null` with no price. A video resumed in a later run belongs to the later run, with all its time |
| `runs[].videos` | `{ jobId, file, workerSeconds, eur, savedBytes, state, status, eurPerTb, priceMissing }`. `state` is `freed`, `waitingForReview` or `nothingSaved`; `status` is the job status; `savedBytes` is original minus AV1, `0` for `nothingSaved` or when validation recorded no sizes; `eur` is its own time × the price, not rounded to the hour |

If Immich cannot be searched, `calibrate` still prints the report for the watch folders (the JSON too), then writes `Immich couldn't be searched, so its videos are not in this report: ...` to stderr and exits with the Immich failure's code (`3` for an API key problem, `6` otherwise). Treat that report as partial.
