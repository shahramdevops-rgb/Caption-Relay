# CLAUDE.md — CaptionRelay: what the owner wants, what exists, how to work here

Read this first. It replaces the conversation that built this. Together with `README.md` (user guide), `ROADMAP.md`
(every phase, what was measured), `CONTRIBUTING.md` (rules) and `docs/protocol.md` (app ↔ extension messages), it
should put a new session exactly where the last one stopped. Keep it current: when a decision changes, update the
"Current behavior" and "Decided against" sections in the same change.

## What the product is

CaptionRelay helps the owner in live conversations (job interviews in English): Windows 11 **Live Captions** turns
the other person's speech into text; a Windows app (C# .NET 8, WinForms) reads that text through UI Automation and
keeps the whole transcript; with numpad keys the owner pastes the latest words into a **Gemini** (or ChatGPT / Claude)
web chat through a Chrome extension (Manifest V3) and gets an answer. Everything is local: the app and the extension
talk over a loopback WebSocket with a token.

```
speech → Live Captions → (UI Automation) → CaptionRelay app → ws://127.0.0.1:47321 → Chrome extension → Gemini tab
```

## How the owner works (follow these)

- **Never commit unless the owner says so in that message.** When they say "commit", commit; when they also say
  "PR", push the branch and open a PR to `main`. Approval for one commit is not approval for the next.
- **After every code change run `powershell -ExecutionPolicy Bypass -File build-exe.ps1`** so `build\CaptionRelay.exe`
  is current (it stops a running copy at that path, replaces it and starts it again). The exe is never committed
  (`build/` is in `.gitignore`).
- The owner dictates by voice: read charitably. "clot.md" = CLAUDE.md, "tab" = tap, "F1" once meant F7, "comment"
  = commit, "exa/exit file" = exe file, "toolbar" = taskbar. Ask only when two readings lead to different work.
- They test in their own Chrome with Gemini. After any change under `extension/` they must start the new exe once
  (it unpacks the extension) and click **reload** on CaptionRelay in `chrome://extensions` — always tell them.
- Report plainly what was tested and what was not; say so when a test touched their real browser.
- They like short answers with the result first; they send screenshots (Live Captions window, Gemini page).
- Their screen: 2560×1440. Their config: `%LOCALAPPDATA%\CaptionRelay\config.json` (sendKey `NumMinus`, tapKey
  `NumPlus`, skipKey `F9`, tapWords 15, tapPasteDelayMs 400, indicatorX 300 / indicatorY 1348). Edit it only while
  the app is stopped (the app rewrites it), and never print the token.

## Current behavior (the spec as decided with the owner)

**Keys** (single keys, no Ctrl/Alt; CaptionRelay swallows them so they never reach the active app):

| Key | Does |
|---|---|
| **Numpad +** (tap) | Each press selects 15 more of the latest words (1 = 15, 2 = 30, …). Pasted 0.4 s after the key is released, unless another press comes. **Hold ≥ 0.4 s** → on release the selection is pasted **and sent** (indicator/preview say "Release: paste + send"; while Gemini is answering: pasted now, sent when the answer ends). |
| **Numpad −** | Paste everything heard since the last skip. |
| **F9** | Skip: start fresh; older words are no longer pasted. |

Only the numpad keys (VK_ADD 0x6B / VK_SUBTRACT 0x6D) — never the main keyboard's `+=` (0xBB) / `-_` (0xBD).
Config names: `NumPlus`, `NumMinus`, `NumStar`, `NumSlash`, any `Keys` name, `CtrlShift`, `None`.

**Pasting** (extension default "paste only"):
- Text goes into the chat's input, **merged** with what is there: if the input's end matches part of the new text,
  newer words are appended and older words (more taps) are put in front; nothing appears twice; all of it already
  there → nothing added, indicator says "Already in the input". No overlap → appended after **one space** (never a
  new line).
- **Nothing is used up by pasting**: pressing again pastes the same words again (the app moves `sentIndex` back after
  a paste-only ack). Only **F9** moves the start. (In "Press Enter for me" mode submitted text *is* used up.)
