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
- Only Gemini is built and proven in Phases 7–10; the others come after, each with its own real test.

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
- [ ] **Phase 7 — Extension connection + provider registry**: MV3 service worker WebSocket, backoff 1/2/5 s,
      30 s alarm, ON/OFF badge, provider registry, options page (port, token, active provider).
- [ ] **Phase 8 — Gemini provider delivery**: shared web-chat module + providers/gemini/selectors.js, insert
      fallback chain with verification, keep draft, submit fallbacks, verify message appeared, ack, options "Test" button.
- [ ] **Phase 9 — Busy handling** (shared web-chat module): queue while the provider is generating, merge queued
      sends, submit when the answer ends.
- [ ] **Phase 10 — Background/unfocused tab** (highest risk): real experiments with throttling/freezing,
      execCommand without focus, open a background tab if none exists.
- [ ] **Phase 11 — Wrap-up**: README, recommended Live Captions settings, verify every "Done when" item.

## Later (after the Gemini version is done)

- [ ] **Phase 12 — ChatGPT web provider** (chatgpt.com): selectors + busy/appeared checks, real test.
- [ ] **Phase 13 — Claude web provider** (claude.ai): selectors + busy/appeared checks, real test.
- [ ] **Phase 14 — API providers** (OpenAI API and OpenAI-compatible endpoints): key storage, streaming,
      showing the answer (optional `answer` message to the app or an extension side panel).
