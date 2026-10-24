# Changelog

## Unreleased

- Core: `config.json`/`state.json` (tolerant decoding, DPAPI-protected password and token), NDJSON
  stream client (keepalive watchdog, backoff, `since` resume without duplicates, auth/redirect stop),
  catch-up with a 120 s freshness window, own-echo guard, clip classification incl. the
  treat-as-text rule, publisher (JSON text, long text as `clip.txt`, files, images), attachment
  downloads with Windows-safe names, rolling file log.
- App skeleton: tray icon and menu, toasts, global hotkey, single instance with argument forwarding.
- CI on Windows: tests, self-contained single-file build, zip artifact; release on `v*` tags.
