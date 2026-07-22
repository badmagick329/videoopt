# Deferred work

## Interrupted temporary outputs

Interrupted encodes leave partial files in `<source>\.video-optimiser`. Resume creates a fresh attempt and does not reuse the partial file.

Add a safe cleanup command or policy that lists and removes only known abandoned temporary outputs. Make resume/status output state this behaviour clearly.

## Original-file policies

Implement `original.action: archive` and `original.action: keep`.

- `archive`: move the original to the configured archive directory using collision-safe naming, then install the validated AV1.
- `keep`: retain the original and install the validated AV1 alongside it using a distinct output filename.
