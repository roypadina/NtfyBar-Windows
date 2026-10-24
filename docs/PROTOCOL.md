# Shared clipboard wire protocol

How every sender (the NtfyBar apps for Windows and macOS, clipboard managers, phone shortcuts) puts
clipboard content on an [ntfy](https://ntfy.sh) topic, and how every receiver reads it. One protocol,
so each receiver has one code path. Examples use `https://ntfy.example` and `example-topic`.

## 1. Sending

| Payload | Request | Fields | Notes |
|---|---|---|---|
| Text ≤ 4000 bytes (UTF-8) | `POST <server>/` with a JSON body | `{"topic","message","title","tags","priority","actions"}` | `actions` = `[{"action":"copy","label":"Copy","value":<text>}]` only when the text is ≤ 2000 bytes (gives the phone a one-tap Copy button). JSON keeps any Unicode intact. |
| Text > 4000 bytes | `PUT <server>/<topic>`, body = the UTF-8 bytes | query `filename=clip.txt`, `title`, `tags`, `message=<first 80 characters>…` | The explicit `filename` makes the server store it as an attachment, byte-exact (the plain-text path trims whitespace). |
| File | `PUT <server>/<topic>`, body = the file | query `filename=<name>`, `title`, `tags`, `message=<name>` | One message per file (ntfy: one attachment per message). |
| Image from the clipboard | as File, `filename=clip.png` | | Receivers paste it as a picture. |

- `title`, `tags`, `filename` and `message` go in the **query string**, URL-encoded UTF-8 (HTTP
  headers are ASCII-only in many HTTP stacks). Priority goes in the `X-Priority` header (1–5,
  default 3).
- Auth: `Authorization: Basic base64(user:pass)` or `Authorization: Bearer <token>`. Never follow a
  redirect with credentials.
- `tags` = `<sender-tag>[,extra tags]`. The **first tag is the sender id**, e.g. `ntfybar-win`,
  `ntfybar-mac`. A sender that cannot tag (the ntfy phone app's Share) sends no tags; receivers
  never need a tag to handle a message. Do not use emoji shortcode names as sender tags (`phone`
  would render as ☎).
- `title` defaults to the device name. For files, `message` = the file name, so the notification
  reads "Laptop: photo.jpg" instead of the server's generic text.
- Limits, checked before anything is uploaded: at most 20 files per send, at most 100 MB per file.
- Text is sent with LF line endings (senders convert CRLF first), so the byte limits count LF.
- Nothing else goes in the body: the clipboard body is the raw payload, no footer.

Errors a sender should name: 401/403 "not allowed on this topic", 413 "too large", 429 "rate
limited", no connection "server unreachable".

## 2. Receiving

Subscribe with one stream per server: `GET <server>/<t1,t2,…>/json?since=<last message time>`.
Expect a `keepalive` about every 45 s; treat 90 s of silence as a dead connection and reconnect
with exponential backoff (1 s doubling, capped at 60 s). On 401/403 or a redirect, stop until the
user changes the settings. `since` returns messages with time ≥ the given second, so drop ids
already seen at that second.

Classify each message:

| Kind | Rule | Receiver action |
|---|---|---|
| Text | no `attachment` | copy `message` as text |
| Text attachment | **treat-as-text rule**: `attachment.type` starts with `text/plain`, `attachment.size` ≤ 1 MB, `attachment.name` is exactly `clip.txt` or `attachment.txt` | download, decode UTF-8, handle as text |
| Image | `attachment.type` starts with `image/` | download, put on the clipboard as a picture |
| File | any other attachment (including other `.txt` files) | download to the downloads folder, offer to copy/open |
| Expired | `attachment.expires` < now | nothing to download: say "attachment expired" (a download that returns 404 means the same) |

`attachment.txt` is what the server names a long text it received without a file name (for
example from the phone's Share sheet); `clip.txt` is what senders of this protocol use.

Downloads: only `http`/`https` URLs; send the server's credentials only when the attachment URL has
the same scheme, host and port as the server; sanitize the file name for the local file system;
never overwrite (`name (2).ext`); skip anything over 100 MB.

## 3. Live, missed and own messages

- **Catch-up**: a message whose `time` is earlier than the stream's `open` event was cached by the
  server while the receiver was away. Both times come from the server clock.
- **Freshness window**: a catch-up message at most 120 s old when it arrives is treated as live
  ("copied on the phone, then opened the laptop").
- Older catch-up messages are **missed**: list them and show one summary ("3 messages while away");
  never auto-copy them, unless the user enabled "process missed messages" for that topic.
- **Own echo**: a receiver also gets what it sent. It is recognised by either
  1. the message id returned by the publish request (remember ids for 2 minutes), or
  2. the first tag being the receiver's own sender tag (covers a restart between publish and echo).

  An own echo is kept in history as read and triggers nothing.
- **No loops by construction**: senders publish only on an explicit user action (button, hotkey,
  share, menu), never because the clipboard changed.

## 4. Security notes

- The topic name is a capability: keep it out of logs, screenshots and public files.
- Attachment URLs (`/file/<id>`) may be downloadable without credentials on some server versions;
  they are random and expire (3 h by default), but treat them as secrets and do not send secrets as
  files.
