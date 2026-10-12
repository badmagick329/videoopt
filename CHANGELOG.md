# Changelog

## [Unreleased]
### Added

- An `immich:` configuration section for the Immich source: the server's LAN URL, where the API key loads from (`VIDEO_OPTIMISER_IMMICH_API_KEY`, then `.env`), the age gate, whether to skip favourites, and the staging directory. With `immich.baseUrl` set, `doctor` checks that the server runs Immich 3, that it accepts the key, and that the staging directory is writable. A command that needs a permission the key lacks stops with exit code 3 and names it.
- `immich run` converts old videos from the Immich library. It first moves back to Immich's trash any converted original the phone uploaded again and lists those files to delete from the phone. Then it downloads each eligible video taken more than `immich.minimumAgeDays` ago to the staging directory and processes it as `queue run` does. `--first` converts one video; while an Immich job is still open, it converts that one and downloads nothing new.
- `immich finalize` lists the converted Immich videos with their capture date and sizes and asks once. It then uploads each AV1 as a new asset with the original's albums, tags, favourite, description and rating, and moves the original to Immich's trash. If finalisation stops partway, the next run continues it.
- `queue cancel <job-id>` also cancels a job that is ready to finalise.
- `status` and `history` show an Immich job's asset IDs; their `--json` output adds `source`, `immichAssetId` and `immichNewAssetId`.
- In local mode, `doctor` checks that ab-av1 is 0.11.7 or newer and that FFmpeg has the libsvtav1 encoder, the libvmaf filter and the 10-bit `yuv420p10le` format. In both modes it checks that FFmpeg can decode AV1 with libdav1d, which validation's decode test needs, unless `validation.decodeTest` is `none`.
- [docs/immich.md](docs/immich.md) explains Immich setup and each round. [docs/agent-guide.md](docs/agent-guide.md) tells an AI agent how to run VideoOptimiser for a user, including which commands need the user's go-ahead.
- The release zip includes `docs/`, `.env.example` and `scripts/bootstrap-remote-worker.sh`, so managed Hetzner processing works from a release download.
- `--local` and `--remote` on `queue run`, `process` and `immich run` set `processing.mode` for one run, without editing the config; the `Encoding on:` line ends with `(from --local)` or `(from --remote)`. A job started in the other mode is skipped, printed as `Skipped <file>: started on ...`, and resumed by the next run without the flag; it does not count as a failure. Both flags together, or either on another command, exit `2`.
- `queue run`, `process` and `immich run` first print where encoding runs, before any download or encoding: `Encoding on: this PC.`, the SSH target, a Hetzner server still running from an earlier run, or `a Hetzner server created for this run, billed hourly (tries cx43, cx53, cpx42, ccx33; max EUR 0.30/h).` `doctor` reports the same line as `EncodingLocation` in a new `Processing` category.

### Changed

- **Breaking config change:** `processing.remoteSsh.hetzner.serverType` is replaced by `serverTypes`, a list tried in order, each in every configured location, so a sold-out type falls through to the next. The default is `["cx43", "cx53", "cpx42", "ccx33"]`. A config that still has `serverType` fails to load and names the replacement. Before creating a server, VideoOptimiser skips any type that is sold out, not x86, deprecated, or smaller than `minimumCpuCount` or `minimumAvailableMemory`, and prints the type, location and hourly price it creates. Jobs started on a managed worker before this change still resume.
- **Breaking for scripts:** a managed Hetzner `queue run`, `process` or `immich run`, and `worker create`, now ask `Create the server? [y/N]` before creating a server. The prompt shows the first type's hourly price and, when a fallback costs more, the most a fallback may cost; one `y` covers every fallback. Any other answer, or no input, creates nothing: the command prints `No server was created.`, exits `0`, and leaves the jobs queued. Scripts must pipe `'y'`. Reusing a running server never asks.
- New `processing.remoteSsh.hetzner.maxHourlyPrice` skips any server type and location costing more, in EUR per hour including VAT. The default is `0.30`; `null` removes the cap.
- When no Hetzner server can be created, the error lists each server type, the locations tried for it, and why each failed.
- `worker status` and `worker create` show the worker's server type and location.
- `queue run` processes only jobs from the watch roots. Immich jobs run with `immich run`.
- `watch.roots` may be empty when `immich.baseUrl` is set, so an Immich-only config passes `config validate` and `doctor`. `queue discover` then says it needs a watch root.
- The README is rewritten to be shorter and task-first.
- A managed Hetzner worker now installs ab-av1 0.11.7, the version CI tests, instead of 0.10.4. `doctor` rejects a remote worker with an older ab-av1. A worker created before this change keeps 0.10.4: delete it with `worker delete` and the next run creates a new one.

### Fixed

- `queue discover` no longer queues an original again after its job failed or was cancelled, which encoded a failing file again on every run and undid `queue cancel`. It skips the original until the file changes and reports it as "Previously failed or cancelled". Failures caused only by the file not being ready, being ineligible, or lying outside the watch roots still requeue.
- Creating a managed Hetzner worker from a Windows checkout with Git's `core.autocrlf` no longer sends the bootstrap script with CRLF line endings, which bash on the worker cannot run.
- `queue retry <job-id>` now queues any failed or cancelled job's original again, not only one whose remote configuration changed.
- The AV1 output now keeps the original's container metadata: its capture date (`creation_time`), GPS location, and phone make and model (`com.android.*`). Before, every converted file lost them, so photo libraries dated it by the conversion and dropped it from the map. Files already converted stay without them.

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
