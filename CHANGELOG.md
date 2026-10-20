# Changelog

## [Unreleased]

### Remote workers

- A Hetzner worker can start from a snapshot of an earlier worker, with FFmpeg and the encoders already built, so it's ready sooner and bills less before the first video. Set `processing.remoteSsh.hetzner.image` to the snapshot's ID; `docs/remote-ssh.md` explains how to make the snapshot. Before, a worker made from such a snapshot was reported ready before it had set up SSH.

### Background runs

- New `run stop --undo` withdraws a stop after this video, as long as that video is still converting, and the run carries on through the queue. A stop now can't be undone.
- In the full-screen view, pressing `x` again withdraws a stop after this video, the same as `run stop --undo`. The footer then offers `x carry on`.

## [0.15.0] - 2026-10-19
### Check your config

- **If your config has `sampleCount: 5` under `quality.crfSearch`, delete that line to keep the number of CRF-search samples you had before.** Earlier versions of `config init` wrote it into every config. Until now it did almost nothing: every video whose length ffprobe could read got one sample per two minutes of video, from 2 to 12. `sampleCount` now applies to every video, so a config that keeps the line gets exactly 5 samples per search. A 3-minute video then takes 5 samples instead of 2, and a 30-minute video 5 instead of 12. Configs made by `config init` from now on leave the line commented out.

### Remote workers

- A new Hetzner worker gets an SSH host key that VideoOptimiser makes before the server exists, so even the first connection checks that key. Before, the first connection trusted whatever answered at the new address.
- The worker's bootstrap checks FFmpeg and ab-av1 against pinned SHA-256 checksums and builds libvmaf and SVT-AV1 from pinned commits. A download that doesn't match stops the bootstrap, and the run says why.
- New setting `processing.remoteSsh.stageSilenceTimeout` (default `30m`): how long a CRF search or encode on the worker may go without writing to its log before it counts as hung. A long encode gets more time, up to 1.5 times as long as it had been running.

### Other changes

- `finalize --ready` and `immich finalize` finalise videos in the order they were queued, with any you moved up through `queue next` first, as the full-screen view's `F` does. They used to go by when each video last changed. A video that fails with an unexpected error now counts as failed and the command goes on to the next one, where it used to stop the whole command.
- The full-screen view words a refused hold, move up, drop or reject as the `queue` commands do.

### Fixes

- **Breaking:** A setting left empty in the config, with `~`, `null` or nothing after the colon, now stops the config from loading, and the error names it, such as `processing.mode can't be empty.` Commands used to stop with `Object reference not set to an instance of an object.`, or pass `config validate` and fail later. Settings that `config validate` let through empty, such as `quality.pixelFormat`, now refuse too: give each a value, or delete the line to get its default. An empty section still gets its defaults, an empty optional setting such as `immich.baseUrl` still means it isn't set, and an empty list is still empty, except `processing.remoteSsh.hetzner.locations`, which still gets the default locations.
- **Breaking:** A setting written twice in one section, including names that differ only in case, now stops the config from loading, and the error names both lines. The config used to load with the last value, so `action: keep` followed by `action: delete` deleted originals.
- **Breaking:** The config now refuses `quality.minimumVmaf` outside 50 to 100, `quality.preset` outside -1 to 13, `quality.crfSearch.minCrf` or `maxCrf` outside 1 to 63, and a rule that lists `av1` in `codecs`. These used to pass, so a typo such as `minimumVmaf: 9.5` for `95` ran at that target.
- A finalise that can't move a file because another program has it open, such as your player, leaves the video ready to finalise: close the file and run `finalize` again. With `original.action: delete`, a finalise that installs the AV1 but can't delete the original counts as done and says where the original is. Both used to mark the video as failed.
- If a crash or power cut stops a finalise between moving the original aside and installing the AV1, the next `finalize` puts the original back and finishes. When it can't, the review message says where the original is.
- `finalize --ready` exits `8` when every video it tries fails, as `immich finalize` does. It used to exit `10`, the code for a partial success. A script that checks `finalize --ready` for `10` should check for `8` too.
- `cleanup` checks each video again after you answer `y` and keeps the files of one that changed meanwhile. It used to delete the AV1 of a video a background run had just made ready to finalise.
- `immich finalize` moves an original to Immich's trash only once Immich's copy of the AV1 has the same checksum as the AV1 that passed validation. If they differ, the original stays and the message says what to delete. `immich run` now leaves alone a video another terminal queues while it searches Immich, where it used to download it again over the copy that job reads.
- VideoOptimiser no longer follows a redirect from Immich, so your API key goes only to `immich.baseUrl`. A redirect stops the command with an error that says where it pointed.
- On a worker, uploading a large video no longer fails when the checksum check takes more than 30 seconds, and a CRF search or encode that hangs is stopped once its log is silent for `stageSilenceTimeout`. The run used to wait on it forever while the worker billed.
- A worker that can't remove a finished video's files no longer fails that video or stops the run: the run says so in one line, and a later run removes them. A video whose encode started on a worker stays stopped while its original is offline, such as on a USB drive, and resumes once the original is back. It used to fail, and the encode was lost.
- A run that fails while it starts, for example because the database is busy, only reports the error. It used to decide the worker's fate after letting go of the run lock, so it could delete a worker another run had just started using. Stopping a run now always ends the ffprobe, ab-av1 or ssh it was waiting on.
- In the full-screen view, `s` no longer offers the worker while the worker pane is deleting or creating it. A background run whose chosen worker is deleted before the run gets to it now ends and says so. It used to create a new server on its own, and that server billed.
- `queue add` no longer queues files VideoOptimiser made, such as an Immich download in `immich.stagingDirectory` or a temporary AV1 in `.video-optimiser`, whatever `eligibility.excludedDirectories` says. Nor does it queue a video that is already AV1, even with `--force`, or one another program still has open for writing, such as a copy in progress.
- When a background run is converting the video, `queue hold` and `queue drop` tell you to stop it with `run stop --now`, or with `run stop` to let a dropped video finish first. In the full-screen view, `h` offers `X`, and `n` says the video is converting. They used to say to press Ctrl+C in the run's window, which a background run doesn't have.
- A mistyped option, such as `--forse`, stops the command before it does anything and suggests `--force`. It used to be read as a file or job ID. A `finalize` with neither a job ID nor `--ready` says it needs one, and `help` shows the help only as the first word.

