# HTTPS API v1

Windows host: HTTPS on TCP 45831 by default. Redirects are disabled. There is no plaintext HTTP listener. A Windows client with no saved fingerprint first authenticates the host through the OPAQUE password-authenticated key exchange, verifies a server proof over the presented TLS certificate fingerprint, then pins that fingerprint before normal HTTPS login. The temporary unpinned TLS client is limited to the two OPAQUE routes. Android clients continue to require an explicitly supplied or previously saved fingerprint.

## Authentication and lifetime

`POST /v1/login` with JSON `{"password":"..."}` returns `{"token":"...","protocol":1,"maxFileBytes":1073741824}`. No host interaction is required. Only one session may be active. Control endpoints require `Authorization: Bearer <token>`; the two `/v1/pake/*` bootstrap routes are public but require a valid OPAQUE exchange.

Windows key bootstrap uses `POST /v1/pake/start` with an OPAQUE login request and a 32-byte random nonce, then `POST /v1/pake/finish` with the one-use exchange id, OPAQUE finish request, and the same nonce. On successful OPAQUE authentication, the host returns its SHA-256 certificate fingerprint and a domain-separated HMAC proof under the PAKE session key. Exchanges expire after 30 seconds, are one-use, and are globally rate-limited. These endpoints authenticate and pin the host key only; the client then reconnects through normal fingerprint-pinned TLS and uses `/v1/login` to create a control session. This bootstrap is Windows-only in this release. Existing host settings need one-time re-enrollment by entering the current password locally.

Login attempts are serialized and limited globally to one attempt per two seconds. Missing/incorrect authentication is HTTP 401; throttling is 429; an occupied host is 409. Control bodies are limited to 32KiB and five seconds of reading. Passwords are stored using PBKDF2-HMAC-SHA256, 600,000 iterations, a random 32-byte salt and a 32-byte hash.

Authenticated requests renew the session. Fifteen seconds without one revokes the token and releases recorded pressed inputs. Host stop/logout also revokes the session. File uploads are cancelled on revocation. Password changes require stopping the host. Clients must not log tokens or passwords. While the Android activity is stopped it does not download frames; it calls GET /v1/ping about every five seconds. If that token is gone when the activity is visible again, the client logs in again with the password held only in process memory. Disconnect or process teardown clears that password, and it is never written to disk.

## Endpoints

| Method and path | Request | Response |
|---|---|---|
| POST `/v1/login` | Password JSON | Session JSON |
| POST `/v1/pake/start` | OPAQUE login request + nonce (Windows clients) | One-use challenge |
| POST `/v1/pake/finish` | Challenge id + OPAQUE finish request + nonce | TLS fingerprint + PAKE proof |
| POST `/v1/logout` | Empty/JSON | 200 |
| GET `/v1/ping` | — | Protocol version |
| GET `/v1/displays` | — | `[{id,name,left,top,width,height,primary}]` |
| GET `/v1/frame?display={id}&quality={25..90}&view=x,y,w,h&fps={5|10|15|30}&cursor=local` | Optional display ID, JPEG quality, normalized viewport, client frame target, and local cursor drawing | JPEG for the selected viewport. Omitted display selects primary, quality defaults to 55, viewport defaults to the full display, and fps defaults to 15. `cursor=local` asks the host not to burn the cursor into the JPEG. |
| POST `/v1/input` | Input JSON | 200 |
| GET `/v1/clipboard` | — | `{id,origin,files:[{name,size}],text}` |
| PUT `/v1/clipboard/text` | `{origin,expectedRevision,text}` | New clipboard manifest |
| POST `/v1/clipboard/uploads` | `{origin,expectedRevision,files:[{name,size}]}` | `{id}` |
| PUT `/v1/clipboard/uploads/{id}/{index}` | Raw bytes and required `X-Content-SHA256` | 200 after size/hash verification |
| POST `/v1/clipboard/uploads/{id}/commit` | Empty | New clipboard manifest |
| DELETE `/v1/clipboard/uploads/{id}` | — | Cancel staged batch |
| GET `/v1/clipboard/{id}/{index}` | — | Pinned snapshot file bytes and `X-Content-SHA256` |