- After a paste: **jump to the chat** — the extension activates the tab and window and puts the caret at the end of
  the input, **scrolled to the end** (like Ctrl+End); 200 ms later the app brings that browser window to the front
  itself if Windows refused (popup checkbox "Jump to the chat after pasting", on by default).
- Popup checkbox "Press Enter for me" (off by default) = old behavior: paste and submit, queued while answering.

**Tap preview**: while tapping, a see-through (60 %), click-through, never-focused panel above the indicator shows the
words the Live Captions window shows (read with UI Automation `TextPattern.GetVisibleRanges`) with the selection
highlighted, older words dimmed; header "Tap ×2 · last 30 words · 26 to paste · hold to send". It wraps the words its
own way (see "Decided against"). On/off: extension popup switch, taskbar-window checkbox, tray menu — kept in step.

**Indicator** (always-on-top pill): `● Captions ● Gemini` plus short flashes ("Pasted ✓", "Tap ×2 · 30 words",
"Already in the input"); no word count; only as wide as its text; default at the bottom, 300 px from the left edge
(owner's choice, under Gemini's input); draggable, position saved.

**On/off**: the popup switch ON starts the app through the native messaging host (the app starts Live Captions if
needed); OFF sends `quit` → the app exits **and closes Live Captions**. The app has a taskbar window (starts minimized,
never takes focus); closing it or menu Exit also exits and closes Live Captions. No sounds.

**Install**: one self-contained `CaptionRelay.exe` (≈ 74 MB, .NET runtime + extension embedded). On start it unpacks
the extension to `%LOCALAPPDATA%\CaptionRelay\extension`, registers the native messaging host
`com.captionrelay.host` (HKCU, Chrome + Edge; gives the extension port + token, launches the app), and on first run
shows a guided "Load unpacked" window (Chrome does not let programs install non-store extensions). Extension id
`hpmjjnjmlhbolpmeafdkmdldlfmoaaee`.

## Decided against (tried, then reverted or replaced — do not bring back unless asked)

- Switch OFF leaving Live Captions open (and `closeLiveCaptionsOnSwitchOff`): reverted, "bad decision".
- Tap preview drawn line-for-line like the Live Captions window (same lines, width, font size from line rects):
  reverted together with the above.
- Auto-submit as default; app-side "repeat words" trimming and using text up after a paste; blank-line separators.
- Earlier keys: Ctrl+Shift (send), F7 / F8, 50 words per tap, 0.9 s paste delay.
- Indicator at bottom-right, at 17 px from the left, or with "N words pending".

## Code map

| Path | What |
|---|---|
| `app/Program.cs` | Startup, native-host mode, single instance, wiring, shutdown order (hook → relay → capture → LC window → close LC). |
| `app/Captions/` | `CaptionsReader` (UI Automation: `CaptionsTextBlock`, `ReadVisible`), `CaptionCapture` (transcript, `sentIndex`, `Take` / `TakeLast` / `ConfirmPasted` / `PreviewLast`), `TranscriptMerger`, `SessionWriter`. |
| `app/Input/` | `KeyboardHook` (WH_KEYBOARD_LL; swallows single-key hotkeys; measures how long the tap key is held), `HotkeyStateMachine`, `Hotkey` (numpad names). |
| `app/Sending/SendController.cs` | What the keys do: taps, hold-to-send, delivery outcomes, focus requests. |
| `app/Relay/RelayServer.cs` | Kestrel loopback WebSocket, protocol, settings push. |
| `app/UI/` | `TrayContext` (glue, tray menu), `IndicatorForm`, `TapPreviewForm`, `MainForm` (taskbar window), `SetupForm`, `ChatWindow` (brings the browser window forward). |
| `app/Setup/` | `NativeHost`, `ExtensionInstaller`, `AppLauncher`. |
| `extension/` | `background.js` (service worker), `lib/connection.js`, `providers/webchat.js` (content script: insert, merge, focus, scroll, queue), `providers/webprovider.js` (tab handling, focus), `popup.*`, `options.*`. |
| `tests/` | xUnit (`*.cs`), `tests/extension/connection.test.mjs` (Node), `tests/extension/fake-chat.html` (browser page with fake Gemini/ChatGPT/Claude). |
| `tools/` | `RelayTestClient` (fake extension), `keytest/press.ps1` (drives keys in a test window), `tts/`, `CaptionRecorder`. |