## [0.14.1] - 2026-10-18
### Remote workers

- A new Hetzner worker whose bootstrap fails stops the run within about 15 seconds. The run names the stage that failed and why, then shows the end of the worker's cloud-init log. It used to wait out `bootstrapTimeout` (90 minutes by default) and keep the server billing.
- A worker that never got ready is no longer kept by default. A run in a terminal asks `Delete the worker now? [y/N]`, and a run with redirected input deletes it. A background run deletes it even when it was started to keep the worker, since no later run could use it.
- Ctrl+C while a worker is being created no longer leaves a server billing with no record of it on this PC. The run records the worker and ends like any other stop with a worker up. If Hetzner never answered the create request, the run looks the worker up there; if it can't, it says the worker may be billing and gives the commands to check for it and delete it.
- When Hetzner takes more than 100 seconds to answer, `worker create`, `worker delete` and a run that creates a worker say so and exit with code 6. They used to print nothing and exit with code 9, as if you had pressed Ctrl+C. A run that was setting up a worker ends as failed, and the worker is kept or deleted as after any other failed setup.

### Fixes

- Editing a stopped video's original before the next run, to fix its date or location for example, now makes that run convert it from the start. The run used to reuse the work done on the old file, and finalising then replaced your edited original with an AV1 of the old version.
- In the full-screen view, the server list or the finalise question no longer covers or replaces a question you are answering, such as a reject or a drop, so y can no longer act on a video you never chose. The footer names the key that brings the list or the question back.
- In the full-screen view, an action Hetzner doesn't answer now ends with an error after 100 seconds. It used to stay busy until the view was restarted, and quitting asked about it.

## [0.14.0] - 2026-10-18
### Defaults

- `savings.minimumBytesSaved` now defaults to `1MiB` instead of `100MiB`, so `quality.maxEncodedPercent` decides whether a video is worth converting.
- `quality.crfSearch.minCrf` now defaults to `8` instead of `18`. A video that needs a CRF below 18 to reach the VMAF target can now be converted.
- If your config leaves either setting out, the next run checks the videos it skipped as not worth converting again. A config written by `config init` sets both, so change them there to get the new values.

### Fixes

- iPhone 16 videos with spatial audio no longer fail to encode. FFmpeg can't copy the spatial track (`apac`), so the AV1 leaves it out and keeps the stereo track, and the video gets a note saying so.
- Notes show wherever warnings do, but they don't hold a video back from `finalize --ready`. `queue list --json` and `history --json` have a new `notes` array.
- To keep spatial audio, set the new `quality.unsupportedAudio` to `skip`; those videos then stay unconverted. A video with no audio FFmpeg can copy is skipped under either setting.
- Videos that already failed this way convert after `queue retry <job-id>`.

## [0.13.0] - 2026-10-17
### What the freed space cost

- New `cost` command: what the space freed on Hetzner workers cost, in EUR per TB freed, for all time and for each run. `cost --json` adds each video's figures.
- `$` in the full-screen view opens a page with the same figures and the selected run's videos. `$` or `Esc` closes it.
- A video counts as freed once it is finalised, whether its original was deleted, archived or kept. Until then, its saving shows as waiting for review.
- Only workers video-optimiser created count, and recording starts with this version, so earlier runs, runs on this PC and runs on your own SSH host don't appear.
- The figures for a video, a run and all time don't add up, because each counts different time. `docs/commands.md` explains why.

### Fixes

- MKV files without a recorded video bitrate (common from ffmpeg and yt-dlp) no longer fail every rule with "video bitrate is unavailable". The whole file's bitrate, audio included, is used instead. The next `queue add` probes those files again; the rest of the library stays cached.

## [0.12.0] - 2026-10-16
### Breaking changes

- `finalize --ready` and `immich finalize` now leave out videos with validation warnings and name each one. Watch it, then finalise it alone with `finalize <job-id>`. A finalisation that stopped partway still resumes, warnings or not.

### Checking the AV1

- Validation now fails an AV1 only when it is missing, empty, unreadable or not AV1. A different length, stream count or audio sync, or a failed decode test, leaves the video ready to finalise with a warning to check. The run, `validate` and `queue list` print each warning, and `queue list --json` and `history --json` have a new `validationWarnings` array.
- An AV1 that saves too little is skipped as not worth converting instead of failing. Its AV1 is deleted and the original stays.
- `validate` exits `0` for a pass, a pass with warnings and a video not worth converting, and `7` for a failure.
- The next run checks again the videos an earlier version failed, under the new rules, without uploading or encoding anything. Leave `cleanup` and `queue retry` alone until then: both delete the AV1 that check needs.

### Finalising

- `finalize <job-id>` on a warned video lists its warnings and asks first.
- The status card and the run's tally count warned videos, as in `2 ready to finalise (1 to check)`. `status --json` has a new `queue.toCheck` count.

### The full-screen view

