# CaptionRelay WebSocket protocol (version 1)

The Windows app listens on `ws://127.0.0.1:{port}/relay` (default port 47321, loopback only). The browser
extension's service worker connects. All messages are JSON text frames with a `type` field. Unknown types and
unknown fields must be ignored (forward compatibility); new features only add new optional types or fields.

The app and the protocol are **provider-neutral**: they move text and acks. Which chat receives the text
(Gemini, ChatGPT, Claude, an API, ...) is decided inside the extension.

## Connection

- The `Origin` header must be `chrome-extension://<id>` (Chrome and Edge). Web pages (`http`/`https` origins) are
  rejected with HTTP 403. `allowedExtensionIds` in config.json can restrict the ids.
- The first message must be `hello` within 5 s, with the token from config.json. Otherwise the app sends `error`
  (for a wrong token) and closes with status 1008.
- If several extensions are connected, the most recently connected one receives sends; the others stay connected
  as fallbacks.

## Messages

| Direction | Message | Notes |
|---|---|---|
| ext → app | `{"type":"hello","token":"…","version":"0.1.0","protocol":1,"client":"CaptionRelay extension 0.1.0","provider":"gemini"}` | first message; `provider` = the chat it delivers to (label in the indicator) |
| app → ext | `{"type":"welcome","app":"CaptionRelay","version":"0.1.0","protocol":1,"sendKey":"Ctrl+Shift","skipKey":"F9"}` | handshake accepted; `sendKey` / `skipKey` (optional) are the app's hotkeys as shown to the user (`"None"` = off), for the extension popup |
| app → ext | `{"type":"error","reason":"bad token"}` | then the socket is closed |
| app → ext | `{"type":"ping"}` | every 20 s; keeps the MV3 service worker alive |
| ext → app | `{"type":"pong"}` | reply to ping |
| app → ext | `{"type":"send","id":"<guid>","text":"…"}` | text to deliver (prefix already applied) |
| ext → app | `{"type":"ack","id":"<guid>","status":"sent","provider":"gemini"}` | submitted and verified |
| ext → app | `{"type":"ack","id":"<guid>","status":"queued","provider":"gemini"}` | chat busy; will submit automatically |
| ext → app | `{"type":"ack","id":"<guid>","status":"error","detail":"input not found","provider":"gemini"}` | failed |

| ext → app | `{"type":"result","id":"<guid>","status":"sent","provider":"gemini"}` | final result of a send acked `queued` (submitted after the answer ended); `status` `error` + `detail` if it was given up |
| ext → app | `{"type":"log","level":"info","text":"…"}` | diagnostics for the app log (page structure, timings — never chat text) |
| ext → app | `{"type":"quit"}` | the user turned CaptionRelay off in the extension popup: the app exits (and closes Live Captions); only after a successful `hello` |

## Native messaging host (`com.captionrelay.host`)

Not part of the WebSocket protocol: the app's exe also answers Chrome native messaging (registered in HKCU by the
app on every start; only the CaptionRelay extension id may call it). One request per call, 4-byte length + JSON:

| Request | Reply |
|---|---|
| `{"type":"getConfig"}` (or no type) | `{"type":"config","port":47321,"token":"…"}` |
| `{"type":"launch"}` | `{"type":"launched","status":"started" \| "started via explorer" \| "already running"}` — starts the app (popup switch on) |
| any, extension not in `allowedExtensionIds` / failure | `{"type":"error","reason":"…"}` |

Either side may also send `ping` and get `pong`.

## Timing

- If no `ack` arrives within 3 s the app shows "no response" and keeps the text for the next send (an ack that
  arrives later is logged and ignored).
- After `ack` `queued` the app waits for `result` (up to 200 s; the extension gives up after 3 min of answering).
  `sent` confirms the text; `error` or no result puts it back as pending. Queued texts are merged into one message.
- A connection that sends nothing for 65 s (no pong) is closed.
