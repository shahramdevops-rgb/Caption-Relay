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
| ext → app | `{"type":"hello","token":"…","version":"0.1.0","protocol":1,"client":"CaptionRelay extension 0.1.0"}` | first message |
| app → ext | `{"type":"welcome","app":"CaptionRelay","version":"0.1.0","protocol":1}` | handshake accepted |
| app → ext | `{"type":"error","reason":"bad token"}` | then the socket is closed |
| app → ext | `{"type":"ping"}` | every 20 s; keeps the MV3 service worker alive |
| ext → app | `{"type":"pong"}` | reply to ping |
| app → ext | `{"type":"send","id":"<guid>","text":"…"}` | text to deliver (prefix already applied) |
| ext → app | `{"type":"ack","id":"<guid>","status":"sent","provider":"gemini"}` | submitted and verified |
| ext → app | `{"type":"ack","id":"<guid>","status":"queued","provider":"gemini"}` | chat busy; will submit automatically |
| ext → app | `{"type":"ack","id":"<guid>","status":"error","detail":"input not found","provider":"gemini"}` | failed |

Either side may also send `ping` and get `pong`.

## Timing

- If no `ack` arrives within 3 s the app shows "no response" and keeps the text for the next send (an ack that
  arrives later is logged and ignored).
- A connection that sends nothing for 65 s (no pong) is closed.
