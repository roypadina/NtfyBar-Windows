# NtfyBar for Windows — manual test plan (v0.1.0 BETA)

One test at a time. Each numbered step is **one action**. "Expect" is what must happen; "Log" is a
line to look for at the end of the log file. A test passes when every Expect is true.

**Read the log:** Win+R → `%LOCALAPPDATA%\NtfyBar\logs\ntfybar.log` → Enter (opens in Notepad).
Close and reopen it to see new lines. Lines look like
`2026-10-25 14:03:07.120 +03:00 INFO  stream connected (1 topics)`.

**Names used below:** *your server* (your ntfy URL), *your clipboard topic* (the topic you share
the clipboard on, label "Clipboard" in Settings), *the phone* (ntfy app subscribed to your clipboard
topic), *the Mac* (NtfyBar for macOS on the same topic; optional, only for R2, R5, P1–P2).

## 0. Risky: check these first

The App (tray, toasts, clipboard, forms, Send To) has **only been compiled**, on a Mac. Nothing
in it has run yet. These are the parts only Windows can confirm, with the test that covers each:

| # | Risk | If it fails | Test |
|---|---|---|---|
| 1 | Toasts from the unpackaged single-file exe (Toolkit 7.1.3 on .NET 10): shown, buttons work, no sound on "Sent" toasts | No toasts or dead buttons: the toast stack must change | I4, R1 |
| 2 | Clicking a toast button while NtfyBar is **not running** starts it and runs the action | App doesn't start or ignores the click | S6 |
| 3 | "N files saved" replaces the first file toast (toast tag) instead of stacking | 3 separate toasts | R5 |
| 4 | Clipboard writes: text with line breaks, picture (pastes in Paint), file drop list (pastes in Explorer) | Paste gives nothing / wrong format | R1, R3, R4 |
| 5 | Clipboard read for Send Clipboard: files, screenshot as PNG, text | Wrong payload sent | P1, P3, P4 |
| 6 | Send To shortcut created through `WScript.Shell` COM from the single-file exe; Explorer passes the files; a second launch hands them to the running app | No Send To entry, or nothing sent | I3, P4 |
| 7 | Start with Windows (HKCU Run) | Not started at sign-in | S1 |
| 8 | Hotkey registration (parser moved to Core) | Hotkey dead | P5, S3 |
| 9 | Forms at 125–150 % scaling, Settings fits a laptop screen | Clipped or overlapping controls | S8 |
| 10 | Topics grid: typing a new row, ticking boxes, Remove | Edits lost on Save | I3, S4 |
| 11 | "From the internet" mark on received files | No "Unblock" checkbox in file Properties | R3 |
| 12 | Forms come to the front when opened from the hotkey | Window opens behind others (taskbar flashes) | P5 |
| 13 | Reconnect after sleep | Up to 90 s without messages after wake | S7 |
| 14 | `--uninstall` removes the notification registration | Leftover entry in Settings → Notifications | S9 |

Known by design: copying cells in Excel puts a picture and text on the clipboard; Send Clipboard
sends the **picture** (pictures win over text, same as the Mac).

## 1. Install and first run

