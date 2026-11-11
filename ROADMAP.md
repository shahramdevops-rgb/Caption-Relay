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
- [ ] **Phase 2 — TranscriptMerger + xUnit tests** (most important): growth, scroll-off, rewritten last words,
      cleared/restarted captions, repeated polls, case/punctuation-insensitive matching.
- [ ] **Phase 3 — Continuous capture**: ~120 ms poller, sentIndex/pending, sessions/*.txt with [SENT]/[SKIPPED] markers.
- [ ] **Phase 4 — Hotkeys**: Ctrl+Shift alone = send, F9 = skip, 300 ms double-press guard, minSendChars.
- [ ] **Phase 5 — WebSocket server**: loopback Kestrel, token + Origin check, provider-neutral protocol
      (hello with protocol version, ping/pong, send, ack with provider), 3 s ack timeout restores sentIndex,
      console test client.
- [ ] **Phase 6 — Tray + indicator pill**: no-focus draggable pill, saved position, flash messages, expand panel, optional sound.
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
