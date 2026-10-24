# Agent task: test, fix and ship NtfyBar for Windows (v0.1.0 BETA)

Hand-off for a Claude Code session on Roy's Windows PC. It holds everything from the design and build
session (2026-10-25, on the Mac) needed to continue: the spec, the plan, what is done, what is risky,
and how to test, fix and release. Read this file fully before you act.

> Public repo: this file names no real server, topic, host, account or token. Roy gives you the
> server address and topic name in chat, and types his password into the app himself.

---

## 1. Mission and status

**Goal:** a Windows tray app that does for Windows what NtfyBar does on the Mac. Its main job is a
**shared clipboard**: text and files go between Roy's Android phone, his Mac and this PC over his
self-hosted ntfy server. It replaces his KDE Connect setup for clipboard, text and file transfer.

| Part | State |
|---|---|
| Design (approved by Roy) | done; summary in §4 |
| `NtfyBar.Core` (logic) | done; 179 xUnit tests green (2 live tests skipped by default) |
| `NtfyBar.App` (WinForms tray, toasts, clipboard, forms, Send To, hotkey) | **compiled only, never run.** Built on a Mac with `EnableWindowsTargeting`; CI on `windows-latest` builds it and runs the Core tests |
| Design review of the implementation | done: verdict FIX-THEN-TEST, all 6 findings fixed (`98e3986`) |
| CI | green on `main` (`.github/workflows/ci.yml`: test, publish single-file exe, zip artifact `NtfyBar-win-x64-<sha>`) |
| Manual test on Windows | **NOT DONE: your first job** (`docs/TESTPLAN.md`) |
| First release `v0.1.0` | not done; needs Roy's explicit OK after the test passes |
| macOS NtfyBar | v1.3.1 has publish ("Send Clipboard", "Publish…"). Same wire protocol |
| Phone | Android ntfy app + Tasker, both directions working (§3.3) |

**Your job, in order:**
1. Set up the dev environment (§6) and build + test locally.
2. Run `docs/TESTPLAN.md` with Roy, risky items first (§7).
3. Fix what fails. Small diffs, a test for any Core change. Re-run the affected tests.
4. Update `CHANGELOG.md` and `README.md` if behaviour changed. Record the test results (§7.4).
5. Ask Roy for the release OK, then release `v0.1.0` (§8).

---

## 2. Ground rules (Roy's, they apply here)

- **Smallest diff that fully solves it.** No drive-by refactors, no new packages without a reason.
  Plain WinForms, no MVVM/DI frameworks.
