# CaptionRelay

One global hotkey sends everything Windows Live Captions heard since your last send into your open
Gemini web chat and submits it, without moving focus.

- `app/` — Windows tray app (C# .NET 8, WinForms): captures Live Captions continuously, global hotkeys,
  loopback WebSocket server.
- `extension/` — Chrome/Edge Manifest V3 extension: receives the text and submits it in gemini.google.com.
- `tests/` — xUnit tests.

## Build

```
dotnet build CaptionRelay.sln
dotnet test CaptionRelay.sln
```

Status: work in progress — see [ROADMAP.md](ROADMAP.md). Full setup instructions come in Phase 11.