## Build and test

```
dotnet build CaptionRelay.sln
dotnet test CaptionRelay.sln                       # xUnit (215 at the last commit)
node --test tests/extension/connection.test.mjs    # Node (19)
powershell -ExecutionPolicy Bypass -File build-exe.ps1   # → build\CaptionRelay.exe
```

Browser test page (42 checks) in headless Chrome; the page title says `PASSED n/n`:

```
"C:\Program Files\Google\Chrome\Application\chrome.exe" --headless=new --disable-gpu --user-data-dir=<temp dir>
  --allow-file-access-from-files --virtual-time-budget=60000 --dump-dom "file:///C:/Users/Ali/Desktop/Caption-Relay/tests/extension/fake-chat.html"
```

## Live tests with the real exe (safely)

1. Start the exe; **wait until the log shows `Extension connected: CaptionRelay extension`** (the owner's real
   extension reconnects within seconds).
2. Then start the fake extension so it is the most recent connection (the most recent one receives sends):
   `tools\RelayTestClient\bin\Debug\net8.0-windows\RelayTestClient.exe --config "%LOCALAPPDATA%\CaptionRelay\config.json" --script sent --pasteOnly [--focus] --seconds 60`
3. Check the log's last `Extension connected:` line says `RelayTestClient` **before pressing any key**. Once, test
   pastes went into the owner's real Gemini input because the real extension connected after the fake one.
4. Drive keys in a throw-away foreground window: `tools\keytest\press.ps1 -Steps "say:<text>|wait:2500|tap|send|hold:NumPlus:700|down:NumPlus|shot:<png>|up:NumPlus|seen|fg|check"`
   (`say` speaks with TTS so Live Captions captions it; `seen` lists keys that reached the window; `fg` names the
   program in front).
5. Logs: `%LOCALAPPDATA%\CaptionRelay\logs\captionrelay-yyyyMMdd.log` (never contains conversation text).
6. If the app was not running before the test, switch it off again afterwards (`RelayTestClient --quit`).

## Environment notes (this PC)

- Windows 11, .NET SDK 8, Node, Chrome. Shell tools: Git Bash and Windows PowerShell 5.1.
- **GitHub CLI (`gh`) is not installed.** PRs were opened with the GitHub REST API using git's stored login
  (`git credential fill` for github.com, token kept in memory only, never printed). Remote:
  `https://github.com/shahramdevops-rgb/Caption-Relay.git`.
- Long inline Python/PowerShell heredocs in Git Bash sometimes fail to parse — write the script to a file and run it.
  PowerShell variable names are case-insensitive (`$s` overwrites `$S`).
- Git normalizes line endings (`core.autocrlf`); its "LF will be replaced by CRLF" warnings are harmless. Keep a
  file's existing line endings when editing it.
- Live Captions UI Automation: text element `CaptionsTextBlock` (supports TextPattern); `GetVisibleRanges` returns
  one range per line **including lines scrolled out of view** (those have infinite rectangles). Starting Live
  Captions brings its window to the front (known, not fixed).

## Where things stand (2026-11-19)

- Branch `feature/single-exe` (pushed, tracks `origin/feature/single-exe`), ahead of `main` by: Phase 16 (one exe),
  17 (popup), 18 (switch starts/closes app, taskbar window, no sounds), 19–22 (paste only, tap keys, numpad, preview,
  indicator), 23 (jump to the chat, scroll to end), and this CLAUDE.md. A PR from `feature/single-exe` to `main` is
  open on GitHub (an earlier PR #1 was closed by the owner's request to add this file first).
- All tests pass. `build\CaptionRelay.exe` is built from the latest code.
- **Not yet verified with the owner's reloaded extension**: the full "jump to the chat" chain (tab → window → caret
  → scrolled to the end) and holding numpad + to send. Everything else was checked with the real exe, Live Captions
  and a fake extension, and pasting into real Gemini was used by the owner.
- Next: wait for the owner's bug reports; fix, test, rebuild the exe, and do not commit until told.
