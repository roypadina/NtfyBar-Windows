# NtfyBar for Windows

A Windows tray client for [ntfy](https://ntfy.sh) whose main job is a **shared clipboard** between
an Android phone, a Mac and a Windows PC: send text, images and files from one device and have them
on the clipboard of the others. Windows sibling of [NtfyBar for macOS](https://github.com/roypadina/NtfyBar),
same `config.json` field names and the same [wire protocol](docs/PROTOCOL.md).

**Status: BETA (0.x).** Core (subscribe, publish, attachments, own-echo, catch-up) is done and
tested; the tray UI is being built. Unsigned: Windows SmartScreen will warn
("Windows protected your PC" → More info → Run anyway).

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build NtfyBar.sln
dotnet test
dotnet publish src/NtfyBar.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The App project builds on macOS/Linux too (`EnableWindowsTargeting`), but release builds come from
Windows CI (`.github/workflows`), which embeds the icon and the GUI subsystem correctly.
Live tests against ntfy.sh (random throwaway topic): `NTFYBAR_LIVE_TEST=1 dotnet test --filter Live`.

## Layout

- `src/NtfyBar.Core` — all logic, no Windows APIs: config/state, stream client, classification,
  own-echo, publisher, attachment downloads, rolling log.
- `src/NtfyBar.App` — WinForms tray app: icon, menu, toasts, global hotkey, single instance, DPAPI.
- `tests/NtfyBar.Core.Tests` — xUnit tests for Core (fake HTTP handler; no network).

Config lives in `%APPDATA%\NtfyBar\config.json` (password/token encrypted with DPAPI),
logs in `%LOCALAPPDATA%\NtfyBar\logs` (never message content or credentials).

## License

MIT, see [LICENSE](LICENSE).