- Warned videos are marked in Review and Queue, and Review lists the selected video's warnings in full. `f` shows them in its question; `F` leaves warned videos out.
- While several videos are being finalised, Review counts through them (`finalising 3 of 8`) and shows how much has been saved so far. The video being finalised and the ones still to come are tagged in Review and Queue, and each video's result reaches the log as it finishes.
- During a finalise, `f`, `F` and `r` answer at once in the footer instead of opening a question.
- `Esc` stops a finalise after the video it's on. That video finishes, and the rest stay ready.

### Fixes

- Validation no longer fails good variable-frame-rate phone video. The packet-count check is gone, and the decode test no longer reports a false `non monotonically increasing dts`.

## [0.11.0] - 2026-10-16
### The full-screen view

- During a CRF search, the progress line says which CRF and test clip ab-av1 is on, such as `CRF 34: encoding test clip 1 of 2.` A single CRF can take minutes on a worker, and before this the line said nothing until it was done.
- When a remote step has reported nothing for a minute, the progress line now says when the worker last answered (`worker answered 10s ago`), so a slow step reads differently from a lost worker. If the worker stops answering, it says for how long, in yellow.

### Remote workers

- Each time an SSH command to the worker gets no answer or loses its connection and is sent again, the log says so, with the try number (`asking again (try 2 of 3)`). Once the worker answers, the log says that too. A lost connection during a download is reported the same way.

## [0.10.0] - 2026-10-16
### Converting

- Phone videos with a variable frame rate no longer fail validation with `Output video packet count differs from the original.` FFmpeg was converting them to the fixed rate in their header, which can be slightly off, and dropped frames to fit: one in 117 for a 3-minute video. Every frame now keeps its original timing. Retry those videos with `t` in the Queue pane or `queue retry <job-id>`.

### The full-screen view

- The Activity log lists each CRF the search tries, with its VMAF, its size as a share of the original and whether it passed, as `run` prints them in a terminal.
- The progress line draws a bar for an encode, an upload, a download or a checksum check. Its time left shows once, after the speed, instead of twice.
- Until a step's first progress line arrives, a spinner shows in place of `No progress line yet.`, after an empty bar when the step will draw one.
- When a converting video has reported nothing for a minute, its progress line says how long ago it last did. A healthy encode or transfer reports every few seconds.
- `d` on a failed video in the Queue pane dismisses it, after asking. With several failures listed, you can dismiss just that one or all of them. A dismissed video leaves the list, but its job stays failed in `history`, its original stays skipped, and `queue retry <job-id>` still works on it.

### The queue

- New `queue dismiss <job-id>` and `queue dismiss --all` do the same from a terminal.

### Remote workers

- A run no longer hangs when an SSH command to the worker never returns. A command with no answer after 30 seconds counts as a lost connection and is sent again with 60 seconds, then 90; the worker counts as unreachable after 3 minutes. One such command held a run for over half an hour after its encode had finished.

## [0.9.1] - 2026-10-16
### Fixes

- In the full-screen view, creating or deleting a Hetzner worker, and starting a run on a new server, failed with "The remote SSH lifecycle is not configured for Hetzner" when `processing.mode` is `local`. They now work in any mode, as the `worker` commands do.
- `r` in the Worker pane now says when Hetzner has answered, or why it couldn't be asked, however long Hetzner takes.
- Long lines in the Activity log wrap onto the next line instead of being cut off.
- Progress messages such as `Uploading 507.2 MiB to the worker.` and the full-screen view's `Creating the worker.` end with a full stop instead of `...` or `…`. An ellipsis now only means text was cut off to fit.
- A running worker's price showed as unknown in the full-screen view, `status` and `worker status`, because Hetzner moved where it names a server's location. The price shows again.

## [0.9.0] - 2026-10-16
### The full-screen view

- Run `video-optimiser` with no command in a terminal to open it. It shows the queue, the worker, the run's log and the videos ready to finalise, and refreshes about once a second.
- It follows any run, including one started in another window, and catches up on a run that started before you opened it. Quitting with `q` or `Ctrl+C` never stops a run.
- From it you can start and stop a run, hold, release, drop or retry videos, create and delete the worker, open the original or the AV1, and finalise or reject videos. Anything that can lose work or cost money asks first. Press `?` to see every key.
- With output redirected, or with `--json`, a bare `video-optimiser` still prints the status card.

### Background runs

- A run started from the full-screen view runs in the background, so closing the terminal doesn't stop it. It asks first which worker to use and whether to delete or keep it when the run ends; you can change that answer while it runs.
- New `run stop` stops a background run after the video converting now, and `run stop --now` stops it at once, the same as one or two presses of `Ctrl+C` in a terminal.
- `status` shows a background run, and `status --json` adds a `background` field to its `run` object.

### Rejecting a video

- New `queue reject <job-id>` is for a ready video you watched and don't want. It deletes the AV1, and an Immich video's download, at once and keeps the original. `r` in the full-screen view's Review pane does the same after asking.
- `queue add` and `immich run` then pass over that video until its file changes, and count it separately (`Previously rejected`, `rejected by you`). `queue retry <job-id>` queues it again.
- It refuses when the AV1 may be the only copy of the video, or when an Immich video's finalisation stopped partway (`immich finalize` finishes that one).

### Other changes

- Windows no longer goes to sleep while a run is converting.

### Docs

- The README is shorter, with a quick start first. Every command is now in [docs/commands.md](docs/commands.md), and the full-screen view has its own page, [docs/tui.md](docs/tui.md).

## [0.8.0] - 2026-10-15
### Breaking changes

