# The full-screen view

Run `video-optimiser` with no command in a terminal and it opens a full-screen view of everything at once: the queue, the worker, what the run is doing, and the videos ready to finalise. You can also add videos, start and stop runs and act on videos from it.

```powershell
.\video-optimiser.exe --config .\video-optimiser.yaml
```

When the output goes to a file or another program, or with `--json`, the same command prints the status card instead (see [commands.md](commands.md)).

## The four panes

| Pane | What it shows |
| --- | --- |
| Queue | Videos converting now, waiting in run order, held, ready to finalise, and recent failures. While videos are being finalised, the ready ones carry the same `finalising` and `queued` marks as in Review. |
| Worker | The Hetzner worker, if you use one: its type, location, age and hourly price, checked with Hetzner every 30 seconds. |
| Activity | The run going now, or the last one, line by line, as it would print in a terminal. The video converting now has a progress line: a bar for an encode or a transfer, or, while the CRF search runs, the CRF and test clip ab-av1 is on and then each CRF's result. If a remote step has reported nothing for a minute, the line says when the worker last answered, which is normal for a slow test clip; in yellow, it says how long the worker or the run has been silent. Each SSH command sent again after no answer or a lost connection shows in the log. |
| Review | Videos ready to finalise, with the original's size, the AV1's size and the saving. A video that validation warned about is marked `1 warning` (or `2 warnings`), here and in the Queue pane, and the header counts these videos as `to check`. The selected video's warnings are listed in full at the bottom of the pane, followed by its notes, such as an audio track that was left out. A note doesn't mark the video or count it as `to check`. While several videos are being finalised, the pane's title counts through them (`finalising 3 of 8`) and its last line says how much has been saved so far. The video being finalised is marked `finalising` in place of its saving, and the ones still to come are dimmed and marked `queued`. Each finalised video leaves the list, and the keys keep working meanwhile. A single `f` shows only the `finalising` mark. |

`tab` and `shift+tab` move between panes. The screen refreshes about once a second.

The view reads the same database as every other command, so it follows a run started anywhere: from this view, from `run` in another window, or one that started before you opened it. A reopened view shows the whole log of the current run, including what happened while it was closed. Quitting never stops a run.

## Keys

Press `?` for a guide to the main keys. Anything that can lose work or cost money asks `y/N` first.

These work from any pane:

