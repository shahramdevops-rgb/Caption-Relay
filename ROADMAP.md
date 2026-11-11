# CaptionRelay roadmap

Each phase ends with a checkpoint: `dotnet build` (0 errors), tests pass, a real run/test where possible,
and a local commit.

- [x] **Phase 0 — Skeleton**: sln, app/ (net8.0-windows, WinForms + UseWPF, Microsoft.AspNetCore.App,
      ImplicitUsings off), tests/ (xUnit), extension/ stub, .gitignore, README/ROADMAP, git init.
- [x] **Phase 1 — Config + logs**: config.json next to the exe with defaults and a random 43-char token on first run
      (missing fields filled, invalid values reset, invalid JSON never overwritten); rolling daily logs in logs/ next to
      the exe (5 MB per file, 7 days kept, no transcript text); single-instance named mutex.
- [ ] **Phase 2 — TranscriptMerger + xUnit tests** (most important): growth, scroll-off, rewritten last words,
      cleared/restarted captions, repeated polls, case/punctuation-insensitive matching.
- [ ] **Phase 3 — Continuous capture**: ~120 ms poller, sentIndex/pending, sessions/*.txt with [SENT]/[SKIPPED] markers.
- [ ] **Phase 4 — Hotkeys**: Ctrl+Shift alone = send, F9 = skip, 300 ms double-press guard, minSendChars.
- [ ] **Phase 5 — WebSocket server**: loopback Kestrel, token + Origin check, hello/ping/pong/send/ack,
      3 s ack timeout restores sentIndex, console test client.
- [ ] **Phase 6 — Tray + indicator pill**: no-focus draggable pill, saved position, flash messages, expand panel, optional sound.
- [ ] **Phase 7 — Extension connection**: MV3 service worker WebSocket, backoff 1/2/5 s, 30 s alarm, ON/OFF badge, options page.
- [ ] **Phase 8 — Delivery into Gemini**: selectors.js, insert fallback chain with verification, keep draft,
      submit fallbacks, verify message appeared, ack, options "Test" button.
- [ ] **Phase 9 — Busy handling**: queue while Gemini is generating, merge queued sends, submit when the answer ends.
- [ ] **Phase 10 — Background/unfocused tab** (highest risk): real experiments with throttling/freezing,
      execCommand without focus, open a background tab if none exists.
- [ ] **Phase 11 — Wrap-up**: README, recommended Live Captions settings, verify every "Done when" item.
