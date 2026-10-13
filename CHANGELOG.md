# Changelog

## [Unreleased]

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
