# Commands

Every command takes `--config <path>`. `video-optimiser --help` lists them all with their options.

## Finding the config

Without `--config`, the app uses `VIDEO_OPTIMISER_CONFIG` if it is set to a config's full path. Then it looks for `%ProgramData%\VideoOptimiser\config.yaml`, then `video-optimiser.yaml` in the current folder. `config show` prints the one in use.

## Where things stand

Run `video-optimiser` with no command. In a terminal it opens the [full-screen view](tui.md). Otherwise, or with `status`, it prints a short card:

```text
Queue: 3 waiting, 1 ready to finalise.
Run: none.
Next:
  video-optimiser finalize --ready   finalises the ready video; review it first
  video-optimiser run               converts the 3 videos in the queue
```

Ready videos with validation warnings are counted as `to check` (`Queue: 2 ready to finalise (1 to check).`), and the card then suggests `queue list` to read their warnings, since `finalize --ready` leaves them out.

The card shows the queue's counts, the run going now, the Hetzner worker if you use one (checked with Hetzner), and up to three commands worth running next. With no config found, it says so and names `config init`.

| Command | What it does |
| --- | --- |
| `status [--json]` | Print the card. `--json` is for scripts. |
| `queue list [--json]` | List the open videos in groups: converting now, the queue in run order, held, and ready to finalise. A ready video with validation warnings shows them on its line, then any notes, such as an audio track that was left out. A note doesn't hold the video back from `finalize --ready`. |
| `history [--json]` | List the videos that are done: finalised, failed, dropped, rejected, not worth converting, or skipped because of audio FFmpeg can't copy. |

## Setting up

