# CaptionRelay

One global hotkey sends everything Windows Live Captions heard since your last send into your open Gemini web
chat and submits it — without touching the mouse, without typing, and without moving your focus away from the
window you are working in.

- **Ctrl+Shift** (press and release, nothing else) — send all new speech to Gemini.
- **F9** — skip: forget the speech so far without sending it.
- Nothing is ever lost: the app keeps the whole conversation even when Live Captions scrolls old lines away.
- Works while Chrome is in the background, behind other tabs or minimized (~60–80 ms from the hotkey to the
  message being submitted).
- A small indicator shows `● Captions ● Gemini · 42 words pending` and flashes `Sent ✓`, `Queued` or errors.

```
 speech ──► Live Captions ──(UI Automation)──► CaptionRelay app ──(ws://127.0.0.1)──► Chrome extension ──► Gemini tab
                                               transcript, hotkeys,                   service worker +
                                               indicator, sessions/                   content script
```

## Parts

| Folder | What |
|---|---|
| `app/` | Windows tray app (C# .NET 8, WinForms). Reads Live Captions, keeps the transcript, global hotkeys, indicator, loopback WebSocket server. |
| `extension/` | Chrome/Edge extension (Manifest V3, plain JavaScript). Receives text and submits it in Gemini. |
| `tests/` | xUnit tests (C#), Node tests and a browser test page for the extension, real Live Captions recordings. |
| `tools/` | Test helpers: caption recorder, TTS speaker, fake extension client, key/UI test scripts. |
| `docs/protocol.md` | The app ↔ extension WebSocket protocol. |
| `docs/CaptionRelay-course.pdf` | Illustrated design course (Persian, with English interview summaries); sources in `docs/course/`. |
| `ROADMAP.md` | Phases, what was built and what was measured. |

## Requirements

- Windows 11 with **Live Captions** (Win+Ctrl+L).
- .NET 8 Desktop Runtime (to run) / .NET SDK 8+ (to build).
- Chrome 120+ or Edge, signed in to Gemini.

## Build and run

```
dotnet build CaptionRelay.sln
dotnet test CaptionRelay.sln
dotnet publish app/CaptionRelay.csproj -c Release -o publish
```

Run `publish\CaptionRelay.exe` (or the desktop shortcut). Only one instance runs at a time. On first start it creates
`config.json` next to the exe with a random token, and starts Live Captions if it is not running.

## Install the extension (once)

1. Open `chrome://extensions` (or `edge://extensions`) and turn on **Developer mode**.
2. **Load unpacked** → choose the `extension` folder. The extension id is always `hpmjjnjmlhbolpmeafdkmdldlfmoaaee`.
3. Right-click the indicator (or the tray icon) → **Copy token (for the extension)**.
4. Click the extension's icon → paste the token → **Save**. The status turns green and the badge shows **ON**.
5. Optional: the **Test** button inserts "CaptionRelay test" into your Gemini box without sending it and shows which
   page elements were found.

Use the extension in one browser profile only: if two browsers are connected, the most recently connected one
receives the text.

## Recommended Live Captions settings

- **Turn OFF "Include microphone audio"** (Live Captions → ⚙ → Preferences), so only the other person (system audio)
  is captioned, not you.
- Make the Live Captions window taller, or use the app's **Hide Live Captions window** option (moves it off-screen;
  captions keep working; it is put back on exit).
- Pick the language of the conversation in Live Captions' language settings.

## Everyday use

1. Open a Gemini chat and, if you like, give it the context first (for example "I am in a job interview, help me
   answer"). A Gemini **Gem** saves such instructions so you only open the Gem before the conversation.
2. Keep that chat as the last Gemini tab you used — the text goes to the most recently active Gemini tab
   (if none is open, the extension opens a new chat in the background).
3. Talk / listen. Press **Ctrl+Shift** whenever you want Gemini to see what was said since the last send.
4. If Gemini is still answering, the text is **queued** and submitted automatically right after the answer ends;
   several queued sends are merged into one message. Your own half-typed draft in the box is kept (sent first).

Text on the Live Captions screen when the app starts belongs to an earlier conversation and is ignored.

## Indicator and tray menu

- Dots: Live Captions connected / extension connected (the label is the chat the extension delivers to).
- `N words pending` — what the next send would contain; `· queued` — a send waits for Gemini to finish.
- Click the indicator to see the end of the pending text; drag to move it (the position is saved).
- Menu (right-click the indicator or the tray icon): show/hide indicator, sound, hide Live Captions window, start
  with Windows, open sessions folder, copy token, open config, reconnect Live Captions, exit.

## config.json

Created next to the exe; edit it while the app is closed (or use the menu for the common options).

| Key | Default | Meaning |
|---|---|---|
| `port` | 47321 | Loopback WebSocket port (must match the extension). |
| `token` | random | Shared secret the extension must send. |
| `sendKey` / `skipKey` | `CtrlShift` / `F9` | `CtrlShift`, any key name (e.g. `F8`, `Pause`) or `None`. Keys are never swallowed (F9 still reaches the active app). |
| `prefix` | `""` | Text put before every message. |
| `minSendChars` | 3 | Shorter pending text is not sent ("Nothing new"). |
| `pollMs` | 120 | How often Live Captions is read. |
| `sound` | true | Short tones for sent / queued / error. |
| `startLiveCaptions` | true | Start Live Captions (and restart it if closed). |
| `hideLiveCaptions` | false | Keep the Live Captions window off-screen. |
| `startWithWindows` | false | Start with Windows (HKCU Run entry). |
| `indicatorX/Y`, `indicatorVisible` | — | Indicator position and visibility. |
| `sessionRetentionDays` | 30 | Delete `sessions/*.txt` older than this (0 = keep). |
| `allowedExtensionIds` | `[]` | Restrict which extension ids may connect (empty = any extension with the token; web pages are always refused). |

## Files

- `sessions/yyyy-MM-dd_HHmm.txt` — the full conversation with `[SENT hh:mm:ss]` / `[SKIPPED hh:mm:ss]` markers.
- `logs/captionrelay-yyyyMMdd.log` — diagnostics (never the conversation text; the token only as a fingerprint).
  The extension also reports page diagnostics here (which buttons it found, whether the tab was hidden …).

## Troubleshooting

| Indicator / message | What to do |
|---|---|
| `Gemini not connected` | Is the extension enabled, is the token pasted, is the port the same? Click **Reconnect** on the extension options page. |
| `Live Captions not available` | Start Live Captions (Win+Ctrl+L) or use **Reconnect Live Captions**. |
| `Error: input not found` / `send button not found` | Google changed the page. Selectors live in `extension/providers/gemini/selectors.js`; the log shows the buttons the extension saw. Reload the extension after fixing. |
| `Error: not logged in` | Sign in to Gemini in that browser. |
| `No response` | The extension did not answer within 3 s; the text is kept for the next send. |
| `Port 47321 in use` | Another program uses the port: change `port` in config.json and in the extension options. |

Failed sends never lose text: it stays pending and goes with the next send.
