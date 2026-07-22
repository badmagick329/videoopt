# Deferred work

## Interrupted temporary outputs

Interrupted encodes leave partial files in `<source>\.video-optimiser`. Resume creates a fresh attempt and does not reuse the partial file.

Add a safe cleanup command or policy that lists and removes only known abandoned temporary outputs. Make resume/status output state this behaviour clearly.