| Command | What it does |
| --- | --- |
| `config init [--quality archive\|standard\|compact]` | Write a starter config. It never overwrites a file. |
| `config validate`, `config show` | Check the config, or print it. |
| `doctor [--json]` | Check the config, the tools, the database, disk space, Immich and any remote worker. |
| `calibrate [--json]` | Show how your rules fit your library. It changes nothing. See [configuration.md](configuration.md#tune-your-rules). |
| `remote setup` | One-time Hetzner setup. It creates no worker. See [remote-ssh.md](remote-ssh.md). |

## Filling the queue

| Command | What it does |
| --- | --- |
| `queue add [--first \| --limit <n>]` | Queue the videos in your watch folders that match your rules. `--limit <n>` queues up to `n` new ones, in the order the scan finds them, and stops scanning once it has them; videos that already have a job don't count. `--first` is `--limit 1`. |
| `queue add <file> [--force]` | Queue one file. `--force` skips the watch folder and rule checks, but never the checks that the file can save enough and isn't already AV1. |
| `queue add immich [--first \| --limit <n>]` | Download up to `n` new old videos from Immich, check them against your rules and queue the ones that fit. See [immich.md](immich.md). |

## Converting

| Command | What it does |
| --- | --- |
| `run [--local \| --remote]` | Convert every queued video, folder and Immich alike. It stops before replacing anything. `--local` or `--remote` picks where to encode for this run only. First it rechecks the AV1s of videos that failed validation in an earlier version, without encoding them again or using a worker, so they can become ready to finalise. |
| `immich run [--first \| --limit <n>] [--local \| --remote]` | `queue add immich`, then `run`. `--limit` counts new videos only. |
| `run stop [--now \| --undo]` | Stop a background run after this video, or now. `--undo` withdraws a stop after this video while that video is still converting, and the run carries on through the queue. A stop now can't be undone. A run in a terminal stops with `Ctrl+C` instead. |

## Changing the queue

| Command | What it does |
| --- | --- |
| `queue hold <job-id>`, `queue release <job-id>` | Make runs pass over a waiting or stopped video, or take it again. |
| `queue next <job-id>` | Convert a waiting or stopped video next, and release it if it was held. A run that is going takes it after the current video. |
| `queue drop <job-id>`, `queue drop --all` | Take one video, or every waiting, stopped and held one, out of the queue. Its files stay until `cleanup`. |
| `queue retry <job-id>` | Queue a failed, dropped, rejected or not worth converting video again, as a fresh job. |
| `queue dismiss <job-id>`, `queue dismiss --all` | Hide one failed video, or every one, from the full-screen view's failed list. The job stays failed in `history`, `queue add` still passes over its original, and `queue retry` still works on it. |

A run reads the queue again before each video, so these count from its next video.

## Reviewing and finishing

| Command | What it does |
| --- | --- |
| `validate <job-id>` | Run the checks again on the AV1 of a video that is ready to finalise. It prints any notes after the result. It exits `0` when the AV1 passes, passes with warnings (length, streams, audio sync or the decode test) or turns out not worth converting, in which case the AV1 is deleted. It exits `7` when the original or AV1 is missing, the AV1 is empty or unreadable, or its video is not AV1. |
| `finalize <job-id>`, `finalize --ready` | Replace one original, or every ready one, Immich videos included. `finalize --ready` leaves out videos with validation warnings and prints the `finalize <job-id>` for each. For a video with warnings, `finalize <job-id>` lists them, and any notes, and asks `y/N` first; a video without warnings is finalised without a question. |
| `immich finalize` | Finalise only the Immich videos: upload each AV1 to Immich and move the original to Immich's trash. Like `finalize --ready`, it leaves out videos with validation warnings and names them. |
| `queue reject <job-id>` | Reject a video that is ready to finalise: delete its AV1 (and an Immich video's download) now, and keep the original. Later `queue add` and `immich run` pass over it until its file changes. |
| `cleanup [--dry-run]` | Delete temporary files no video will use again. It keeps an AV1 that may be the only copy of a video. |

`queue drop`, `queue reject` and `cleanup` refuse to delete an AV1 that may be the only copy of a video, for example when its original is missing or gone from Immich.

## Workers and what they cost

| Command | What it does |
| --- | --- |
| `worker create`, `worker status`, `worker delete` | Manage the Hetzner worker by hand. |
| `cost [--json]` | Show what the space freed on Hetzner workers cost, in EUR per TB freed: first for all time, then one line per run. `--json` adds each video. It only reads, though like every command it first upgrades a database an older version wrote. |

`cost` counts only workers that video-optimiser created, and only since version 0.13.0. Earlier runs, runs on this PC and runs on your own SSH host don't appear. A worker's bill is its server's hourly price plus its IPv4 address's, which Hetzner prices separately. If Hetzner listed no price for a worker, `cost` says so and leaves out that worker's bill and its videos.

A video counts as freed once it is finalised, whether its original was deleted, archived or kept. Until then, a ready video's saving shows as waiting for review. A TB here is 10^12 bytes, as drives are sold.

The three figures don't add up, because each counts different time. A video counts only its own conversion time. A run counts all the time its worker was up during it, setup and gaps between videos included. The all-time figure is what Hetzner billed, rounded up to whole hours per worker. In the full-screen view, `$` shows the same figures with each run's videos (see [tui.md](tui.md#what-the-freed-space-cost)).

```text
All time: EUR 192.86 per TB freed. Hetzner billed EUR 0.27 for 1 worker, which freed 1.3 GiB; 572.2 MiB more is waiting for review.
A worker is still up: its bill counts to now and grows until the worker is deleted.
Runs, newest first:
  2026-10-17 11:10 to 12:40  video-optimiser-4f1c9a2e7b30  1h 20m  2 videos  EUR 0.18  1.3 GiB freed, 572.2 MiB waiting for review  EUR 128.57 per TB
For each video's figures, run 'video-optimiser cost --json'.
```

## Other

| Command | What it does |
| --- | --- |
| `version` | Print the version. |

## Exit codes

`0` means success, including a question answered "no". `9` means you stopped it with `Ctrl+C`, and `10` means it partly worked: read the output. The full list is in [agent-guide.md](agent-guide.md#exit-codes).