| Key | What it does |
| --- | --- |
| `s` | Start a run in the background. See [Starting a run](#starting-a-run). |
| `x` | Stop the run after the video converting now. Press `x` again before that video finishes to carry on. |
| `X` | Stop the run now, after asking. The video converting now starts again from its last finished step on the next run. |
| `$` | Show what the freed space cost. See [What the freed space cost](#what-the-freed-space-cost). |
| `?` | Show the key guide. Any key closes it. |
| `q`, `Ctrl+C` | Quit. If an action you started is still going, it asks first, because quitting would cut it off. |

Each pane adds its own:

| Pane | Key | What it does |
| --- | --- | --- |
| Queue | `a` | Add videos: scan your watch folders, download from Immich, or add files by path. See [Adding videos](#adding-videos). |
| Queue | `h` / `r` | Hold a waiting or stopped video, so runs pass over it, or release it. |
| Queue | `n` | Convert the selected video next. |
| Queue | `d` | Drop the video from the queue, after asking. The original is untouched. On a failed video, dismiss it instead: see [Dismissing failures](#dismissing-failures). |
| Queue | `t` | Queue a failed video again as a fresh job, after asking. |
| Worker | `c` | Create a worker. It lists the servers you can have, with their hourly prices. |
| Worker | `d` | Delete the worker, after asking. It stops billing at once. |
| Activity | `↑` `↓` `pgup` `pgdn`, `home`, `end` | Scroll the log, jump to its start, or follow it again. |
| Activity | `w` | Change what happens to the worker when the background run ends. |
| Review | `o` / `a` | Open the original or the AV1 in Windows' default player. For an Immich video, the original is its download. |
| Review | `f` / `F` | Finalise the selected video, or every ready one, after the same question `finalize` asks. `f` on a warned video lists its warnings, and any notes, in the question. `F` leaves warned videos out and names them, so you watch each one and finalise it with `f`. While a finalise is going, they ask nothing: they say whether the selected video is being finalised or queued, or that you can start another finalise once this one ends. |
| Review | `r` | Reject the video, after asking: its AV1 is deleted and the original is kept. See [Rejecting a video](#rejecting-a-video). While a finalise is going, `r` says you can reject once it ends, on every row, because rejecting needs the lock the finalise holds. |
| any | `esc` | Cancel a queue addition in progress, or stop a finalise of several videos after the video it's on. An open dialog closes first. If both actions are going, the addition is cancelled first. |

The worker keys and `w` do something only with a Hetzner worker (`remoteSsh.lifecycle: "hetzner"`).

## Adding videos

In Queue, press `a`, then choose with the arrows and `enter`. Under the list, a few lines explain the highlighted choice using your config: which watch folders a scan covers, or that a source isn't set up yet.

- **Scan your watch folders** starts at once on every folder in `watch.roots`, as `queue add` does. There's no folder to pick.
- **Search Immich** asks how many videos to queue, then checks your Immich videos one at a time. It downloads each one to check it against your rules and deletes the download unless it queues the video. The count starts at `1`; enter a positive whole number, or clear it to add all eligible videos. This is `queue add immich`, including its trash check and re-upload guard: converted originals uploaded again by the phone may go back to Immich's trash.
- **Add files by path** asks for one or more files, as `queue add <file>` does for one. Type or paste a path, use `backspace` to correct it, then `enter`. To add several, paste one path per line, which is what Explorer's **Copy as path** gives for several selected files. A line break in a paste starts the next path; only an `enter` you type adds them. The box shows the line you're on and, with more than one, how many paths it holds. Quotes from **Copy as path** are accepted, and a path given twice is added once. `esc` closes the question. While typing, letters such as `q`, `s` and `?` are part of the path.

With several files, Activity shows a line for each one as it's added, then a count such as `Queued: 3. Doesn't fit your rules: 2. Can't be queued: 1.`

If files are outside the watch folders or don't fit the eligibility rules, one question offers to force them, however many there are. That bypasses only those checks; AV1 files, the size cap and protected tool files stay excluded, and the question never offers files refused for those reasons.

Adding works with an empty queue and while a run converts. It queues videos without starting a run; a run already going picks them up between videos. Progress and results appear in Activity. Only one addition runs at a time. With no dialog open, `esc` cancels it: videos already queued stay queued, a list of files stops before the next file, and a partial Immich download is removed. It leaves a conversion run going.

## Starting a run

`s` starts a background run: it keeps going after you quit the view or close the terminal, and Windows doesn't sleep while it converts.

Without a Hetzner worker (`remoteSsh.lifecycle: "hetzner"`), it starts at once and encodes where the config says. With one, it asks first:

1. Which worker converts. With a worker up, use it or convert on this PC instead. With none, pick a server from the list with its hourly price, or convert on this PC. Converting on this PC starts the run straight away. A server you pick is created before the run starts; if Hetzner can't create it, no run starts.
2. If you picked a worker, what happens to it when the run ends: delete it, or keep it up, billed hourly. You can change this while the run goes with `w` in the Activity pane.

While the worker pane is deleting or creating the worker, `s` asks nothing and tells you to start the run once that's finished. If the worker you picked is deleted before the run gets to it, from here or from another terminal, the run ends without creating a server in its place, and the Activity pane says so.

To stop a background run from a terminal, use `run stop` (after this video) or `run stop --now`. `run stop --undo` withdraws a stop after this video. A run started with `run` in a terminal is stopped only with `Ctrl+C` in its own window; `x` and `X` say so.

## What the freed space cost

`$` opens a page with what the space freed on Hetzner workers cost, in EUR per TB freed. These are the figures `cost` prints (see [commands.md](commands.md#workers-and-what-they-cost)), plus each run's videos:

- At the top, the all-time figure: what Hetzner billed, how much it freed, and how much is waiting for review. While a worker is still up, it's marked `still billing`, and its bill counts to now. A worker deleted outside video-optimiser is marked `end estimated`.
- In the middle, one row per run, newest first: when it ran, the worker, what it cost, what it freed and the EUR per TB. When any run has videos waiting for review, a column shows how much. `↑` and `↓` select a run; `home` and `end` jump to the first and the last.
- At the bottom, the selected run's videos, each with its worker time, cost, what it saved and its EUR per TB. A video that saved nothing says why, such as `nothing saved: failed`, because its worker time still counts. `pgup` and `pgdn` scroll a long list.

The line at the foot of the page is a reminder that the three levels don't add up. [commands.md](commands.md#workers-and-what-they-cost) explains why.

The page refreshes every few seconds while it's open. The run, a finalise and the worker check carry on underneath. `$` or `esc` closes it; if a queue addition is still going, `esc` cancels that first and leaves the page open. Until then the other keys do nothing, except `Ctrl+C`, which quits as usual. Before your first run on a Hetzner worker, the page says it fills in after one.

## Rejecting a video

When you watch an AV1 in the Review pane and don't want it, press `r`. The AV1 is deleted at once, and so is an Immich video's download. The original stays where it is, and `queue add` and `immich run` won't queue that video again until its file changes. `queue retry <job-id>` brings it back.

If the AV1 is open in a player, it can't be deleted yet. The video is still rejected, and `cleanup` deletes the file later.

The same action from a terminal is `queue reject <job-id>`.

## Dismissing failures

The Queue pane lists the latest failures. To clear ones you won't retry, select one and press `d`. With only one failure listed, it asks first. With several, you choose between that video and all of them.

A dismissed video leaves the list, but its job stays failed in `history`. `queue add` and `immich run` still pass over it until its file changes, so dismissing it won't get it converted again. To retry one later, use `queue retry <job-id>`; `history` shows the ID.

From a terminal, the same thing is `queue dismiss <job-id>`, or `queue dismiss --all` for every failure.
