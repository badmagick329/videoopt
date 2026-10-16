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

The card shows the queue's counts, the run going now, the Hetzner worker if you use one (checked with Hetzner), and up to three commands worth running next. With no config found, it says so and names `config init`.

| Command | What it does |
| --- | --- |
| `status [--json]` | Print the card. `--json` is for scripts. |
| `queue list [--json]` | List the open videos in groups: converting now, the queue in run order, held, and ready to finalise. |
| `history [--json]` | List the videos that are done: finalised, failed, dropped, rejected or not worth converting. |

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
| `queue add [--first]` | Queue the videos in your watch folders that match your rules. `--first` queues one. |
| `queue add <file> [--force]` | Queue one file. `--force` skips the watch folder and rule checks, but never the check that the file can save enough. |
| `queue add immich [--first \| --limit <n>]` | Download up to `n` new old videos from Immich, check them against your rules and queue the ones that fit. See [immich.md](immich.md). |

## Converting

| Command | What it does |
| --- | --- |
| `run [--local \| --remote]` | Convert every queued video, folder and Immich alike. It stops before replacing anything. `--local` or `--remote` picks where to encode for this run only. |
| `immich run [--first \| --limit <n>]` | `queue add immich`, then `run`. `--limit` counts new videos only. |
| `run stop [--now]` | Stop a background run after this video, or now. A run in a terminal stops with `Ctrl+C` instead. |

## Changing the queue

| Command | What it does |
| --- | --- |
| `queue hold <job-id>`, `queue release <job-id>` | Make runs pass over a waiting or stopped video, or take it again. |
| `queue next <job-id>` | Convert a waiting or stopped video next, and release it if it was held. A run that is going takes it after the current video. |
| `queue drop <job-id>`, `queue drop --all` | Take one video, or every waiting, stopped and held one, out of the queue. Its files stay until `cleanup`. |
| `queue retry <job-id>` | Queue a failed, dropped, rejected or not worth converting video again, as a fresh job. |

A run reads the queue again before each video, so these count from its next video.

## Reviewing and finishing

| Command | What it does |
| --- | --- |
| `validate <job-id>` | Run the checks again on the AV1 of a video that is ready to finalise. |
| `finalize <job-id>`, `finalize --ready` | Replace one original, or every ready one, Immich videos included. |
| `immich finalize` | Finalise only the Immich videos: upload each AV1 to Immich and move the original to Immich's trash. |
| `queue reject <job-id>` | Reject a video that is ready to finalise: delete its AV1 (and an Immich video's download) now, and keep the original. Later `queue add` and `immich run` pass over it until its file changes. |
| `cleanup [--dry-run]` | Delete temporary files no video will use again. It keeps an AV1 that may be the only copy of a video. |

`queue drop`, `queue reject` and `cleanup` refuse to delete an AV1 that may be the only copy of a video, for example when its original is missing or gone from Immich.

## Workers

| Command | What it does |
| --- | --- |
| `worker create`, `worker status`, `worker delete` | Manage the Hetzner worker by hand. |

## Other

| Command | What it does |
| --- | --- |
| `version` | Print the version. |

## Exit codes

`0` means success, including a question answered "no". `9` means you stopped it with `Ctrl+C`, and `10` means it partly worked: read the output. The full list is in [agent-guide.md](agent-guide.md#exit-codes).