- `queue run` is now `run`, `queue discover` is now `queue add`, `queue cancel` is now `queue drop`, and `process` is removed (use `queue add <file>`, then `run`). `--force` moved to `queue add <file>`, and `--dry-run` is for `cleanup` only. The old names exit `2` and name their replacement.
- `immich run` is now `queue add immich` followed by `run`. It converts every queued video, folder videos included, and the resume command after `Ctrl+C` is `run`. Before, it converted only the Immich videos its limit chose.
- `immich run --limit <n>` and `--first` now add `n` new videos. Immich videos that already have a job no longer count toward `n`, so repeating the command downloads `n` more each time. Before, `n` topped the open Immich jobs up to `n`.
- `status` and a bare `video-optimiser` print a status card instead of a job list. `status --json` prints the card's fields (`queue`, `run`, `worker`, `next`) instead of an array of jobs; `queue list --json` still has the array.
- `queue retry` prints `Queued clip.mp4 again as job <id>.` instead of `Created fresh queued job <id>. Run 'queue run' to process it.`

### New commands

- `status` shows the queue's counts, the run going, the Hetzner worker (type, location, age, hourly price) and up to three commands worth running next. It asks Hetzner for at most 15 seconds and does not wait for a run that is creating a worker. With no configuration it says so and names `config init`.
- `queue add <file>` queues one file after the checks a run makes; `--force` skips the rule checks and stays on the job. `queue add immich [--first | --limit <n>]` searches Immich, downloads and checks videos and queues the ones that fit, without converting anything. Both end with the queue's counts and what to run next.
- `queue hold <job-id>` parks a waiting or stopped video until `queue release <job-id>`, and `queue next <job-id>` puts one first. A run reads the queue again before each video, so these count from its next video. The job database is upgraded for holds and run order: an older version reading it ignores holds and would convert held videos.

### The queue and runs

- `run` converts folder and Immich videos in one run order, and downloads again any missing Immich copy first. If Immich can't be reached, those videos stay queued and the rest convert.
- `Ctrl+C` stops after the video being converted and keeps its progress; a second press stops at once. Both print how to resume, and with a Hetzner worker up the first press asks whether to delete it when the run stops.
- `queue list` groups open videos (converting, in run order, held, ready to finalise) and says what the next run does with a stopped video. `queue drop` says what stays until `cleanup` and refuses to drop a video whose only copy `cleanup` would then delete.
- Commands no longer trip over each other: a second run refuses to start while one is going, a run leaves a video being finalised alone, and a command that loses a race for one job leaves the other's change.
- Clearer wording across commands: "worker" everywhere, plain phrases instead of internal failure names, `history` status names, and suggested commands that repeat the `--config` you typed.

### Workers and billing

- Every way a remote run ends now decides what happens to the Hetzner worker: it follows `deleteAfterRun`, or says the worker stays up and bills hourly and how to delete it. A run creates a worker only once a job will use it.
- `worker create`, `worker status` and `worker delete` work in local mode when `remoteSsh.lifecycle` is `"hetzner"`, so a worker left billing can be deleted. `doctor` warns about one, and `worker status` and `doctor` no longer freeze while a run sets up a worker.
- Remote runs recover better from dropped connections and reruns: keepalives end a dead connection within about a minute, uploads resume, a CRF search or encode is never started twice, and a rerun picks up a running encode without replaying its log. When the worker can't be reached, the run stops at that job instead of failing the rest.
- The first run on a worker stops the work of dropped or finished videos there and removes their workspaces.

### Immich

- Discovery prints less before the first download, says why each candidate doesn't fit, and closes with counts. `immich run` and `queue add immich` leave held videos alone.
- A missing downloaded copy is fetched again by `run`. Only Immich saying an asset is gone fails the video; a server error or rate limit leaves it queued.
- `immich finalize` names videos by file name and prints each failure once. When Immich can't find an original, it keeps the job ready and names the copies that survive.

### Fixes

- `Ctrl+C` or a crash during `finalize` no longer leaves a folder job needing manual review: it continues when the original and AV1 are where they started. A missing file stops only its own job, so the rest of the batch finalises.
- `validate` refuses a held video or one not ready to finalise, and a missing or unreadable validation record gives a clear message instead of a .NET error. `cleanup` keeps any AV1 or download that may be the only copy of a video.
- `queue add` and `calibrate` exit `10` on any `Issue:` line, not only a bad watch root. `calibrate` says why it has no suggestions and keeps the folder report when Immich can't be searched.
- `remote setup` no longer says it is complete while the config still fails validation.

### Docs

- [docs/agent-guide.md](docs/agent-guide.md) now helps an AI assistant explain the app to you: what each command changes, how to read its output and what to run next. You run the commands; it no longer tells an agent how to run them for you.

## [0.7.1] - 2026-10-13
### Changed

- The README is shorter. Eligibility rules, `calibrate`, videos not worth converting and what `finalize` does to originals are now in [docs/configuration.md](docs/configuration.md).

### Fixed

- [docs/immich.md](docs/immich.md), [docs/remote-ssh.md](docs/remote-ssh.md) and [docs/agent-guide.md](docs/agent-guide.md) now match the program: exit codes for a missing Immich permission, a failed run and `remote setup`; the `handled in earlier runs` count; which Immich videos are searched; transfer progress every 10 seconds; `.env` beside the config file; and the Hetzner workspace path.

## [0.7.0] - 2026-10-13
### Added

- `config init` asks for the quality target: Archive (VMAF 97, indistinguishable from the original), Standard (95, hard to tell apart, the default) or Compact (93, small visible loss, bigger savings). It writes the answer as `quality.minimumVmaf`, with a comment naming the level. `--quality archive|standard|compact` answers it without asking. With redirected input and no flag it uses Standard and says so. `--quality` on another command exits `2`.
- `calibrate [--json]` shows how the eligibility rules fit the library, and changes nothing. Per rule, it lists the videos it admits now and the earlier results for videos like them: conversions with their saving, and videos not worth converting with their bitrate. The videos no rule admits are grouped by codec, resolution and bitrate. Immich videos no run has downloaded are grouped without a codec, with a bitrate worked out from size and duration. Suggestions rest on 3 or more earlier results: a higher `minimumVideoBitrate` for a rule, or a new rule. It also names a typical file to test from the largest groups never tried. Only results at the current quality settings count. A database from an older version stops it with exit `1` until `status` upgrades it. See *Tune your rules* in the README.