### I1 Unzip and start
1. Download the zip from the CI run or the release.
2. Unzip it to `%LOCALAPPDATA%\Programs\NtfyBar\`.
3. Double-click `NtfyBar.exe`.
   - Expect: "Windows protected your PC".
4. Click **More info → Run anyway**.
   - Expect: the NtfyBar icon in the tray (maybe behind the **^** arrow) and the **Settings** window
     with a "Welcome!" line.
   - Log: `NtfyBar 0.1.0… starting`

### I2 First-run defaults
1. Look at the Settings window without changing anything.
   - Expect: one topic row labelled "Clipboard" with Copy text, Download files, Copy files ticked
     and an empty Topic; Device tag `ntfybar-win`; Default title = this PC's name; Send hotkey
     `Ctrl+Alt+Shift+C`; Downloads folder `C:\Users\<you>\Downloads\NtfyBar`; **Start with
     Windows** and **Add to Explorer's Send To menu** ticked.

### I3 Fill in and test
1. Server URL: type your server.
2. Sign-in: pick **Username + password**.
3. Type your username.
4. Type your password.
5. In the "Clipboard" row, click the Topic cell and type your clipboard topic.
6. Click **Test connection**.
   - Expect: "OK: the server answered and accepted the sign-in".
   - Log: `connection test: HTTP 200`
7. Click **Save**.
   - Expect: the window closes.

### I4 Connected
1. Hover the tray icon.
   - Expect: tooltip "NtfyBar — Connected".
2. Right-click the tray icon.
   - Expect: first line "Connected"; then Recent, Send Clipboard…, Publish…, Open Downloads,
     Settings…, Start with Windows (ticked), Quit NtfyBar.
   - Log: `stream connected (1 topics)`
3. In Explorer, right-click any file → **Send to**.
   - Expect: "NtfyBar" is in the list. (Press Esc.)

### I5 Restart keeps everything
1. Tray → **Quit NtfyBar**.
   - Expect: the icon disappears. Log: `NtfyBar stopped`
2. Start `NtfyBar.exe` again.
   - Expect: no Settings window; status "Connected" within a few seconds.

### I6 Secrets stay secret
1. Tray → **Settings…**
   - Expect: the Password box is empty and shows "•••••••• saved, type to replace".
2. Click **Test connection** (don't type a password).
   - Expect: "OK: …" (the saved password is used).
3. Click **Cancel**.
4. Win+R → `%APPDATA%\NtfyBar\config.json` → Enter.
   - Expect: `"pass": "dpapi:…"`, never your real password.

## 2. Receive (phone → PC)

### R1 Text
1. On the phone, share a two-line text to your clipboard topic (ntfy Share).
   - Expect: a toast titled "Clipboard" (or the sender's title) showing the text, with a **Copy** button.
   - Log: `message <id>: Text, Live` then `copied text (<n> chars)`
2. Open Notepad and press Ctrl+V.
   - Expect: the text, with both lines (line break kept).
3. Copy something else on the PC (any word, Ctrl+C).
4. Click **Copy** on the toast (or in the notification center, Win+N).
   - Expect: Ctrl+V in Notepad pastes the phone text again. Log: `toast activated: copy`
5. Copy something else again.
6. Tray → **Recent** → click the top item ("Clipboard · …").
   - Expect: Ctrl+V pastes the phone text again.

### R2 Long text (more than 4000 characters)
1. On the Mac (NtfyBar → Send Clipboard) or the phone, send a text longer than 4000 characters.
   - Expect: a toast with the first 80 characters and "…"; Ctrl+V in Notepad pastes the **whole** text.
   - Log: `message <id>: TextAttachment, Live`, `text attachment <id>: Saved`, `copied text (<n> chars)`

### R3 Picture
1. On the phone, share a photo (JPEG or PNG) to your clipboard topic.
   - Expect: toast "<name> saved and copied" with **Open** and **Open folder**.
   - Log: `message <id>: Image, Live` then `attachment <id>: Saved`
2. Open Paint and press Ctrl+V.
   - Expect: the photo is pasted as a picture.
3. Click **Open** on the toast.
   - Expect: the photo opens in the default viewer. Log: `toast activated: open`
4. Click **Open folder** on the toast (Win+N if it is gone).
   - Expect: Explorer opens `Downloads\NtfyBar` with the photo selected.
5. Right-click the photo → **Properties**.
   - Expect: "This file came from another computer…" with an **Unblock** box (risk 11).

### R4 File
1. On the phone, share a PDF to your clipboard topic.
   - Expect: toast "<name>.pdf saved and copied".
   - Log: `message <id>: File, Live` then `attachment <id>: Saved`
2. Open any Explorer folder (e.g. Desktop) and press Ctrl+V.
   - Expect: a copy of the PDF appears.

### R5 Three files at once
1. From the Mac (NtfyBar → Publish…, drop 3 files, Send) — or any sender that sends 3 files in a row.
   - Expect: **one** toast that ends as "3 files saved" / "The last one is on the clipboard." (the
     first file's toast is replaced, not stacked). All 3 files are in `Downloads\NtfyBar`.
   - Log: three `attachment <id>: Saved` lines.
2. Ctrl+V in an Explorer folder.
   - Expect: the **last** of the 3 files is pasted.

### R6 Catch-up after the PC was off
1. Tray → **Quit NtfyBar**.
2. On the phone, share the text "missed one".
3. Wait **3 minutes** (the 2-minute "still fresh" window must pass).
4. Start `NtfyBar.exe`.
   - Expect: the clipboard is **not** changed; toast "1 item on “Clipboard” while away".
   - Log: `message <id>: Text, Missed`
5. Tray → **Recent** → click "Clipboard · missed one".
   - Expect: Ctrl+V pastes "missed one".
6. Tray → **Quit NtfyBar**.
7. On the phone, share the text "fresh one".
8. Start `NtfyBar.exe` within 1 minute.
   - Expect: "fresh one" is copied and toasted like a live message.
   - Log: `message <id>: Text, Live`

### R7 Expired attachment (takes 3+ hours)
1. Tray → **Quit NtfyBar**.
2. On the phone, share a photo to your clipboard topic.
3. Wait longer than your server's attachment expiry (ntfy default 3 hours) but less than its cache
   time (default 12 hours).
4. Start `NtfyBar.exe`.
   - Expect: toast "1 item on “Clipboard” while away"; Recent shows "Clipboard · <name> (expired)".
5. Click that Recent item.
   - Expect: toast "<name>: attachment expired on the server".

### R8 Sign-in error
1. Tray → **Settings…**
2. Type a wrong password in the Password box.
3. Click **Save**.
   - Expect: toast "NtfyBar can't receive" / "Sign-in rejected (HTTP 401) — check Settings" with a
     **Settings…** button; tray status line shows the same.
   - Log: `stream stopped: HTTP 401; not reconnecting until settings change`
4. Click **Settings…** on the toast.
   - Expect: the Settings window opens.
5. Type the right password.
6. Click **Save**.
   - Expect: status "Connected". Log: `stream connected (1 topics)`

## 3. Publish (PC → phone / Mac)

### P1 Text, first send
1. In Notepad, type "hello from Windows", select it, Ctrl+C.
2. Tray → **Send Clipboard…**
   - Expect: the **Publish** window opens (no topic used yet), topic "Clipboard" selected.
3. Type "hello from Windows" in Message.
4. Press **Ctrl+Enter**.
   - Expect: status line "Sent to “Clipboard”"; the phone shows the message with a **Copy** button;
     the Mac copies it; the PC does **not** copy or toast it.
   - Log: `published message <id>`, `send ok`, then `message <id>: Text, OwnEcho`
5. Tray → **Recent**.
   - Expect: top item "Sent · Clipboard · hello from Windows".
6. Close the Publish window.
7. Copy "second hello" in Notepad.
8. Right-click the tray icon.
   - Expect: the item now reads **Send Clipboard to “Clipboard”**.
9. Click it.
   - Expect: silent toast "Sent to “Clipboard”"; the phone shows "second hello".

### P2 Long text
1. Copy a text longer than 4000 characters on the PC.
2. Tray → **Send Clipboard to “Clipboard”**.
   - Expect: "Sent to “Clipboard”"; the phone shows a `clip.txt` attachment whose message is the
     first 80 characters; the Mac copies the whole text as text.

### P3 Screenshot
1. Press Win+Shift+S and snip any area.
2. Tray → **Send Clipboard to “Clipboard”**.
   - Expect: "Sent to “Clipboard” · 1 file"; the phone shows `clip.png` with the picture.

### P4 Two files via Send To
1. In Explorer, select 2 small files.
2. Right-click → **Send to → NtfyBar**.
   - Expect: toast "Sent to “Clipboard” · 2 files"; the phone shows 2 messages (one per file).
   - Log: `Send To with 2 file(s)` then `send ok`
3. Select a folder → Send to → NtfyBar.
   - Expect: toast "Couldn't send: folders can't be sent" / "Zip the folder first."

### P5 Hotkey
1. Copy "via hotkey" in Notepad.
2. Press **Ctrl+Alt+Shift+C**.
   - Expect: toast "Sent to “Clipboard”"; the phone shows "via hotkey". Log: `send clipboard`
3. In Notepad type one space, select just that space, Ctrl+C.
4. Press **Ctrl+Alt+Shift+C**.
   - Expect: toast "Couldn't send: the clipboard is empty".

### P6 Not allowed
1. Tray → **Publish…**
2. Pick a topic your account may only read (or temporarily set Sign-in to **None** in Settings if
   your server refuses anonymous writes; the stream then also reports "can't receive").
3. Type "should fail" and press Ctrl+Enter.
   - Expect: status "Couldn't send: not allowed on this topic".
   - Log: `publish failed: HTTP 403` (or 401)
4. Undo the change from step 2.

### P7 Server unreachable
1. Turn Wi-Fi off (or unplug the network cable).
2. Copy "offline" and press **Ctrl+Alt+Shift+C**.
   - Expect: within 30 s, toast "Couldn't send: server unreachable".
   - Log: `publish failed: …` or `publish timed out`
3. Wait 2 minutes.
   - Expect: tray status "Reconnecting…".
4. Turn the network back on.
   - Expect: status "Connected" within about a minute. Log: `stream connected (1 topics)`

## 4. System

### S1 Start with Windows
1. Tray → check that **Start with Windows** is ticked.
2. Sign out of Windows and sign in again.
   - Expect: the NtfyBar icon is back without starting it by hand; Task Manager → Startup apps lists NtfyBar.
3. Tray → click **Start with Windows** (unticks it).
4. Sign out and in again.
   - Expect: NtfyBar does not start. (Start it by hand and tick it again if you want it.)

### S2 Send To on/off
1. Tray → **Settings…** → untick **Add to Explorer's Send To menu** → **Save**.
2. Right-click a file → **Send to**.
   - Expect: no NtfyBar entry.
3. Settings → tick it again → **Save**.
   - Expect: the entry is back.

### S3 Hotkey change and conflict
1. Settings → Send hotkey: type `Ctrl+Foo` → **Save**.
   - Expect: "“Ctrl+Foo” is not a valid hotkey (example: Ctrl+Alt+Shift+C)."; the window stays open.
2. Type `Win+E` → **Save**.
   - Expect: toast "NtfyBar hotkey not available" / "Win+E is already used by another app. Pick
     another one in Settings." (If Windows lets NtfyBar have Win+E, try `Win+R`.)
   - Log: `hotkey not registered`
3. Settings → type `Ctrl+Alt+Shift+V` → **Save**; copy text; press Ctrl+Alt+Shift+V.
   - Expect: the text is sent.
4. Set it back to `Ctrl+Alt+Shift+C` → **Save**.

### S4 Settings checks
1. Settings → Server URL `ntfy.example` (no https://) → **Save**.
   - Expect: "The server URL is not valid (example: https://ntfy.example)."
2. Fix the URL. In the empty last row of Topics, type Label `Test` and Topic `my topic` → **Save**.
   - Expect: "Topic “my topic” is not valid: use letters, digits, - and _ only (up to 64)."
3. Change the topic to `my-test-topic`, untick Notify in that row → **Save**.
4. Open Settings again.
   - Expect: the row is there with Notify unticked.
5. Click a cell in that row → **Remove selected topic** → **Save**.
6. Open Settings again.
   - Expect: the row is gone.

### S5 One instance only
1. With NtfyBar running, double-click `NtfyBar.exe` again.
   - Expect: still exactly one NtfyBar tray icon.

### S6 Toast click when NtfyBar is not running
1. Do R4 (receive a PDF) so a file toast is in the notification center.
2. Tray → **Quit NtfyBar**.
3. Win+N → on that toast click **Open folder**.
   - Expect: NtfyBar starts (tray icon back) and Explorer shows the PDF selected.
   - Log: `started by a toast click` then `toast activated: reveal`
4. Win+N → on an older **text** toast click **Copy**.
   - Expect: toast "That item is no longer in Recent".

### S7 Sleep and wake
1. Start → Power → **Sleep**. Wait 1 minute. Wake the PC and sign in.
   - Log: `resumed from sleep: reconnecting` then `stream connected (1 topics)`
2. Right away, share a text from the phone.
   - Expect: copied within a few seconds.

### S8 Display scaling
1. Windows Settings → System → Display → Scale: **150 %**.
2. Tray → **Settings…**
   - Expect: all labels readable, nothing cut off or overlapping, Save/Cancel visible (scroll if the
     screen is small); the topics grid shows its columns.
3. Tray → **Publish…**, resize the window larger and smaller.
   - Expect: Message and Files grow and shrink; buttons stay visible.
4. Set the scale back.

### S9 Uninstall
1. With NtfyBar running: Win+R → `%LOCALAPPDATA%\Programs\NtfyBar\NtfyBar.exe --uninstall`.
   - Expect: "Quit NtfyBar from its tray menu first, then run this again."
2. Tray → **Quit NtfyBar**.
3. Run the same `--uninstall` command again.
   - Expect: "Removed Start with Windows, the Send To entry and the notification registration. …"
4. Right-click a file → **Send to**.
   - Expect: no NtfyBar entry. Task Manager → Startup apps: no NtfyBar.
5. Start `NtfyBar.exe` again to keep using it (Settings are still there; tick Start with Windows and Send To again).

## 5. Report back

For each failed test send: the test id (e.g. R3 step 2), what happened instead, and
`%LOCALAPPDATA%\NtfyBar\logs\ntfybar.log` (it holds no message text, topic names or passwords).
