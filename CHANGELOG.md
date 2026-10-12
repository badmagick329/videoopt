# Changelog

## [Unreleased]
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