### Changed

- **Breaking config change:** `savings.minimumPercentageSaved` is removed; `quality.maxEncodedPercent` already demands a larger saving. A config that still sets it fails to load. Delete the line.
- **Breaking config change:** `quality.crfSearch.enabled` is removed. Nothing read it: the CRF search always ran, so `false` did nothing. A config that still sets it fails to load. Delete the line.
- `savings.minimumBytesSaved` now applies before the encode, not only after it. Each video gets a size cap, the stricter of `quality.maxEncodedPercent` and the share that still saves `minimumBytesSaved` (of the video stream, or of the whole file when the CRF search tests the whole video), and ab-av1's CRF search rejects any CRF over it. A video too small to save the minimum ends `Not worth converting` before its CRF search, so it is never uploaded or searched, even with `--force`. `queue discover` does not queue it, and `immich run` does not download an asset no bigger than the minimum. The output is still checked against the minimum after the encode.
- When `savings.minimumBytesSaved` sets the cap, the CRF search's start line and the skip reason name it: `Finding the best CRF for VMAF 97 that saves 100MiB (...)`, `AV1 would save about 62.4 MiB at the best quality-passing setting (CRF 30, VMAF 97.2); the minimum is 100MiB (savings.minimumBytesSaved).`
- The job database keeps each job's video codec, resolution, bitrate, duration and size, and those of each Immich video skipped after its download, for `calibrate`. Existing jobs get them from the probe cache where it still describes the original. No output changes.
- Each job now records its quality settings when its CRF search chooses a CRF, not only when it ends not worth converting, so `calibrate` counts a conversion only under the settings that made it. Videos converted before this release have none and are left out of `calibrate`'s history.
- The `--json` error now lists the commands that take it: `--json is only supported by doctor, queue list, status, history and calibrate.`
- Changing `savings.minimumBytesSaved` checks skipped videos again, like the quality settings. Every video skipped before this release is checked once more.

## [0.6.0] - 2026-10-13
### Added

- `quality.maxEncodedPercent` (default `80`, a whole number from 1 to 100) is the largest share of the original's size the AV1 output may have. It is passed to ab-av1 as `--max-encoded-percent`, which used to rely on ab-av1's own default of 80. `config init` writes it.
- An `immich:` configuration section for the Immich source: the server's LAN URL, where the API key loads from (`VIDEO_OPTIMISER_IMMICH_API_KEY`, then `.env`), the age gate, whether to skip favourites, and the staging directory. With `immich.baseUrl` set, `doctor` checks that the server runs Immich 3, that it accepts the key, and that the staging directory is writable. A command that needs a permission the key lacks stops with exit code 3 and names it.
- `immich run` converts old videos from the Immich library. It first moves back to Immich's trash any converted original the phone uploaded again and lists those files to delete from the phone. Then it downloads each eligible video taken more than `immich.minimumAgeDays` ago to the staging directory and processes it as `queue run` does. `--first` converts one video; while an Immich job is still open, it converts that one and downloads nothing new.
- `immich finalize` lists the converted Immich videos with their capture date and sizes and asks once. It then uploads each AV1 as a new asset with the original's albums, tags, favourite, description and rating, and moves the original to Immich's trash. If finalisation stops partway, the next run continues it.
- `immich run --limit <n>` converts at most `n` videos in one round. Open Immich jobs count toward `n`, so a rerun after an interruption converts those first and downloads only enough new videos to reach it. `--first` is the same as `--limit 1`. A limit below 1, `--limit` with `--first`, or `--limit` on another command exits `2`.
- `queue cancel <job-id>` also cancels a job that is ready to finalise.
- `status` and `history` show an Immich job's asset IDs; their `--json` output adds `source`, `immichAssetId` and `immichNewAssetId`.
- In local mode, `doctor` checks that ab-av1 is 0.11.7 or newer and that FFmpeg has the libsvtav1 encoder, the libvmaf filter and the 10-bit `yuv420p10le` format. In both modes it checks that FFmpeg can decode AV1 with libdav1d, which validation's decode test needs, unless `validation.decodeTest` is `none`.
- [docs/immich.md](docs/immich.md) explains Immich setup and each round. [docs/agent-guide.md](docs/agent-guide.md) tells an AI agent how to run VideoOptimiser for a user, including which commands need the user's go-ahead.
- The release zip includes `docs/`, `.env.example` and `scripts/bootstrap-remote-worker.sh`, so managed Hetzner processing works from a release download.
- `cleanup` also deletes the staged Immich download of each completed, failed or cancelled Immich job, then its asset folder once empty. A download that an open job still reads, including a retry's, is never deleted, nor is one kept after a failed finalisation.
- `immich run` downloads again the missing staged copy of an open Immich job, such as one `queue retry` queued after `cleanup`, and checks it against the job's checksum. If Immich no longer returns those bytes, the job fails as `SourceMissing` and the asset is free to be found again.
- VideoOptimiser refuses to move anything to Immich's trash while the trash is off, because Immich would then delete it for good. `doctor` fails as `ImmichTrashDisabled`, and warns as `ImmichTrashShort` when the trash keeps videos for fewer than 7 days. `immich run` stops before the re-upload guard and `immich finalize` before its prompt, both with exit code `3`; `finalize <job-id>` leaves the job ready to finalise.
- `remote setup` does the one-time managed Hetzner setup. It checks the API token, or prints how to create one and exits `3`. It reuses `remoteSsh.identityFile`, or creates `%USERPROFILE%\.ssh\video-optimiser-hetzner-ed25519` with `ssh-keygen`. It uploads the public key to the Hetzner project unless the project already has it, under any name. Then it shows the `processing:` section it would write, with `mode: "remoteSsh"`, `lifecycle: "hetzner"`, `identityFile` and `sshKeyName` filled in, and asks `[y/N]`. On `y` it backs the config up to `<config>.bak`, replaces only that section, and restores the backup if the result is invalid. It creates no server. [docs/remote-ssh.md](docs/remote-ssh.md) now uses it for setup; the manual steps are under *Doing it by hand*.
- On a terminal, output is coloured: a job's header is bold, its CRF attempts and progress are dim, and outcomes are green (ready to finalise, `queued`, `Done`), yellow (not worth converting, `skip:`) or red (failed, `issue:`, errors on stderr). The words are the same with or without colour. Colour is off when the stream is redirected, when `NO_COLOR` is set and not empty, when `TERM` is `dumb`, and for `--json`; each stream decides for itself.
- `--local` and `--remote` on `queue run`, `process` and `immich run` set `processing.mode` for one run, without editing the config; the `Encoding on` line ends with `(--local)` or `(--remote)`. A job started in the other mode is skipped, printed as `Skipped <file>: started on ...`, and resumed by the next run without the flag; it does not count as a failure. Both flags together, or either on another command, exit `2`.
- `queue run`, `process` and `immich run` first print where encoding runs, before any download or encoding: `Encoding on this PC.`, the SSH target, a Hetzner server still running from an earlier run, or `a Hetzner server created for this run, billed hourly, trying cx43, cx53, cpx42, ccx33 at up to EUR 0.30/h.` `doctor` reports the same line as `EncodingLocation` in a new `Processing` category.

