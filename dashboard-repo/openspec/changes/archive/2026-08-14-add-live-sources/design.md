# Design
Same `Source` boundary as files: views never know the difference. Fetch is
one-shot; refresh is explicit. Failures are per-host and non-destructive.
