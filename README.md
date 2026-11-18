# CaptionRelay

One global hotkey pastes everything Windows Live Captions heard since last time into the input box of your open
Gemini, ChatGPT or Claude web chat — without touching the mouse, without typing, and without moving your focus away
from the window you are working in. You read it and press Enter yourself (or let the extension press it: **Press
Enter for me** in its popup).

- **Numpad +** — tap: paste only the latest 15 words; tap again quickly for 15 more each time (2 taps = 30, 3 = 45
  …). It goes in 0.4 s after your last tap; a see-through preview above the indicator shows what will be pasted.
  **Hold it** (0.4 s or longer) and it is pasted **and sent** when you let go.
- **Numpad −** — paste everything heard since the last F9 (or since the app started) into the chat's input (not
  sent).
- **F9** — skip: start fresh. Everything heard so far is left behind; the numpad keys only use what is said after it.
- Only the numpad's + and − are used, not the +/= and −/_ keys of the main keyboard.
- Pasting uses nothing up: press again and the same words are pasted again (plus anything new). The text is merged
  with what the chat's input box already holds, so nothing appears twice: newer words go after it, older words (from
  more taps) go in front of it.
- Single keys, no Ctrl/Alt. CaptionRelay keeps them for itself: they do not reach the app you are in.
- Nothing is ever lost: the app keeps the whole conversation even when Live Captions scrolls old lines away.
- Works while Chrome is in the background, behind other tabs or minimized (~60–80 ms from the hotkey to the
  message being submitted).
- A small indicator shows `● Captions ● Gemini` and briefly `Pasted ✓`, `Tap ×2 · 30 words`, `Already in the
  input` or errors.

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
3. Talk / listen. Press **numpad −** to paste everything said since your last **F9** at the end of the chat's input
   box, one space after anything already there (no new line). Check it and press **Enter** in the chat to send it.
   Only want the latest part (the question just asked)? Tap **numpad +**: once = the last 15 words, quickly again =
   15 more per tap. While you tap, a see-through preview above the indicator highlights exactly what will go in
   (older words dimmed before it); it is pasted 0.4 s after your last tap. **Hold numpad +** on the last press
   (0.4 s or longer; the preview and indicator then say "release: paste + send") and the selection is pasted and
   sent as soon as you let go (if the chat is still answering: pasted now, sent when the answer ends). Numpad − or F9
   while tapping cancels the taps.
   Nothing is used up by pasting: pressing again pastes the same words again, merged with what the input box still
   holds. Words said since then are added after it; tapping more times adds the older words in front of it (1 tap,
   then 3 taps → the box holds the last 45 words, in order, nothing twice). If the box already has all of it,
   nothing changes and the indicator says `Already in the input`. If you sent the message and the box is empty,
   everything goes in again. Press **F9** to start fresh (for example when a new question
   begins).
4. With **Press Enter for me** on, the text is submitted right away instead, and submitted text is used up (the
   next numpad − starts after it). If Gemini is still answering, it is
   **queued** and submitted automatically right after the answer ends; several queued sends are merged into one
   message. Your own half-typed draft in the box is kept (sent first).

Text on the Live Captions screen when the app starts belongs to an earlier conversation and is ignored.

## Extension popup

Click the CaptionRelay icon in the browser toolbar (pin it via the puzzle piece) to open its popup:

- **On/off switch**: shows ON while the CaptionRelay app runs (badge green `ON`). **Off closes the app and Live
  Captions**; **on starts the app** (which starts Live Captions). Starting or closing the app any other way (desktop,
  taskbar, tray) moves the switch too — opening the popup checks at once, otherwise within 30 s.
- **Status**: on / off / starting / closing, with the reason when something failed.
- **Deliver to**: Gemini, ChatGPT or Claude.
- **Press Enter for me**: off (default) = the text is only pasted into the chat's input and you send it; on = it is
  pasted and submitted (queued while the chat is answering).
- **Shortcuts**: the app's paste, tap and skip keys (as set in config.json) and what each one does.
- **Show tap preview**: turns the see-through tap preview on or off (also in the taskbar window and the tray menu;
  all three stay in step).
- **All settings** opens the full options page (port, token, Test).

## Taskbar window, indicator and tray menu

- **Taskbar**: CaptionRelay has a taskbar button like any app. It starts minimized and never takes your focus; click
  it to see the status and the shortcuts. **Closing it (the X, or right-click → Close window) exits CaptionRelay
  completely and closes Live Captions**; the extension switch turns off. Menu **Exit** does the same.
- Dots: Live Captions connected / extension connected (the label is the chat the extension delivers to).
- `· queued` — a submitted send waits for Gemini to finish (Press Enter for me only).
- The indicator starts at the bottom of the screen, 300 px from the left edge (just under a Chrome window's chat box
  on the left half). It is only as wide as its text. Click it to see the end of what was heard since the last skip;
  drag to move it (the position is saved).
- **Tap preview**: while you tap numpad +, a see-through panel above the indicator shows the same words the Live
  Captions window shows right now (it reads the window's visible lines, so it follows the window's size), with the
  words the taps will paste highlighted and the rest dimmed. If the taps reach further back than the window shows,
  those words are highlighted too and the header says how many are above the window. It cannot be clicked or focused: clicks go through it. Turn it on or off
  with **Show tap preview** in the extension popup, the checkbox in the taskbar window, or the tray menu.
- Menu (right-click the indicator or the tray icon): show/hide indicator, hide Live Captions window, show tap
  preview, start with Windows, set up browser extension, open sessions folder, copy token, open config, reconnect
  Live Captions, exit. The app makes no sounds.

## config.json

Lives in `%LOCALAPPDATA%\CaptionRelay\` (or next to the exe if a `config.json` is already there: portable mode); edit
it while the app is closed (or use the menu for the common options).

| Key | Default | Meaning |
|---|---|---|
| `port` | 47321 | Loopback WebSocket port (must match the extension). |
| `token` | random | Shared secret the extension must send. |
| `sendKey` / `tapKey` / `skipKey` | `NumMinus` / `NumPlus` / `F9` | Paste everything / tap (latest words) / skip. `NumPlus`, `NumMinus`, `NumStar`, `NumSlash` (numpad keys only), any key name (e.g. `F8`, `Pause`), `CtrlShift` (press and release Ctrl+Shift) or `None`. A single key is kept by CaptionRelay (it does not reach the active app); Ctrl+Shift is not. |
| `tapHoldSendMs` | 400 | Holding the tap key at least this long pastes and sends on release. |
| `tapWords` | 15 | Words per tap of the tap key. |
| `tapPasteDelayMs` | 400 | The tap selection is pasted this long after the last tap. |
| `tapPreview` / `tapPreviewOpacity` | true / 0.6 | Show the see-through tap preview; its opacity (0.2–1). |
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