There is no shared-folder filesystem API. File transfer uses only the clipboard routes below. Outbound sources are limited to files selected in the host OS file-drop clipboard, referenced only by session-scoped opaque snapshot ID and integer index. Remote source paths are never accepted or returned. Uploaded files are staged under a private cache and placed into the host file-drop clipboard only after the entire batch verifies.

Clipboard batches permit 1–100 ordinary files, at most 1GiB each and 4GiB total. Duplicate basenames get separate staging directories. Folders/links are unsupported. Snapshot changes or incomplete commits return 409. An expected clipboard revision prevents an older upload from replacing a newer copy. On the client, the local clipboard sequence is checked before publishing downloaded files. Origin markers suppress transfer loops. Clipboard publication always requests copy semantics; source files are never deleted.

Plain Unicode text uses the same session-scoped clipboard manifest and revision check. It is limited to 1,000,000 UTF-16 characters. Publishing text replaces the file-drop payload; publishing a file batch replaces text. Origin markers suppress echoes, and local sequence checks preserve a newer local text copy if it changes during a remote update. Empty clips, HTML/RTF, images, and other clipboard formats are intentionally omitted in this version.

Unfinished batches are cancelled on session revocation. Completed cache files remain available for subsequent OS paste. Automatic cache expiry is not yet implemented. Authentication is required on every clipboard route. Android uses read-only content URIs with per-recipient grants; Windows uses CF_HDROP on a dedicated STA dispatcher. [Windows clipboard documentation](https://learn.microsoft.com/en-us/windows/win32/dataxchg/using-the-clipboard), [Android copy/paste documentation](https://developer.android.com/develop/ui/views/touch-and-input/copy-paste).

## Input messages

```json
{"kind":"move","display":"all","x":0.5,"y":0.5}
{"kind":"button","code":0,"down":true}
{"kind":"button","code":0,"down":false}
{"kind":"wheel","code":120}
{"kind":"key","code":17,"down":true}
{"kind":"key","code":17,"down":false}
{"kind":"text","text":"안녕하세요"}
{"kind":"release"}
```

Coordinates are normalized to the selected display, excluding client letterboxing. `all` selects the virtual desktop; other IDs are returned by the displays endpoint. Input uses the ID of the rendered frame to prevent monitor-switch races. Server mapping accounts for negative monitor origins. Unknown/removed IDs return 400 rather than silently controlling another display.

Mouse buttons are left=0, right=1, middle=2. Key codes are Windows virtual-key values, including VK_HANGUL (21); Windows clients forward native keys before local IME processing so composition runs on the server. Android uses a native InputConnection: composing text stays local and committed strings use Unicode injection. Text is limited to 4,096 UTF-16 units. Input requests must be ordered on the client. On focus loss clients send `release`.

The display additions are backward-compatible within v1: old clients omit display and continue using the primary monitor. New clients require a server with the displays endpoint; update both applications together.

## Initial implementation limits

Frames are polled sequentially and input has a separate request stream. Clients may select JPEG quality 25–90 and a local request rate up to 30 FPS. An installed host with stutter reduction enabled may temporarily encode below the requested quality when capture and JPEG compression miss the requested frame interval, then step back toward it. Response headers `X-Capture-Ms`, `X-Encode-Ms`, `X-Frame-Reused`, `X-Jpeg-Quality`, `X-Cursor-X`, `X-Cursor-Y`, and `X-Cursor-Local` describe that frame. Older clients ignore them. Mobile clients can request a normalized viewport (`x,y,width,height` in 0–1 coordinates), so zoomed views transfer only the visible part of the monitor. Capture, JPEG encoding, decoding, and network time can reduce the actual rate. There is no video codec, adaptive bandwidth allocation, resume protocol or Internet rendezvous/relay. Use on LAN/VPN. Server-side receipt hashes and TLS protect file transport; this is not a general remote filesystem API.

The HTTPS endpoint configuration uses ASP.NET Core Kestrel. [Microsoft endpoint documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0)