### Changed

- `Ctrl+C` on a remote run stops only this PC's side: a CRF search or encode keeps running on the worker instead of being killed, since the worker bills hourly either way. The command prints what is still running, that the worker is billed until it is deleted, the command that picks the work up again, and `worker delete` to stop it instead. Rerunning the same command follows the stage, or uses its result if it finished meanwhile, and a remote encode keeps its attempt instead of starting again from 0%. `Ctrl+C` during an upload, a download or the worker's setup says the worker stays up, billed hourly, and how to resume or delete it.
- **Breaking config change:** `processing.remoteSsh.hetzner.serverType` is replaced by `serverTypes`, a list tried in order, each in every configured location, so a sold-out type falls through to the next. The default is `["cx43", "cx53", "cpx42", "ccx33"]`. A config that still has `serverType` fails to load and names the replacement. Before creating a server, VideoOptimiser skips any type that is sold out, not x86, deprecated, or smaller than `minimumCpuCount` or `minimumAvailableMemory`, and prints the type, location and hourly price it creates. Jobs started on a managed worker before this change still resume.
- **Breaking for scripts:** a managed Hetzner `queue run`, `process` or `immich run`, and `worker create`, now ask `Create the server? [y/N]` before creating a server. The prompt shows the first type's hourly price and, when a fallback costs more, the most a fallback may cost; one `y` covers every fallback. Any other answer, or no input, creates nothing: the command prints `No server was created.`, exits `0`, and leaves the jobs queued. Scripts must pipe `'y'`. Reusing a running server never asks.
- New `processing.remoteSsh.hetzner.maxHourlyPrice` skips any server type and location costing more, in EUR per hour including VAT. The default is `0.30`; `null` removes the cap.
- When no Hetzner server can be created, the error lists each server type, the locations tried for it, and why each failed.
- `worker status` and `worker create` show the worker's server type and location.
- `queue run` processes only jobs from the watch roots. Immich jobs run with `immich run`, and `queue retry` on an Immich job now says so.
- `watch.roots` may be empty when `immich.baseUrl` is set, so an Immich-only config passes `config validate` and `doctor`. `queue discover` then says it needs a watch root.
- The README is rewritten to be shorter and task-first.
- `queue run`, `process` and `immich run` print each job as a short block instead of raw logs. A header (`[2/5] name.mp4 (1002.4 MiB)`) opens it, a line says when each step starts (upload, CRF search, encode, download, checks) and the last line is the job's outcome. The CRF search prints one line per attempt with its VMAF, size and verdict, and the chosen CRF's expected output size and encode time. Encode progress, upload and download progress and long checksums print at most once every 10 seconds, and nothing at all for a step that finishes sooner. A transfer ends with one summary line (`Uploaded 1002.4 MiB in 23s, checksum verified.`). ab-av1's timestamped log and the remote `/usr/bin/time -v` report are no longer printed. When ab-av1 fails, its full log is kept in the `.video-optimiser` folder beside the original (local runs now keep one too, as `<job-id>.crf.log` or `<job-id>.encode-<attempt>.log`), and the failure and `history` name its path. A run ends with the next command to run (`finalize --ready`), and the retained-worker message prints `video-optimiser worker delete`, with `--config` only when you passed it. `--json` output and exit codes are unchanged.
- Every other command's output now reads like a job's: `immich run` opens by saying that candidates are downloaded to be checked with ffprobe and that a rejected candidate's download is deleted and the video skipped in future runs, then prints one line per downloaded video with its date, codec, resolution, video bitrate, size and outcome (`hevc 4K 33 Mbps  319.4 MiB  queued: fits rule 3 (1 of 5)`, `h264 4K 42 Mbps  495.1 MiB  skip: rule 1 needs 2GiB`, `issue: ...`), and ends with what it found, why videos were skipped and how many it never downloaded. The re-upload guard prints only when it acts. `queue discover` prints a count every 10 seconds instead of a line per file and groups queued files by folder. A new managed worker says setup takes several minutes, prints its stage, and no longer prints a line for every refused SSH check while the server boots. `finalize --ready` names each job by file with `Done` or `Failed` and ends with `Finalised: 2.`, naming only the counts that are not zero, `immich finalize` says how long Immich keeps the originals in its trash, and `cleanup` groups files by folder. `--json` output, exit codes and prompts are unchanged.
- Run and finalisation tallies leave out zero counts: `Ready to finalise: 1.` instead of `Ready to finalise: 1. Not worth converting: 0. Failed: 0.`, `Nothing to finalise.` when all are zero, and the same for `Finalised:`, `Failed:` and the `cleanup` totals. `immich finalize` counts `2 videos` instead of `2 job(s)`. A finalisation's message no longer starts with `Finalisation complete.`: it reads `New asset <id>; the original is in Immich's trash.` or `Original archived to <path>.`
- An encode's `about <time> left` is worked out from the pace since the first progress line instead of taken from ab-av1, whose estimate is rounded to whole minutes and was wrong early on. It appears once the encode has gained 5 percentage points and run for 30 seconds. Sizes ab-av1 reports (`Expected output`, `Encoded`) print like every other size, `208.9 MiB`.
- The re-upload guard's lines read `Re-upload guard: trashed ...`, `restored ...` and `Re-upload guard issue: ...` instead of `TRASHED:`, `RESTORED:` and `RE-UPLOAD GUARD ISSUE:`.
- A managed Hetzner worker now installs ab-av1 0.11.7, the version CI tests, instead of 0.10.4. `doctor` rejects a remote worker with an older ab-av1. A worker created before this change keeps 0.10.4: delete it with `worker delete` and the next run creates a new one.

### Fixed

- `.mov` and `.m4v` originals no longer fail at the encode step with `ab-av1 encode failed with exit code 1`. FFmpeg cannot write AV1 into those containers, and the output used the original's extension. The AV1 is now an `.mp4` for `.mp4`, `.mov` and `.m4v` originals and an `.mkv` for every other extension, keeping an all-capitals extension in capitals (`IMG_0897.MOV` gives `IMG_0897.MP4`). In a watched folder, `delete` and `archive` install it under the original's name with the new extension, numbered if a different file already has that name, and `finalize` names it: `Original deleted; the AV1 is <path>.` An Immich upload already took the output's extension. PCM audio is re-encoded to FLAC, because PCM copied into MP4 does not play in browsers, including Immich's web player; FLAC is lossless, except that 32-bit float PCM becomes 24-bit. Other audio is still copied.
- The CRF search line no longer claims `2 test clips of 20s each` for a video too short to cut them from. ab-av1 tests the whole video when the clips would cover at least 85% of it, and the line now says `testing the whole video`.
- `queue cancel` on a job that has already ended says why instead of `is not cancellable`: a failed or cancelled job points to `cleanup` to delete its temporary output, and a job still in progress says it can be cancelled once its run stops.
- `immich run` no longer says `Your eligibility rules changed since the last run` on every run after a rules change. Skips recorded under older rules were counted until the search reached each one, so a `--first` run left the rest counted and the next run repeated the claim. The line now reads `N videos were skipped under different eligibility rules; each is checked again when the search reaches it.`, and a stale skip that the extension or size check rejects without a download is recorded under the current rules, so it stops counting.
- A video that AV1 cannot shrink enough, because no CRF met both `quality.minimumVmaf` and `quality.maxEncodedPercent`, is no longer reported as `CrfSearchFailed`. Before, the job failed with a message that was an unrelated ab-av1 log line, a retry failed the same way, and a managed Hetzner worker was kept and `One or more jobs failed. Delete the managed worker anyway?` was asked. Now the job ends as `Skipped` (`NotWorthConverting`), prints `Not worth converting: <file>. AV1 would make it 95% of the original's size at the best quality-passing setting (CRF 31, VMAF 97.3); the limit is 80% (quality.maxEncodedPercent).` (or, when even the lowest allowed CRF missed the VMAF, `No CRF reached VMAF 97; even CRF 18, the lowest allowed (quality.crfSearch.minCrf), scored 96.3.`), counts as neither ready nor failed (`Not worth converting: 1.` in the run's tally), exits `0`, and lets `deleteAfterRun` delete the worker without asking. `queue discover` and `immich run` leave the video alone ("Previously skipped") until the file or the quality settings change (`minimumVmaf`, `maxEncodedPercent`, `preset`, `encoder`, `pixelFormat`, `crfSearch.minCrf`, `crfSearch.maxCrf`); `queue retry <job-id>` queues it now, and `cleanup` deletes a skipped Immich job's download.
- When ab-av1 fails for any other reason, the job's message is its `Error:` line (or its last output line) instead of the first line of its log, which was an INFO line. A remote stage failure adds the same line after the exit code.
- A queued or interrupted job whose original was deleted no longer stays queued forever, failing every run. It fails as `SourceMissing`, and discovery queues the file or Immich asset again once it is back.
- `queue discover` no longer queues an original again after its job failed or was cancelled, which encoded a failing file again on every run and undid `queue cancel`. It skips the original until the file changes and reports it as "Previously failed or cancelled". Failures caused only by the file not being ready, being ineligible, or lying outside the watch roots still requeue.
- Creating a managed Hetzner worker from a Windows checkout with Git's `core.autocrlf` no longer sends the bootstrap script with CRLF line endings, which bash on the worker cannot run.
- `queue retry <job-id>` now queues any failed or cancelled job's original again, not only one whose remote configuration changed.
- Remote CRF search and encoding print ab-av1's progress while it runs. Before, the SSH command that started each stage stayed open until the stage ended, so its whole log appeared at once when the step was over.
- The AV1 output now keeps the original's container metadata: its capture date (`creation_time`), GPS location, and phone make and model (`com.android.*`). Before, every converted file lost them, so photo libraries dated it by the conversion and dropped it from the map. Files already converted stay without them.
- In a watched folder, `finalize` now gives the installed AV1 the original's Date created and Date modified, for `delete`, `archive` and `keep`. Before, the AV1 showed the time of the conversion, so it sorted as a new file in Explorer and in photo libraries. The archived original keeps its own dates. If the dates cannot be set, the AV1 is still installed and `finalize` ends with a warning. On Linux and macOS only Date modified is copied. Files already finalised keep the dates they have.
- `validate` and `queue run` now fail an output that ffprobe cannot open, such as a truncated MP4 with no moov atom, as a validation failure: exit code 7, job `Failed` as `ValidationFailed`, and a message naming the output file. Before, `validate` printed "Configuration could not be read" and exited 3, as if the config were wrong. Any other ffprobe failure that reaches the top level now exits 6 with ffprobe's own message instead of being reported as a configuration error; an unreadable original still stops the run with that message instead of failing the output. A Hetzner server without a public IPv4 address or an unreadable output manifest are no longer reported as configuration errors either.

