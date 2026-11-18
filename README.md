# CaptionRelay

One global hotkey sends everything Windows Live Captions heard since your last send into your open Gemini, ChatGPT or Claude web
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
| `extension/` | Chrome/Edge extension (Manifest V3, plain JavaScript). Receives text and submits it in Gemini, ChatGPT or Claude (chosen on the options page). |
| `tests/` | xUnit tests (C#), Node tests and a browser test page for the extension, real Live Captions recordings. |
| `tools/` | Test helpers: caption recorder, TTS speaker, fake extension client, key/UI test scripts. |
| `docs/protocol.md` | The app ↔ extension WebSocket protocol. |
| `docs/CaptionRelay-course.pdf` | Illustrated design course (Persian, with English interview summaries); sources in `docs/course/`. |
| `ROADMAP.md` | Phases, what was built and what was measured. |
| `CONTRIBUTING.md` | How to build, test and send changes. |

## Requirements

- Windows 11 with **Live Captions** (Win+Ctrl+L).
- .NET 8 Desktop Runtime (to run) / .NET SDK 8+ (to build).
- Chrome 120+ or Edge, signed in to Gemini, ChatGPT or Claude (pick the chat under **Provider** on the extension options page).

## Quick start: one exe

`CaptionRelay.exe` is a single self-contained file. It includes the app, the .NET runtime and the browser extension,
so the PC needs nothing installed besides Windows 11 and Chrome (or Edge).

1. Run `CaptionRelay.exe` (from anywhere: Desktop, Downloads …). Only one instance runs at a time.
2. On first start it unpacks the extension to `%LOCALAPPDATA%\CaptionRelay\extension`, registers itself so the
   extension can get the token automatically, starts Live Captions and opens **Connect your browser**.
3. In that window: **Open Chrome extensions** → turn on **Developer mode** → **Load unpacked** → in the folder window
   press **Ctrl+V** in the *Folder* box (the path is already copied) → **Select Folder**.
4. The window turns green: **Connected**. Nothing to paste: port and token reach the extension by themselves.

Chrome does not let programs install extensions that are not on the Chrome Web Store, so step 3 is the one manual
step, done once. The setup window can be opened again from the tray menu (**Set up browser extension…**).

## Build

```
dotnet build CaptionRelay.sln
dotnet test CaptionRelay.sln
powershell -ExecutionPolicy Bypass -File build-exe.ps1     # → build\CaptionRelay.exe (single self-contained exe)
```

The exe always lands at `build\CaptionRelay.exe` and every build replaces it (a running copy there is stopped,
replaced and started again).

`dotnet publish app/CaptionRelay.csproj -c Release -o publish` still makes a small framework-dependent build (needs the
.NET 8 Desktop and ASP.NET Core runtimes).

## Install the extension by hand (developers)

1. Open `chrome://extensions` (or `edge://extensions`) and turn on **Developer mode**.
2. **Load unpacked** → choose the `extension` folder. The extension id is always `hpmjjnjmlhbolpmeafdkmdldlfmoaaee`.
3. While the app runs, the extension gets port and token from it (native messaging host `com.captionrelay.host`,
   registered in HKCU by the app on every start). Without that: right-click the indicator → **Copy token (for the
   extension)**, click the extension's icon, paste the token → **Save**.
4. The status turns green and the badge shows **ON**.
5. Optional: the **Test** button inserts "CaptionRelay test" into your chat box without sending it and shows which
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

## Extension popup

Click the CaptionRelay icon in the browser toolbar (pin it via the puzzle piece) to open its popup:

- **On/off switch**: shows ON while the CaptionRelay app runs (badge green `ON`). **Off closes the app and Live
  Captions**; **on starts the app** (which starts Live Captions). Starting or closing the app any other way (desktop,
  taskbar, tray) moves the switch too — opening the popup checks at once, otherwise within 30 s.
- **Status**: on / off / starting / closing, with the reason when something failed.
- **Deliver to**: Gemini, ChatGPT or Claude.
- **Shortcuts**: the app's send and skip keys (as set in config.json) and what each one does.
- **All settings** opens the full options page (port, token, Test).

## Taskbar window, indicator and tray menu

- **Taskbar**: CaptionRelay has a taskbar button like any app. It starts minimized and never takes your focus; click
  it to see the status and the shortcuts. **Closing it (the X, or right-click → Close window) exits CaptionRelay
  completely and closes Live Captions**; the extension switch turns off. Menu **Exit** does the same.
- Dots: Live Captions connected / extension connected (the label is the chat the extension delivers to).
- `N words pending` — what the next send would contain; `· queued` — a send waits for Gemini to finish.
- Click the indicator to see the end of the pending text; drag to move it (the position is saved).
- Menu (right-click the indicator or the tray icon): show/hide indicator, hide Live Captions window, start
  with Windows, set up browser extension, open sessions folder, copy token, open config, reconnect Live Captions,
  exit. The app makes no sounds.

## config.json

Lives in `%LOCALAPPDATA%\CaptionRelay\` (or next to the exe if a `config.json` is already there: portable mode); edit
it while the app is closed (or use the menu for the common options).

| Key | Default | Meaning |
|---|---|---|
| `port` | 47321 | Loopback WebSocket port (must match the extension). |
| `token` | random | Shared secret the extension must send. |
| `sendKey` / `skipKey` | `CtrlShift` / `F9` | `CtrlShift`, any key name (e.g. `F8`, `Pause`) or `None`. Keys are never swallowed (F9 still reaches the active app). |
| `prefix` | `""` | Text put before every message. |
| `minSendChars` | 3 | Shorter pending text is not sent ("Nothing new"). |
| `pollMs` | 120 | How often Live Captions is read. |
| `startLiveCaptions` | true | Start Live Captions (and restart it if closed). |
| `closeLiveCaptionsOnExit` | true | Close Live Captions when CaptionRelay exits. |
| `hideLiveCaptions` | false | Keep the Live Captions window off-screen. |
| `startWithWindows` | false | Start with Windows (HKCU Run entry). |
| `indicatorX/Y`, `indicatorVisible` | — | Indicator position and visibility. |
| `sessionRetentionDays` | 30 | Delete `sessions/*.txt` older than this (0 = keep). |
| `allowedExtensionIds` | `[]` | Restrict which extension ids may connect (empty = any extension with the token; web pages are always refused). |
| `extensionSetUp` | false | Set after the first extension connection; while false the setup window opens at start. |

## Files

All in `%LOCALAPPDATA%\CaptionRelay\` (or next to the exe in portable mode):

- `extension/` — the browser extension unpacked from the exe (Chrome loads it from here; refreshed on every start —
  after an update click the reload icon on the extension in `chrome://extensions`).
- `native-host.json` — native messaging host manifest (points Chrome at the exe for the automatic token).
- `sessions/yyyy-MM-dd_HHmm.txt` — the full conversation with `[SENT hh:mm:ss]` / `[SKIPPED hh:mm:ss]` markers.
- `logs/captionrelay-yyyyMMdd.log` — diagnostics (never the conversation text; the token only as a fingerprint).
  The extension also reports page diagnostics here (which buttons it found, whether the tab was hidden …).

## Troubleshooting

| Indicator / message | What to do |
|---|---|
| `Gemini not connected` | Is the extension enabled, is the token pasted, is the port the same? Click **Reconnect** on the extension options page. |
| `Live Captions not available` | Start Live Captions (Win+Ctrl+L) or use **Reconnect Live Captions**. |
| `Error: input not found` / `send button not found` | The site changed its page. Selectors live in `extension/providers/<gemini|chatgpt|claude>/selectors.js`; the log shows the buttons the extension saw. Reload the extension after fixing. |
| `Error: not logged in` | Sign in to Gemini in that browser. |
| `No response` | The extension did not answer within 3 s; the text is kept for the next send. |
| `Port 47321 in use` | Another program uses the port: change `port` in config.json and in the extension options. |

Failed sends never lose text: it stays pending and goes with the next send.
