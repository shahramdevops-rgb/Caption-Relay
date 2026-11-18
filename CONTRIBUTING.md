# Contributing to CaptionRelay

Thanks for helping improve CaptionRelay. This page is everything you need to build, test and send changes.

## Before you start

- Read `README.md` (what the app does, how to install and use it) and `ROADMAP.md` (every phase, what was built
  and what was measured).
- `docs/CaptionRelay-course.pdf` explains the architecture and the reasons behind each design decision.
- `docs/protocol.md` is the app ↔ extension WebSocket protocol.

## Requirements

- Windows 11 with Live Captions (Win+Ctrl+L)
- .NET SDK 8 or newer
- Node.js 18 or newer (only for the extension's unit tests)
- Chrome 120+ or Edge

## Build and test

```
dotnet build CaptionRelay.sln
dotnet test CaptionRelay.sln
node --test tests/extension/*.test.mjs
```

- `dotnet test` runs the xUnit tests: unit tests, real-socket relay tests and replays of recorded Live Captions
  sessions (`tests/Fixtures`). It takes a few seconds.
- The web-chat delivery code is tested on a fake chat page: open `tests/extension/fake-chat.html` in Chrome
  (file://) and check that every result is green.
- Run the app from `app/bin/Debug/...` or publish it with
  `dotnet publish app/CaptionRelay.csproj -c Release -o publish`.
- The single self-contained exe for users: `powershell -ExecutionPolicy Bypass -File build-exe.ps1`
  → `build\CaptionRelay.exe` (the `extension` folder is embedded in it). Every build replaces that file; rebuild it
  after each code change so it is always current.
- Load the extension with `chrome://extensions` → Developer mode → Load unpacked → the `extension` folder.

## How the code is organised

| Folder | What |
|---|---|
| `app/Captions` | Reading Live Captions (UI Automation), `TranscriptMerger`, `sentIndex` / send tickets, session files |
| `app/Input` | Low-level keyboard hook and the hotkey state machine |
| `app/Sending` | `SendController` and the `IMessageSender` abstraction |
| `app/Relay` | Loopback WebSocket server (Kestrel) |
| `app/UI` | Tray icon, indicator pill (never takes focus), sounds |
| `app/Config`, `app/Infrastructure` | `config.json`, logging, single instance, startup registration |
| `extension/lib` | WebSocket connection (no `chrome.*` calls, tested in Node) |
| `extension/providers` | One folder per chat (`gemini`, `chatgpt`, `claude`) plus the shared `webchat.js` |
| `tests`, `tools` | Tests, recordings, fake extension client, key/UI test scripts, TTS speaker |

## Rules that keep the project working

1. **Never lose text.** Duplicating a few words is acceptable, dropping any is not. Keep the ticket
   confirm/restore flow intact.
2. **Never take focus.** Nothing may activate a window or send keystrokes to another app.
3. **The app and the protocol stay provider-neutral.** A new chat goes into `extension/providers/<id>/` and the
   registry; new protocol features are new optional message types, never changes to existing ones.
4. **Never log conversation text.** Logs contain lengths and diagnostics only; the token only as a fingerprint.
5. **Never commit `config.json`, `sessions/` or `logs/`.** They hold the token and conversation text and are in
   `.gitignore`.
6. Keep pure logic separate from Windows / browser APIs so it stays unit-testable, and add a test with each change.
7. If a chat site changes its page, fix only that provider's `selectors.js`; the options page **Test** button and
   the app log show which selectors matched.

## Sending changes

1. Create a branch from `main`: `git switch -c feature/short-name`.
2. Make small commits with clear messages (what and why).
3. Run `dotnet build`, `dotnet test` and the Node tests; all must pass.
4. Push the branch and open a pull request to `main`. Describe what you changed, how you tested it, and any
   real-world test you did (which chat, which browser, measured timings).
5. Update `README.md`, `docs/protocol.md` or `ROADMAP.md` when behaviour, the protocol or the plan changes.

## Ideas for next steps

- Phase 15 in `ROADMAP.md`: API providers (OpenAI API and compatible endpoints) and a place to show the answer.
- More recorded Live Captions sessions (other languages, crosstalk) for the replay tests.
