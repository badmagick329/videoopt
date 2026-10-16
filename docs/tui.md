# The full-screen view

Run `video-optimiser` with no command in a terminal and it opens a full-screen view of everything at once: the queue, the worker, what the run is doing, and the videos ready to finalise. You can also start and stop runs and act on videos from it.

```powershell
.\video-optimiser.exe --config .\video-optimiser.yaml
```

When the output goes to a file or another program, or with `--json`, the same command prints the status card instead (see [commands.md](commands.md)).

## The four panes

| Pane | What it shows |
| --- | --- |
| Queue | Videos converting now, waiting in run order, held, and recent failures. |
| Worker | The Hetzner worker, if you use one: its type, location, age and hourly price, checked with Hetzner every 30 seconds. |
| Activity | The run going now, or the last one, line by line, as it would print in a terminal. |
| Review | Videos ready to finalise, with the original's size, the AV1's size and the saving. |

`tab` and `shift+tab` move between panes. The screen refreshes about once a second.

The view reads the same database as every other command, so it follows a run started anywhere: from this view, from `run` in another window, or one that started before you opened it. A reopened view shows the whole log of the current run, including what happened while it was closed. Quitting never stops a run.

## Keys

Press `?` at any time to see every key. Anything that can lose work or cost money asks `y/N` first.

These work from any pane:

| Key | What it does |
| --- | --- |
| `s` | Start a run in the background. See [Starting a run](#starting-a-run). |
| `x` | Stop the run after the video converting now. |
| `X` | Stop the run now, after asking. The video converting now starts again from its last finished step on the next run. |
| `?` | Show every key. Any key closes it. |
| `q`, `Ctrl+C` | Quit. If an action you started is still going, it asks first, because quitting would cut it off. |

Each pane adds its own:

| Pane | Key | What it does |
| --- | --- | --- |
| Queue | `h` / `r` | Hold a waiting or stopped video, so runs pass over it, or release it. |
| Queue | `n` | Convert the selected video next. |
| Queue | `d` | Drop the video from the queue, after asking. The original is untouched. |
| Queue | `t` | Queue a failed video again as a fresh job, after asking. |
| Worker | `c` | Create a worker. It lists the servers you can have, with their hourly prices. |
| Worker | `d` | Delete the worker, after asking. It stops billing at once. |
| Worker | `r` | Check the worker with Hetzner now. |
| Activity | `↑` `↓` `pgup` `pgdn`, `end` | Scroll the log, or follow it again. |
| Activity | `w` | Change what happens to the worker when the background run ends. |
| Review | `o` / `a` | Open the original or the AV1 in Windows' default player. For an Immich video, the original is its download. |
| Review | `f` / `F` | Finalise the selected video, or every ready one, after the same question `finalize` asks. |
| Review | `r` | Reject the video, after asking: its AV1 is deleted and the original is kept. See [Rejecting a video](#rejecting-a-video). |

The worker keys and `w` do something only with a Hetzner worker (`remoteSsh.lifecycle: "hetzner"`).

## Starting a run

`s` starts a background run: it keeps going after you quit the view or close the terminal, and Windows doesn't sleep while it converts.

Without a Hetzner worker (`remoteSsh.lifecycle: "hetzner"`), it starts at once and encodes where the config says. With one, it asks two questions first:

1. Which worker converts. With a worker up, use it or convert on this PC instead. With none, pick a server from the list with its hourly price, or convert on this PC. A server you pick is created before the run starts; if Hetzner can't create it, no run starts.
2. What happens to the worker when the run ends: delete it, or keep it up, billed hourly. You can change this while the run goes with `w` in the Activity pane.

To stop a background run from a terminal, use `run stop` (after this video) or `run stop --now`. A run started with `run` in a terminal is stopped only with `Ctrl+C` in its own window; `x` and `X` say so.

## Rejecting a video

When you watch an AV1 in the Review pane and don't want it, press `r`. The AV1 is deleted at once, and so is an Immich video's download. The original stays where it is, and `queue add` and `immich run` won't queue that video again until its file changes. `queue retry <job-id>` brings it back.

If the AV1 is open in a player, it can't be deleted yet. The video is still rejected, and `cleanup` deletes the file later.

The same action from a terminal is `queue reject <job-id>`.

## What it can't do yet

It doesn't add videos to the queue. Use `queue add` or `queue add immich` in a terminal; the view shows them within a second.
