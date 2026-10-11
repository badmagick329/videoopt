# Changelog

## [Unreleased]
### Changed

- Moved to .NET 10 LTS, since .NET 9 support ends on 2026-11-10. Source builds now need the .NET 10 SDK.
- Validation now enforces `savings.minimumBytesSaved` and decode-tests each output with ffmpeg before it can replace its original. A truncated or corrupt output now fails validation.
- `validation.decodeTest` now takes the mode directly: `sampled` (default; start, middle and end), `full`, or `none`. `validation.decodeTestMode` is removed.
- Removed `validation.runFfprobe`, `requireExpectedVideoCodec`, `requireDurationTolerance`, `requireStreamParity` and `requireNonZeroLength`. They had no effect, and those checks always run.

## [0.1.0] - 2026-07-23
### Added

- Native AOT single-file Windows executable release.
- Persistent queue processing with explicit safe finalisation.
- Rule-based media eligibility.
