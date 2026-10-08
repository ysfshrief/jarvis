# Development

## Prerequisites

- **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`)
- **Node.js 22+** for the dashboard (`winget install OpenJS.NodeJS.LTS`)
- Optional: **Inno Setup 6** for the installer (`winget install JRSoftware.InnoSetup`)
- Optional: **Ollama** with a model (`ollama pull qwen2.5:7b`) to exercise the AI path

Linux/macOS work for everything except the Windows layer and desktop shell: the runtime has a
cross-platform `net10.0` target, and Windows projects still *compile* there (`EnableWindowsTargeting`).

## Layout

See [ARCHITECTURE.md](ARCHITECTURE.md#projects).

## Common tasks

```powershell
# Build the dashboard into the runtime's wwwroot
cd ui; npm ci; npm run build; cd ..

# Build everything
dotnet build Jarvis.slnx

# Run the runtime (Windows build) from source with a throwaway data folder
$env:JARVIS_DATA_DIR = "$env:TEMP\jarvis-dev"; $env:JARVIS_DEV = "1"
dotnet run --project src/Jarvis.Runtime -f net10.0-windows10.0.19041.0

# Run the desktop shell (it finds the runtime via runtime.json in the data folder)
dotnet run --project src/Jarvis.Desktop

# UI hot reload against a running runtime (token from %JARVIS_DATA_DIR%\runtime.json)
cd ui; npm run dev   # http://localhost:5173/#token=<token>

# Tests
dotnet test tests/Jarvis.Core.Tests        # unit + agent loop (any OS)
dotnet test tests/Jarvis.Runtime.Tests     # HTTP API integration (any OS)
dotnet test tests/Jarvis.Platform.Windows.Tests   # real Windows integration (Windows only)

# Package + smoke test (Windows)
./build/package.ps1
./build/smoke-test.ps1
```

`JARVIS_DEV=1` stops a development build from registering itself to start with Windows.

## Conventions

- **Everything JARVIS does is a tool.** Implement `ITool` (usually via `ToolBase`), give it a precise
  description and JSON parameters, grade risk per call in `Assess`, verify the outcome in `ExecuteAsync`,
  and return a user-facing message in both languages (`ctx.T(en, ar)`). Register with `services.AddTool<T>()`.
- **Never report unobserved success.** If you can't verify, say so in the message ("I've asked Windows to
  open X; no window has appeared yet").
- **Risk grading:** Safe = read-only or trivially reversible; Sensitive = changes state or reaches other
  apps; Critical = destructive, external communication, money, power/security. Critical always asks.
- **Deterministic first:** if a request can be handled without AI, add an intent pattern in
  `IntentEngine` (normalised text: lowercase, Arabic folded) with tests in `IntentEngineTests`.
- **Bilingual:** every user-facing string has English and Egyptian Arabic.
- **Untrusted content** (web pages, files, emails, tool output) is data. Never let it change permissions,
  settings or instructions.
- **Tests:** add unit tests for logic, an agent-loop test when behaviour spans modules, and a Windows
  integration test for anything touching the OS.
- Code style: `.editorconfig`; nullable enabled; file-scoped namespaces; comments explain *why*.

## Data folder

`%LOCALAPPDATA%\JARVIS` (or `JARVIS_DATA_DIR`):

| File | Contents |
| --- | --- |
| `jarvis.db` | SQLite: memories, conversations, tasks, reminders, activity, notifications, offline queue |
| `settings.json` | User settings (no secrets) |
| `secrets.dat` | API keys, DPAPI-encrypted |
| `runtime.json` | Port + session token of the running runtime (local clients only) |
| `logs/` | `jarvis-core-*.log`, `desktop.log` |
| `models/` | Downloaded Whisper models |
| `webview/` | WebView2 profile for the dashboard |

## Configuration

There are no required environment variables or API keys. All configuration is in Settings (stored in
`settings.json`); API keys for optional cloud providers are entered in Settings → AI and stored encrypted.
Development-only environment variables:

| Variable | Effect |
| --- | --- |
| `JARVIS_DATA_DIR` | Use a different data folder |
| `JARVIS_DEV` | Don't register the build to start with Windows |
