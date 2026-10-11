# Changelog

## [Unreleased]

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