## [0.5.0] - 2026-10-12
### Added

- `original.action: archive` moves each original to `original.archiveDirectory` when it is finalised: a folder beside the original (`Archive` by default) or an absolute path. `original.action: keep` leaves the original in place and installs the AV1 beside it as `<name>.av1<extension>`. Neither overwrites an existing file; a taken name gets a number, as in `movie (2).mp4`.
- `finalize --ready` says what will happen to the originals before it asks for confirmation.

### Changed

- `queue discover` never scans the archive directory, and it skips an original that `keep` left in place until that file changes. It reports these as "Already converted".

## [0.4.0] - 2026-10-12
### Added

- `cleanup` lists temporary outputs that no job will use again, such as those of completed, failed or cancelled jobs, earlier attempts and interrupted local encodes, then deletes them after one confirmation. `--dry-run` only lists them.

### Changed

- Resuming an interrupted local encode now deletes the partial output before it starts the new attempt. `status` describes what resuming each interrupted job will do.

### Fixed

- Resuming a local job that was interrupted while encoding or validating no longer fails with "Cannot change job from Queued to Encoding" (or "to Validating").
- `doctor` no longer fails under the Hetzner lifecycle when no managed worker is running. It checks the Hetzner token and API access, and warns that the remote host checks were skipped. A missing token or failed API call is now a dependency failure (exit code 4) instead of an unexpected failure.