- **Never `ConfigureAwait(false)`** (Roy's .NET rule).
- **Git:**
  - Run `git status -sb` right before every commit, and stage files by path (never `git add -A` / `git add .`).
  - Commit identity: `Roy Padina <4943097+roypadina@users.noreply.github.com>` (GitHub noreply). Set it repo-locally if this clone doesn't have it.
  - GitHub account `roypadina`. `main` is not protected; push only what Roy has seen working.
  - End commit messages with `Co-Authored-By: Claude <noreply@anthropic.com>`.
- **Net-new needs Roy's explicit OK:** a release, a tag, a new branch on the remote, winget/scoop manifests, a new repo.
- **Secrets:** never print, log or commit a password, token, topic name or server address. Never ask Roy to paste his password into chat; he types it into the app's Settings window. Logs must not contain message bodies, secrets or topic names (there is a test for this; keep it green).
- **Public repo hygiene:** code, tests, docs and screenshots use `https://ntfy.example` / `example-topic`. No real hostnames, IPs, device names or home paths.
- **Don't drive Roy's desktop without asking.** Launching the app, clicking, or sending test messages to his real topics: ask first, or give Roy the steps. He has ADHD, so give numbered steps, one action per step, the next action first, no essays.
- **Library docs:** for any SDK/library question (WinForms, Toolkit notifications, Windows App SDK, System.Text.Json) use Context7 if available, else official docs. Don't guess APIs.
- **Toasts don't work in an elevated (admin) process.** Run the app and your test launches non-elevated.

---

## 3. The system around the app

### 3.1 Devices and server
- **Server:** Roy's self-hosted ntfy **v2.33.1**, reachable only on his LAN and over Tailscale (from the internet it returns 403). Settings:
  - `auth-default-access: deny-all`: everything needs a login.
  - Roy signs in as **his own user (admin)** with username + password (basic auth). Roy decided there are no per-device ntfy users.
  - Attachments up to **100 MB** per file, deleted after **3 h**.
  - Message cache 12 h.
  - Attachment URLs (`/file/<id>`) download **without** auth on this server (verified 2026-10-25). Accepted risk: random ids, 3 h life, LAN/tailnet only.
- **Clipboard topic:** one topic on that server (display name "Clipboard"; Roy tells you the name). The body is the raw payload with no footer. The **sender is identified by the first tag only.**
- **Mac:** NtfyBar 1.3.1. Auto-copies Clipboard-topic text and files; sends with tag `ntfybar-mac`; ignores its own echo. MaccyPlus (a clipboard-history app) also sends with tag `maccyplus`.
- **Phone:** Android ntfy app (signed in as Roy) + Tasker.
  - Receive: Tasker auto-copies PC → phone text, skips items older than 120 s, and fetches `clip.txt`/`attachment.txt` long text.
  - Send: a Quick-Settings tile sends the phone clipboard through the ntfy app. Those sends are **untagged and untitled** (Tasker's Send Intent has only 3 extras).
  - Files from the phone: Share → ntfy (untagged).
- **This PC:** sender tag `ntfybar-win`; the default title is the machine name.

### 3.2 Wire protocol (both apps; source of truth `docs/PROTOCOL.md`)
| Payload | Request |
|---|---|
| Text ≤ 4000 bytes UTF-8 | `POST <server>/` JSON `{topic, message, title, tags, priority}` + `actions:[{action:"copy",label:"Copy",value:<text>}]` when ≤ 2000 bytes (one-tap Copy on the phone) |
| Text > 4000 bytes | `PUT <server>/<topic>?filename=clip.txt&title=…&tags=…&message=<first 80 chars>…`, body = UTF-8 bytes |
| File | `PUT <server>/<topic>?filename=<name>&title=…&tags=…&message=<name>`, one message per file, ≤ 20 files, ≤ 100 MB each (refused before upload) |
| Clipboard picture | as a file named `clip.png` |

- `title`/`tags`/`filename`/`message` go as query params (non-ASCII safe). Priority goes in the `X-Priority` header, auth in `Authorization`. Tags = sender tag first, then user tags. Text is sent with LF line endings.
- **Treat-as-text on receive:** an attachment with `type` starting `text/plain`, size ≤ 1 MB, named `clip.txt` or `attachment.txt` is downloaded and handled as **text**. The server turns any text over 4096 bytes into `attachment.txt`. Any other `.txt` stays a file.
- **Own echo:** ignore messages whose id came back from our own publish (2-min memory) or whose first tag is our device tag.
- **Catch-up:** messages older than the stream's `open` event are catch-up. Catch-up younger than 120 s counts as live. Older ones are never auto-copied: they go to Recent + one summary toast, unless the topic has `processMissed`.
- **No auto-push on copy** in the MVP: every send is user-initiated (tap, hotkey, menu, Send To), so loops are impossible. Auto-push is "offer later" (§9).

### 3.3 Expected round trips (what "working" means)
1. **Phone → PC:** phone tile or Share → PC toast + text in the Windows clipboard (files saved to `Downloads\NtfyBar`, put on the clipboard if the topic copies files).
2. **PC → phone/Mac:** hotkey / tray "Send Clipboard" / Explorer Send To / Publish… → phone auto-copies text (Tasker), Mac auto-copies text and files.
3. **Mac → PC:** NtfyBar "Send Clipboard" on the Mac → PC clipboard.
4. Own sends never come back as a copy or a toast.

---

## 4. Spec (approved design, condensed)

**MVP scope (v0.1.0):**
- **Subscribe:** one server (URL + username/password or token), N topics, NDJSON stream `GET /<t1,t2>/json?since=<persisted>`.
  - 90 s keepalive watchdog, exponential backoff.
  - 401/403/3xx stops the stream until Settings change.
  - Reconnect on wake.
- **Notifications:** a toast per live message (sender as title, preview as body, buttons Copy / Open / Open folder), a summary toast for catch-up, "Sent" toasts, a per-topic `notify` toggle.
- **Clipboard receive:** per-topic `copyToClipboard`, `downloadAttachments`, `copyAttachment` and `processMissed` (same names and meaning as the Mac); the 120 s freshness window.
- **Files:** saved to `%USERPROFILE%\Downloads\NtfyBar` (configurable).
  - Windows-safe names (reserved names, `..`, bidi characters and control chars are stripped), unique `name (2).ext`.
  - Server auth only to the same origin; files over 100 MB are skipped.
  - Mark-of-the-Web is set on saved files.
- **Publish:**
  - Tray **Send Clipboard** and a global hotkey (default `Ctrl+Alt+Shift+C`) send to the last topic. Clipboard order: files → picture → text.
  - **Publish…** form: topic, title, tags, priority, message, files (drag & drop, Add…), Ctrl+Enter sends.
  - Explorer **Send To → NtfyBar** (`.lnk` in the SendTo folder → `NtfyBar.exe --send <files>` → named pipe to the running instance).
- **Tray menu:** status line, Recent (last 20), Send Clipboard, Publish…, Open Downloads, Settings…, Start with Windows, Quit.
- **Settings form:**
  - Server URL and sign-in (None / Username + password / Token); secrets are DPAPI-protected and never shown back.
  - Topics grid, device tag, title, hotkey, downloads folder, Start with Windows, Send To, **Test connection**.
  - Opens on first run.
- **System:**
  - Single instance (mutex `Local\…` + named pipe, current user only); autostart via HKCU Run.
  - Rolling logs in `%LOCALAPPDATA%\NtfyBar\logs`; `NtfyBar.exe --uninstall` removes the toast registration.

**Out of the MVP (Mac-only or later):** rules engine, JS plugins, popups, SQLite history, shell commands, quiet hours, several servers in the UI, auto-push on copy, winget/scoop.

**Stack decisions:**
- .NET 10 LTS with WinForms `NotifyIcon`.
- Toasts via `Microsoft.Toolkit.Uwp.Notifications` 7.1.3, which works unpackaged. Fallback if it fails on .NET 10 single-file: Windows App SDK `AppNotificationManager` (bigger, needs the runtime/bootstrapper).
- DPAPI (`ProtectedData`, CurrentUser) for secrets.
- `System.Text.Json`; an own rolling file logger; xUnit with a fake `HttpMessageHandler`.

**Config:** `%APPDATA%\NtfyBar\config.json` uses a subset of the Mac schema.
- Fields: `servers[] {id,url,auth{type:none|basic|token,user,pass,token}}`, `topics[] {id,serverId,topic,label,enabled,notify,copyToClipboard,downloadAttachments,copyAttachment,processMissed}`, `publish {deviceTag,title,lastTopicId,hotkey}`, `downloadDir`, `launchAtLogin`.
- Secrets are stored as `dpapi:<base64>`.
- Decoding is tolerant: unknown fields are ignored and each field falls back to its default on its own.
- An invalid file is never overwritten: the first save moves it to `config.json.invalid`.
- `state.json` holds the last message time/id per server.

**Distribution:**
- GitHub Release zip `NtfyBar-win-x64-v<version>.zip` (single self-contained exe + README + LICENSE).
- **Unsigned.** Microsoft's Artifact Signing is open to individual developers only in the US/Canada, so SmartScreen shows "Windows protected your PC → More info → Run anyway" on every new version. The README says so.
- Suggested install folder: `%LOCALAPPDATA%\Programs\NtfyBar\`.

---

## 5. Code map

```
NtfyBar.sln
Directory.Build.props        <Version>0.1.0</Version> (exe, zip name and tag all come from here), EnableWindowsTargeting, warnings as errors
src/NtfyBar.Core/            net10.0, no Windows APIs, all logic
  Engine.cs                  Start/Reload/UpdateConfig/ReconnectAll/Stop; events MessageReceived/StatusChanged/MissedSummaryReady (raised on the thread that called Start); Publish*/Send/Download*
  NtfyStream.cs              NDJSON stream, keepalive watchdog, backoff, auth stop, since persistence
  Clip.cs                    classification: Text / TextAttachment / Image / File / ExpiredAttachment; Handling Live / Missed / OwnEcho
  Publisher.cs, Outgoing.cs  the §3.2 protocol, limits, error mapping (401/403 not allowed, 413 too large, 429 rate limited, unreachable)
  Attachments.cs             download: same-origin auth, size cap, sanitised names, deadline
  Config.cs, ConfigStore.cs, Json.cs   tolerant config/state, ISecretProtector
  HotkeySpec.cs, Recent.cs, Log.cs, NtfyMessage.cs
src/NtfyBar.App/             net10.0-windows10.0.17763.0 WinForms (WinExe)
  Program.cs                 [STAThread], single instance, --send / --uninstall / toast activation
  TrayContext.cs             tray menu, engine wiring, receive actions, toasts, Recent, burst collapse
  Toasts.cs                  ToastContentBuilder wrapper, OnActivated → UI thread (init guarded by try/catch)
  ClipboardIO.cs             STA clipboard read/write with retry (text, image, CF_HDROP)
  PublishForm.cs, SettingsForm.cs
  HotKey.cs (RegisterHotKey, message-only window), SingleInstance.cs (named pipe), Shell.cs (Send To .lnk via WScript.Shell, HKCU Run, MotW), DpapiProtector.cs
tests/NtfyBar.Core.Tests/    xUnit; LiveTests.cs runs only with NTFYBAR_LIVE_TEST=1 (random public ntfy.sh topic, never Roy's server)
docs/PROTOCOL.md             shared wire protocol (keep in sync with the Mac app)
docs/TESTPLAN.md             manual test plan: risky table, I1–I6, R1–R8, P1–P7, S1–S10, report-back
.github/workflows/ci.yml     windows-latest: dotnet test + publish + zip artifact
.github/workflows/release.yml  on tag v*: same build, GitHub Release (prerelease for 0.x) with the zip
tools/make-icon.py           regenerates Resources/NtfyBar.ico
```

The macOS reference implementation is `roypadina/NtfyBar` (`Sources/NtfyBarCore`). When behaviour is
unclear, the Mac app is the reference: same field names, same rules.

---

## 6. Dev setup on this PC

1. Install the tools (skip what's there): `winget install Microsoft.DotNet.SDK.10`, `winget install Git.Git`, `winget install GitHub.cli`.
2. Clone: `gh repo clone roypadina/NtfyBar-Windows` (signed in as `roypadina`), then `cd NtfyBar-Windows`.
3. Build + test: `dotnet build NtfyBar.sln -c Release` (expect 0 warnings), `dotnet test NtfyBar.sln -c Release` (expect 179 passed, 2 skipped).
4. Optional live check: set `NTFYBAR_LIVE_TEST=1`, then `dotnet test --filter Live`. It uses a random public ntfy.sh topic, never Roy's server.
5. Publish the exe exactly like CI:
   `dotnet publish src/NtfyBar.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`
   → `publish\NtfyBar.exe` (~60 MB).
6. Or take the CI artifact: `gh run download <run id> -R roypadina/NtfyBar-Windows` (latest green run on `main`).

Runtime paths: config `%APPDATA%\NtfyBar\config.json`, state `%APPDATA%\NtfyBar\state.json`, logs
`%LOCALAPPDATA%\NtfyBar\logs\`, downloads `%USERPROFILE%\Downloads\NtfyBar\`, Send To link
`%APPDATA%\Microsoft\Windows\SendTo\NtfyBar.lnk`, autostart `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

The app must run in Roy's interactive desktop session, non-elevated. An SSH session can't show the
tray or toasts.

---

## 7. Test (with Roy)

### 7.1 Prep
1. Roy unzips/copies `NtfyBar.exe` to `%LOCALAPPDATA%\Programs\NtfyBar\` and starts it. On a SmartScreen warning: More info → Run anyway.
2. Settings opens on first run. Roy enters:
   - the server URL;
   - sign-in Username + password (his own user), typed by Roy himself;
   - one topic row: his clipboard topic, label "Clipboard", with notify, copy text, download files and copy files ticked.
3. Test connection → OK. Save. The tray status should read connected.

### 7.2 Order (risky first)
1. **I1–I4**: start, first-run defaults, Settings + Test connection, stream connected. These gate everything and also prove the toast init survives.
2. **R1**: phone → PC text (toast + clipboard write + Copy button).
3. **P4**: Send To with 2 files (`.lnk` via COM → second launch → pipe → publish). Then **R4** and **S6** (toast click while the app isn't running).
4. Then the rest of `docs/TESTPLAN.md`: R2–R8, P1–P7, S1–S10. R7 takes 3+ hours (expiry); leave it for last or skip it with a note.

The risky table at the top of `docs/TESTPLAN.md` lists 14 Windows-only unknowns and which test covers each.

### 7.3 When something fails
- Read `%LOCALAPPDATA%\NtfyBar\logs\` first. The log has ids, counts and types, never bodies.
- **Toasts not shown or buttons dead** (risk 1/2): check that the process isn't elevated. Check `toasts unavailable: <type>` in the log (init is guarded). Check Settings → System → Notifications for an NtfyBar entry. If Toolkit 7.1.3 really fails on .NET 10 single-file, switch `Toasts.cs` to Windows App SDK `AppNotificationManager`: register `NotificationInvoked` before `Register()`, call `Unregister()` on exit, and note that it needs the Windows App SDK runtime or self-contained deployment. Ask Roy before taking that bigger change.
- **Clipboard:** all calls are on the STA UI thread with retries. If a format fails, test with Paint (picture), Explorer paste (files) and Notepad (text).
- **Send To:** check that the `.lnk` exists and its target is the exe with `--send`. A second instance must hand the files over the pipe and exit.
- Fix with the smallest diff; add or adjust a Core test where the logic is in Core; re-run `dotnet test`; re-publish; re-test.

### 7.4 Record results
Fill in `docs/TESTPLAN.md` §5 ("Report back") or add a results table (test id → pass/fail → note).
Commit fixes + results. Push to `main` once Roy has seen it working.

---

## 8. Release v0.1.0 (only after Roy's OK)

1. `CHANGELOG.md`: turn the 0.1.0 entry into `## [0.1.0] - <date>`. Check that `Directory.Build.props` says `0.1.0`.
2. Commit, push `main`, wait for CI green.
3. Tag and push: `git tag -a v0.1.0 -m v0.1.0` then `git push origin v0.1.0`. `release.yml` builds and creates the GitHub Release (prerelease for 0.x) with `NtfyBar-win-x64-v0.1.0.zip`.
4. Edit the release notes so the first line is `SHA-256: <sha256 of the zip>` (`certutil -hashfile <zip> SHA256`). Then short user-facing bullets, BETA, and the SmartScreen note.
5. Download the asset again and compare the SHA-256.
6. Tell Roy. The Mac session can then add the app to his marketing site and to the macOS NtfyBar README ("also for Windows"). Don't do those from here.

---

## 9. Offer later (Roy picks; don't build unasked)

- Auto-push on copy. Needs a loop guard: a private clipboard format marker on our own writes + a hash of the last received text.
- History window, multiple servers in the UI, config live reload.
- `ntfybar://` URL scheme, tray badge, ARM64 build, a framework-dependent small exe as a second asset.
- winget (`InstallerType: zip` + `NestedInstallerType: portable`) or a personal scoop bucket.
- Copy several received files to the clipboard at once (today the last one wins).
- Left-click on the tray icon (today it does nothing).

---

## 10. Decisions already made (don't re-ask)

| Topic | Decision |
|---|---|
| Repo | separate public repo `roypadina/NtfyBar-Windows` (MIT), not inside the Mac repo |
| Stack | .NET 10 + WinForms + Toolkit toasts, unsigned self-contained zip |
| Auth | Roy's own user with username + password (basic); token auth also supported. No per-device users |
| Sender identity | first tag: `ntfybar-win` (PC), `ntfybar-mac` (Mac), `maccyplus` (MaccyPlus); phone sends untagged |
| Auto-push on copy | not in the MVP |
| Catch-up | auto-copy only if ≤ 120 s old; older → Recent + summary toast |
| Phone | Tasker (not HTTP Shortcuts). Its setup is documented in Roy's notes, outside this repo |
| `/file/<id>` without auth | accepted risk |
| winget/scoop | not now |

Implementation deviations (accepted at review): the hotkey parser lives in Core (A–Z, 0–9, F1–F24,
NumPad, navigation keys; no punctuation); files are checked before the first upload; a
whitespace-only clipboard counts as empty; received text is copied with CRLF; received files get
Mark-of-the-Web; reconnect happens right after wake; `--uninstall` exists; first run pre-ticks Start
with Windows and Send To; Settings edits one server (extra servers in a hand-edited config are kept).

---

## 11. Known gaps and risks

- Every App-layer behaviour is unverified until §7. The test plan exists for exactly that.
- Clock skew over 120 s between the PC and the server makes every catch-up message "missed" (by design, but surprising).
- The download deadline (10 min, shared with the upload timeout) has no unit test.
- Several received files: only the last stays on the clipboard.
