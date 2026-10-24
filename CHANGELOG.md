# Changelog

## 0.1.0 — Unreleased (first BETA)

- Shared clipboard with ntfy: subscribes to your topics on one server (no sign-in, username +
  password, or access token; secrets encrypted with DPAPI) and follows the
  [shared clipboard protocol](docs/PROTOCOL.md).
- Receive: text is copied (line breaks as CRLF) with a toast and a **Copy** button; long text sent
  as `clip.txt` / `attachment.txt` is fetched and copied as text; pictures and files are saved to
  `Downloads\NtfyBar` (marked as from the internet) and put on the clipboard as a picture or a file,
  with **Open** / **Open folder** toasts; several files within 5 s collapse into one "N files saved"
  toast; expired attachments are reported. Per-topic Notify / Copy text / Download files /
  Copy files / Process missed.
- Catch-up: items that arrived while the PC was off are copied only when at most 2 minutes old;
  older ones go to Recent with one "N items while away" toast. Own sends are recognised (message
  id or the `ntfybar-win` first tag) and never acted on.
- Send: tray **Send Clipboard** and a global hotkey (default Ctrl+Alt+Shift+C) send the clipboard
  (files, else picture as PNG, else text) to the last topic; **Publish…** window (topic, title,
  tags, priority, message, files with Add… and drag and drop, Ctrl+Enter); Explorer **Send To →
  NtfyBar**. Limits checked before upload: 20 files, 100 MB each.
- Tray menu: status, Recent (last 20; click copies text or opens the file), Open Downloads,
  Settings…, Start with Windows, Quit.
- Settings window: server, sign-in, topics grid, device tag, default title, hotkey, downloads
  folder, Start with Windows, Send To, **Test connection**; opens on first run. Saving retries a
  stream stopped by a sign-in error.
- Reconnects after sleep; keepalive watchdog and backoff; resumes with `since` after a restart.
- Rolling log in `%LOCALAPPDATA%\NtfyBar\logs` without message text, topic names, hosts or
  credentials; unexpected errors are logged and the app keeps running.
- `NtfyBar.exe --uninstall` removes autostart, Send To and the notification registration.
- Self-contained single exe (win-x64) in a zip; CI and releases on Windows.