## [0.3.0] - 2026-10-12
### Changed

- **Breaking:** an unknown config key is now an error naming its full path, such as `processing.remoteSsh.hetzner.locations`, instead of being ignored. Remove stale keys such as `logging` and `validation.decodeTestMode`, and fix misspelled ones, before upgrading.

### Fixed

- A config that YAML cannot read now exits with code 3 and names the setting at fault, instead of reporting an unexpected failure with code 1.
- CRF search no longer fails with ab-av1 0.11 or newer, which defaults to fractional CRF steps for svt-av1. VideoOptimiser now asks for whole-number steps on every version.

## [0.2.0] - 2026-10-11
### Added

- Remote SSH processing: CRF search and encoding can run on an Ubuntu worker while validation and finalisation stay local. See `docs/remote-ssh.md`.
- Managed Hetzner workers: VideoOptimiser creates, bootstraps, reuses and deletes a CX43 and its Primary IPv4. `worker create`, `worker status` and `worker delete` manage it by hand.
- `processing.remoteSsh.hetzner.locations` lists fallback locations, tried in order when Hetzner reports no capacity.
- `queue retry <job-id>` queues a fresh job for a failure caused by a changed remote configuration. The failed job stays in history.
- `queue discover` caches ffprobe results in SQLite, so later discoveries skip unchanged files.

### Changed

- **Breaking:** `validation.decodeTest` now takes the mode directly: `sampled` (default; start, middle and end), `full`, or `none`. `validation.decodeTestMode` is removed. An old config with `decodeTest: true` or `false` fails `config validate` until it is updated.
- **Breaking:** removed `validation.runFfprobe`, `requireExpectedVideoCodec`, `requireDurationTolerance`, `requireStreamParity` and `requireNonZeroLength`. They had no effect, and those checks always run.
- Validation now enforces `savings.minimumBytesSaved` and decode-tests each output with ffmpeg before it can replace its original. A truncated or corrupt output now fails validation.
- With `deleteAfterRun: true`, a managed worker is deleted only after a clean run. Failed or interrupted runs keep it; an interactive console offers one deletion prompt.
- A job that has started keeps its processing mode, remote identity and workspace. If the configuration has changed since, the job fails before a worker is created.
- Moved to .NET 10 LTS, since .NET 9 support ends on 2026-11-10. Source builds now need the .NET 10 SDK.

### Fixed

- Encoding no longer fails on sources with data streams, such as QuickTime timecode tracks; they are dropped from the output.
- A Hetzner capacity error now explains which locations were tried and links to the Hetzner Console, instead of reporting an unexpected failure.
- `queue discover` reports cache reuse every 100 files instead of per file, and loads the cache in bulk, so large libraries are discovered faster.

## [0.1.0] - 2026-07-23
### Added

- Native AOT single-file Windows executable release.
- Persistent queue processing with explicit safe finalisation.
- Rule-based media eligibility.
