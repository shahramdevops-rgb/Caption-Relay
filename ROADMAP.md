# CaptionRelay roadmap

Each phase ends with a checkpoint: `dotnet build` (0 errors), tests pass, a real run/test where possible,
and a local commit.

## Architecture decision: provider abstraction

The Windows app and the WebSocket protocol know nothing about Gemini. They deliver
`{type:"send", id, text}` and receive `{type:"ack", id, status, detail, provider}`; the extension decides
where the text goes. Adding ChatGPT, Claude or an API later must not change the app or the protocol.

- **Protocol**: `hello` carries `protocol: 1`; `ack.status` is generic (`sent` / `queued` / `error`) and
  `ack.provider` names who handled it (shown in the indicator and the log). New features are added only as
  new optional message types, never by changing existing ones.
- **Extension**: `extension/providers/<id>/` per provider, registered in one registry. The active provider is
  chosen on the options page. Two kinds share one interface (`id`, `name`, `deliver(text)` → ack):
  - *Web-chat providers* (Gemini, ChatGPT, Claude.ai): content script + their own `selectors.js`; common
    logic (insert fallback chain, verify, keep draft, submit, busy queue, background tab) lives in a shared
    `web-chat` module, each provider only supplies selectors and "is busy / did it appear" checks.
  - *API providers* (OpenAI API, OpenAI-compatible endpoints): `fetch` from the service worker with the API
    key in extension storage. There is no chat page, so they need a place to show the answer (planned: an
    optional `answer` message back to the app's indicator panel, or an extension side panel).
- Only Gemini is built and proven in Phases 7–10; the others come after (Phases 13–15), each with its own real test.

## Phases

- [x] **Phase 0 — Skeleton**: sln, app/ (net8.0-windows, WinForms + UseWPF, Microsoft.AspNetCore.App,
      ImplicitUsings off), tests/ (xUnit), extension/ stub, .gitignore, README/ROADMAP, git init.
- [x] **Phase 1 — Config + logs**: config.json next to the exe with defaults and a random 43-char token on first run
      (missing fields filled, invalid values reset, invalid JSON never overwritten); rolling daily logs in logs/ next to
      the exe (5 MB per file, 7 days kept, no transcript text); single-instance named mutex.
- [x] **Phase 2 — TranscriptMerger + xUnit tests** (most important): word-level, case/punctuation-insensitive
      alignment that tolerates rewrites; MapIndex keeps sentIndex correct across rewrites. Tests: growth, scroll-off,
      rewritten last words, I I I I → II, C sharp → C#, cleared/restarted, repeated polls, repeated phrases, plus replay
      of a real 4-minute Live Captions recording (tools/CaptionRecorder + tools/tts/speak.ps1).
      Learned: a freshly started Live Captions kept all 4 minutes (616 words) on screen without scrolling, and
      rewrote words long after they appeared ("dot net eight" → ".net 8", "forty two" → "42", "I I I" → "III").
- [x] **Phase 3 — Continuous capture**: CaptionCapture polls every pollMs on a background thread, keeps sentIndex
      (Take/Confirm/Restore/Skip, all positions mapped through merges), ignores text on screen at startup
      ([SKIPPED] "text on screen at startup"), restarts Live Captions every 5 s if closed, writes
      sessions/yyyy-MM-dd_HHmm.txt every 2 s (last 40 words held back while speaking, an unconfirmed send blocks
      the file at its cut), deletes sessions older than sessionRetentionDays (30).
      Real run: app + 10 min of TTS; Live Captions scrolled 11 times after ~4,300-4,800 chars (~800 words),
      dropping 60-80 words at a time; the session file had all 1,547 words (97.5% of the script, rest are
      recognition errors), no duplicates; app used 3 s CPU and 72 MB.
- [x] **Phase 4 — Hotkeys**: KeyboardHook (WH_KEYBOARD_LL on its own thread, never swallows keys) +
      HotkeyStateMachine (unit-testable, from LiveCaptionsGemini) + SendController (300 ms guard, minSendChars,
      prefix, text kept when not connected / failed / no response). Real test (tools/keytest/hotkey-test.ps1, keys
      sent only to a temporary test window): all 12 scenarios as expected (Ctrl+Shift either order, right Ctrl,
      auto-repeat, double press ignored, Ctrl+Shift+T / +F24 / Ctrl+F9 / Ctrl or Shift alone do nothing, F9 skips,
      nothing new after skip); no stuck keys, foreground restored. F9 still reaches the active app (by design).
- [x] **Phase 5 — WebSocket server**: RelayServer (loopback Kestrel, chrome-extension:// origins only +
      optional allowedExtensionIds, constant-time token check in hello, welcome/ping/pong/send/ack, latest
      connection receives, 3 s ack timeout → text kept, 65 s idle close), protocol in docs/protocol.md,
      tools/RelayTestClient (fake extension). 26 integration tests with real sockets. Real run: TTS + Ctrl+Shift
      with the fake extension answering sent / no ack / queued / error / sent: every result and text-kept case as
      expected, session file has [SENT] only for delivered sends; wrong token and web origin rejected;
      hotkey → ack round trip 54 ms (50 ms of it the client's simulated delay).
- [x] **Phase 6 — Tray + indicator pill**: IndicatorForm (custom-painted pill that never activates:
      ShowWithoutActivation, NOACTIVATE|TOOLWINDOW|TOPMOST in CreateParams, MA_NOACTIVATE; click toggles a panel
      above it with the pending text, drag moves it, position saved and checked against current monitors, 1.5 s
      flash messages, title mirrors the text for UI Automation), tray icon drawn at runtime (green/red dot) with
      menu (show/hide indicator, sound, hide Live Captions window, sessions folder, config, reconnect, exit),
      generated tones (sent/queued/error). Provider label comes from the extension's hello. Real test
      (tools/keytest/ui-test.ps1): clicks and triple clicks toggle, drag moves and is restored after restart,
      foreground never changes, menu via right click (opens on button up; focus returned after), hide/restore
      Live Captions window exact, exit while hidden restores it.
- [x] **Phase 7 — Extension connection + provider registry**: MV3 extension with a fixed id
      (hpmjjnjmlhbolpmeafdkmdldlfmoaaee via "key"), lib/connection.js (hello with provider, ping/pong, ack,
      backoff 1/2/5 s, testable without chrome.*), service worker with a 30 s alarm and ON/OFF badge, provider
      registry (Gemini stub until Phase 8), options page (port, token, provider, live status, reconnect),
      "Copy token" in the app menu. 11 Node tests. Real Chrome: connected after pasting the token; app restart →
      reconnected 0.9 s after the server was up; Ctrl+Shift → ack from Chrome in 4 ms (error "not implemented
      yet", text kept); stayed connected through 4 idle minutes.
- [x] **Phase 8 — Gemini provider delivery**: providers/webchat.js (shared: insert execCommand → paste → direct set,
      each verified; draft kept as "draft, blank line, text"; submit via send button → closest send-like button (never
      mic/upload) → Enter; verified by new user turn / emptied box / generation start), providers/gemini/selectors.js
      (checked on the live page: gem-icon-button.send-button > button "Send message", .stop > "Stop response"),
      most recently used Gemini tab or a new background tab (ack "queued"), options "Test" (insert without sending +
      selector report), extension diagnostics forwarded to the app log (protocol "log"). 13 fake-page tests in a real
      browser. Real Gemini (Chrome, via hotkey): sent in 92–113 ms in-page / ~110 ms from the hotkey; draft kept;
      sending while Gemini answers → "Gemini is still answering", text kept and sent after the answer (stop never
      clicked, 2,166-word answer complete); no Gemini tab → background tab opened and the text delivered there while
      hidden and unfocused (~4 s).
- [x] **Phase 9 — Busy handling** (shared web-chat module): while the chat answers, sends are queued in the page
      (ack "queued"), watched with a MutationObserver + 250 ms poll, merged with a blank line into ONE message and
      submitted when the answer ends (stop button never clicked); given up after 3 min. New protocol message
      "result" reports the final outcome per id; the app keeps the text unconfirmed until then (error or no result
      in 200 s → back to pending); the indicator shows "· queued" until delivered; a tab holding queued text stays
      the target. Real Gemini: a text sent mid-answer was submitted automatically 33 s later after a 3,800-word
      answer; two texts sent mid-answer were queued in the same tab and submitted as one message; session file has
      [SENT …] (queued) for both. Found and fixed: a later send went to another Gemini tab the user was viewing.
- [x] **Phase 10 — Background/unfocused tab** (highest risk): the target tab is marked not auto-discardable; a
      quick ping checks the content script first and a discarded or unresponsive (frozen) tab is reloaded and the text
      delivered after load ("queued" + result); waits use a MutationObserver instead of short timers (Chrome delays
      timers in hidden pages to ~1 s). Real experiments (Chrome 157): execCommand + click work without focus in a hidden
      tab; tab behind another tab: sent in 51–57 ms in-page (~60 ms from the hotkey); minimized window: 69 ms
      (was 1,064 ms before the timer fix); after 7 min minimized (intensive throttling) still delivered, tab neither
      frozen nor discarded; Gemini keeps generating in a hidden tab and the queue flushed there (29 s); the tab stayed
      usable after ~4 h in the background; no Gemini tab → background tab opened and delivered (Phase 8).
      Not testable here: a real Memory Saver discard / freeze (prevented by autoDiscardable, handled by reload).
- [x] **Phase 11 — Wrap-up**: full README (build, extension install, token, Live Captions settings, use, config,
      troubleshooting), "Start with Windows" option, single-file publish in publish\ + desktop shortcut. Every
      "Done when" item verified with the published app on real Gemini:
      1. ~4 min of speech, no key pressed, then one Ctrl+Shift → one 623-word message (script 622 words, 97.4% in
         order — the rest are Live Captions' own recognition errors), 80 ms from the hotkey; nothing missing (a 3-word
         "ok record" duplicate at the very start from Live Captions' first draft).
      2. Focus stayed on the window in use during every send.
      3. F9 skipped; Ctrl+Shift with nothing new sent nothing; Ctrl+Shift+T sent nothing.
      4. Sent while Gemini answered → queued → submitted automatically 27 s later.
      5. Extension off → "Gemini not connected", text kept; back on → kept text sent with the new text.
      6. App restart → reconnected 0.9 s after the server was up; Chrome closed and reopened → reconnected by itself.
      Also: a hung page (no ping answer) → tab reloaded and the text delivered in the same conversation (9 s); the
      main Chrome (Gemini Pro) delivered in 91 ms with busy detection working. Bug found and fixed: a missed
      modifier key-up made the next Ctrl+Shift be ignored once.

- [x] **Phase 12 — Learning guide (PDF)**: a clean, illustrated PDF that teaches how the project was designed —
      ideas, architecture, layers, data flow, protocol, testing strategy and the decisions behind them, with
      schematic diagrams (not a code walkthrough). Result: `docs/CaptionRelay-course.pdf` (44 pages, 17 chapters,
      26 diagrams). Persian text with English terms; every chapter ends with an English "In an interview" summary
      and interview questions. Sources in `docs/course/` (HTML chapters, SVG diagrams, one stylesheet);
      `python docs/course/build.py` prints it with headless Edge, fills the table of contents with page numbers and
      first runs an RTL check (every Persian line starts with a Persian word; no English on both sides of a quote,
      bracket, dash or Persian comma; no English inside «»).

## Later (after the Gemini version is done)

- [x] **Phase 13 — ChatGPT web provider** (chatgpt.com): selectors + busy/appeared checks, real test.
      Tab handling moved to the shared `providers/webprovider.js` (Gemini and ChatGPT are config only); the content
      script waits up to 3 s for the editor while the page hydrates. Real test 2026-11-13 (Free plan, English UI):
      send, draft kept, busy → queue → merge, minimized window (send + queue), fresh page load, options Test, and a
      Gemini send after the refactor.
- [x] **Phase 14 — Claude web provider** (claude.ai): selectors + busy/appeared checks, real test.
      Same pattern as ChatGPT (config + selectors; claude.ai has stable data-testid attributes). Real test 2026-11-13
      (Haiku, English UI): options Test, send, draft kept, busy → queue → merge, minimized window (send + queue),
      fresh page load.
- [x] **Phase 16 — One exe + guided extension setup**: `build-exe.ps1` → `build\CaptionRelay.exe` (replaced on every build), self-contained
      single file (73.7 MB, .NET runtime + extension embedded, no other files). Data moved to
      `%LOCALAPPDATA%\CaptionRelay` (portable mode kept when config.json sits next to the exe). On every start the exe
      unpacks the extension and registers the native messaging host `com.captionrelay.host` (HKCU, Chrome + Edge); the
      extension asks it for port + token when it has none or the app refuses its token, so no token copying. First run
      opens a setup window (open extensions page, path on the clipboard, turns green on connect). Chrome only allows
      store extensions to be installed by programs, so "Load unpacked" stays one manual step.
      Measured 2026-11-18: exe starts, extracts 23 files, registers both hosts, shows the setup window; called like
      Chrome (origin argument + framed stdin), the exe answered with port + token in 162 ms. Not yet done: loading the
      unpacked extension in real Chrome and the first automatic connection.
- [x] **Phase 17 — Extension popup**: the toolbar icon opens a popup (no new tab) with an on/off switch (off =
      disconnected, badge grey OFF, alarms do not reconnect), live status, the chat to deliver to, and the app's
      hotkeys with what they do. The keys come from new optional `welcome` fields `sendKey` / `skipKey` (last known
      keys cached for when the app is not running). "All settings" opens the options page. Checked in Chrome with a
      stubbed chrome.* API: connected, turned off and not-connected states, light/dark colours.
- [x] **Phase 18 — App on/off from the popup, taskbar window, no sounds**: the popup switch now controls the app:
      off sends the new `quit` message (app exits and closes Live Captions), on asks the native host to `launch` the
      app (started outside Chrome's job object, Explorer as fallback; "already running" if it is). The switch shows ON
      exactly while the app is connected. New taskbar window (starts minimized without activating; status + hotkeys;
      closing it exits everything). Live Captions is closed on every exit (`closeLiveCaptionsOnExit`, default true).
      Sounds removed. Measured 2026-11-18 with build\CaptionRelay.exe: taskbar window minimized, not a tool window,
      CaptionRelay never became the foreground window; closing it → app and Live Captions gone, log "Exiting
      normally"; native-host launch → app running after the host exited ("--from-extension"), second launch →
      "already running"; quit from RelayTestClient --quit → app and Live Captions gone 80 ms later.
      Not tested here: the popup in real Chrome (launch from inside Chrome's job object).
      Known: starting Live Captions brings its window to the front (as before this phase).
- [x] **Phase 19 — Paste only (default)**: the send key now only pastes the new speech into the chat's input (after
      whatever is already there, joined with one space, never a new line) and does not submit it; the user reviews it and presses Enter. Popup checkbox
      **Press Enter for me** (`autoSubmit` in extension storage, default off) brings back paste + submit with the
      busy queue (queued texts are also joined with a space). Paste only never queues: inserting works while the chat is answering. New optional
      `submitted: false` on `ack` / `result`; the app shows `Pasted ✓` and writes `[SENT … (pasted, not submitted)]`
      in the session file. Checked: fake-page tests (paste after draft, paste while answering, two pastes in a row,
      nothing clicked), Node and .NET tests. Not tested here: real Gemini / ChatGPT / Claude pages.
- [x] **Phase 20 — Tap for the latest words, F keys, preview, indicator**: new tap key (F7): each tap selects 15
      more of the latest words (`tapWords`), pasted 0.4 s after the last tap (`tapPasteDelayMs`). Keys moved to single
      F keys: F8 paste everything since the last skip (was Ctrl+Shift), F7 tap, F9 skip; single-key hotkeys are now
      swallowed by the hook (down, repeats and up), so e.g. F7 no longer opens Chrome's caret-browsing prompt.
      Pasting no longer uses text up: after a paste-only ack sentIndex goes back (`ConfirmPasted`), so pressing again
      pastes the same words again; only F9 moves the start (submitted sends, "Press Enter for me", are still used
      up). To avoid doubles the extension merges the text with what the input box holds: the box's last ≤ 40 words (at
      least 3, case/punctuation ignored) are found in the text and the match extended backwards; newer words are
      appended, older words (more taps) go in front of the matched part; all of it there → ack `alreadyInInput`,
      indicator "Already in the input". (First version only appended newer words, so 1 tap then more taps said
      "already in the input" and never added the older words; fixed the same day.) Click-through, never-focused, 60 % opaque tap preview above the indicator (selection highlighted,
      older words dimmed; menu **Show tap preview**). Indicator: no word count any more (`● Captions ● Gemini` +
      flashes), as wide as its text, default 300 px from the left edge at the bottom. Welcome carries `tapKey` /
      `tapWords` for the popup.
      Measured 2026-11-19 with build\CaptionRelay.exe + TTS: F8 twice → the same 134 chars both times (nothing used
      up); 2 taps → last 30 words; F7/F8 never reached the foreground test window (F6 did); focus never moved; preview
      see-through above the indicator at x = 300. The input-box overlap rule was checked on the fake chat pages (36/36);
      not yet with the reloaded extension on real Gemini.
- [x] **Phase 21 — Hold to send, numpad keys, preview switch in the UI**: holding the tap key (≥ 0.4 s,
      `tapHoldSendMs`) arms "send on release" (indicator "Release: paste + send", preview header says so); on release
      the selection is sent with `send.submit = true`: the extension merges it with the input and sends it, even in
      paste-only mode (chat answering → pasted now, sent when the answer ends). Like a paste, it is not used up.
      Quick taps now paste 0.4 s after the key is released (nothing happens while a key is held). The hook measures
      the hold (key-down → key-up) and treats a key-down more than 1.5 s after the last event of a swallowed key as a
      new press (missed key-up). Keys: numpad + = tap, numpad − = paste everything (`NumPlus` / `NumMinus`, i.e.
      VK_ADD / VK_SUBTRACT, never the main keyboard's VK_OEM_PLUS / VK_OEM_MINUS), F9 = skip; shown as "Num +" /
      "Num -". **Show tap preview** switch in the extension popup (`setTapPreview` → app, `settings` / welcome
      `tapPreview` → popup) and a checkbox in the taskbar window, in step with the tray menu item.
- [x] **Phase 22 — Tap preview mirrors the Live Captions window**: while tapping, the app reads the lines visible in
      the Live Captions window (UI Automation TextPattern.GetVisibleRanges on CaptionsTextBlock, on a background
      thread, at most every 250 ms), finds them in the transcript (first ≤ 8 words, latest occurrence; rewritten top
      words skipped and counted back) and shows exactly that stretch in the preview, the selection highlighted. A
      selection reaching above the window is shown in full with "(N above the Live Captions window)". Falls back to
      the old context when the window's text is not found. Checked 2026-11-19: probe saw 4 visible lines / 32 words,
      later 10 lines / 73 words in a 540×365 window; screenshots of 1 and 3 taps showed the preview with the window's
      exact text and the last 15 / 41 words highlighted.
- [ ] **Phase 15 — API providers** (OpenAI API and OpenAI-compatible endpoints): key storage, streaming,
      showing the answer (optional `answer` message to the app or an extension side panel).
