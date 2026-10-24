# NtfyBar for Windows

A Windows tray client for [ntfy](https://ntfy.sh) whose main job is a **shared clipboard** between
an Android phone, a Mac and a Windows PC: send text, images and files from one device and have them
on the clipboard of the others. Windows sibling of [NtfyBar for macOS](https://github.com/roypadina/NtfyBar),
same `config.json` field names and the same [wire protocol](docs/PROTOCOL.md).

**Status: BETA (0.x).** Unsigned: Windows SmartScreen warns on first start (see Install).
Manual test plan: [docs/TESTPLAN.md](docs/TESTPLAN.md).

## Install

1. Download `NtfyBar-win-x64-vX.Y.Z.zip` from [Releases](https://github.com/roypadina/NtfyBar-Windows/releases).
2. Unzip it to `%LOCALAPPDATA%\Programs\NtfyBar\` (paste that path into Explorer's address bar;
   create the folder if it is missing). Any folder works, but keep it in one place: Start with
   Windows and Send To point at the exe.
3. Double-click `NtfyBar.exe`. The exe is not code-signed, so Windows shows **"Windows protected
   your PC"**: click **More info → Run anyway**. This happens again after each update.
4. The NtfyBar icon appears in the tray. If you don't see it, click the **^** arrow next to the
   clock and drag the icon onto the taskbar.

No installer, no admin rights, no .NET runtime needed (the exe is self-contained, x64).

## First run

On the first start the **Settings** window opens:

1. **Server URL**: your ntfy server, e.g. `https://ntfy.example` (or `https://ntfy.sh`).
2. **Sign-in**: *None*, *Username + password*, or *Access token*. Passwords and tokens are stored
   encrypted for your Windows user (DPAPI) and are never shown again; leave the box empty to keep the
   saved one.
3. **Topics**: one row per topic. *Label* is your name for it (shown in menus and toasts); *Topic*
   is the ntfy topic name. For a shared-clipboard topic tick all of these:

   | Column | What it does |
   |---|---|
   | Enabled | Subscribe to the topic. |
   | Notify | Show a toast for each message. |
   | Copy text | Put received text on the clipboard. |
   | Download files | Save received files and pictures to the downloads folder. |
   | Copy files | Put a downloaded file on the clipboard (a picture as a picture). |
   | Process missed | Also copy/download messages that arrived while the PC was off. Leave it off for a clipboard topic. |

4. **Test connection** checks the server, the sign-in and read access to the topics.
5. Optional: device tag (default `ntfybar-win`, the sender id other devices see), default title
   (default: the PC's name), the Send Clipboard hotkey, the downloads folder
   (default `Downloads\NtfyBar`), **Start with Windows**, **Add to Explorer's Send To menu**.
6. **Save**. The tray tooltip and the first menu line show the connection status.

## Use

Right-click the tray icon:

| Menu item | What it does |
|---|---|
| status line | Connected / Reconnecting… / Sign-in rejected — check Settings |
| Recent | The last 20 messages. Click: text → copied; file or picture → opened (downloaded first if needed). Your own sends show as "Sent · …". |
| Send Clipboard to “…” | Sends the clipboard to the topic you last sent to: files (as files) first, else a picture (as PNG), else text. Before the first send it opens Publish…. |
| Publish… | Compose a message to any topic: title, extra tags, priority, message, files (Add…, or drag files onto the window). **Ctrl+Enter** sends. |
| Open Downloads | The folder with received files. |
| Settings… | Everything from First run. |
| Start with Windows | Starts NtfyBar when you sign in. |
| Quit NtfyBar | |

- **Hotkey** (default **Ctrl+Alt+Shift+C**): same as Send Clipboard. If another app owns the
  combination, a toast says so; pick another in Settings.
- **Explorer → right-click files → Send to → NtfyBar**: sends the files to the topic you last sent
  to (at most 20 files, 100 MB each; folders: zip them first).
- **Receiving**: text is copied and a toast shows it with a **Copy** button. Long text (sent as a
  `clip.txt` / `attachment.txt` attachment) is fetched and copied as text. Pictures and files are saved to
  the downloads folder and put on the clipboard; their toast has **Open** and **Open folder**.
  Several files within a few seconds collapse into one "N files saved" toast; the last one is on the
  clipboard.
- Every send ends with a silent toast, e.g. *Sent to “Clipboard” · 2 files* or *Couldn't send: server unreachable*.

## How the shared clipboard works

Every device subscribes to the same topic and publishes to it only when you act (hotkey, menu,
share sheet, Send To): nothing is sent just because you copied something, so there are no loops.
Each sender puts its id in the first tag (`ntfybar-win` here), so a device recognises its own
messages coming back and ignores them.

When the PC was off or asleep, the server replays what it cached. Items at most 2 minutes old are
handled as live (copied); older ones are not copied: one toast says "N items … while away" and
they wait in **Recent**. Attachments expire on the server after a few hours (ntfy default 3 h);
then the toast says "attachment expired on the server".

Any app that speaks the [protocol](docs/PROTOCOL.md) can take part: NtfyBar for macOS, the ntfy
Android app's Share sheet, clipboard managers, phone automation.

## Files and privacy

- Settings: `%APPDATA%\NtfyBar\config.json` (password/token encrypted with DPAPI for your Windows
  user), stream position in `state.json` next to it.
- Log: `%LOCALAPPDATA%\NtfyBar\logs\ntfybar.log` (rotates at 1 MB). It never contains message text,
  titles, topic names, the server address or credentials: ids, counts, kinds and HTTP status codes only.
- Received files: the downloads folder. They are marked as downloaded from the internet, like a
  browser does, so Windows asks before running a received program and Office opens received
  documents in Protected View.
- The topic name works like a password: anyone who knows it (and may read on your server) gets
  your clipboard. Don't post it in screenshots.

## Uninstall

1. Tray icon → **Quit NtfyBar**.
2. Run `NtfyBar.exe --uninstall` (Win+R, then the full path, e.g.
   `%LOCALAPPDATA%\Programs\NtfyBar\NtfyBar.exe --uninstall`). It removes Start with Windows, the
   Send To entry and the notification registration.
3. Delete the NtfyBar folder, and `%APPDATA%\NtfyBar` (settings) and `%LOCALAPPDATA%\NtfyBar` (logs)
   if you don't want to keep them. Received files stay in the downloads folder.

## Build from source

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
  own-echo, publisher and sends, attachment downloads, Recent, hotkey parsing, rolling log.
- `src/NtfyBar.App` — WinForms tray app: icon, menu, toasts, clipboard, Publish and Settings
  windows, global hotkey, single instance, Send To, autostart, DPAPI.
- `tests/NtfyBar.Core.Tests` — xUnit tests for Core (fake HTTP handler; no network).

## License

MIT, see [LICENSE](LICENSE).
