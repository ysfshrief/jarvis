# JARVIS — notes for Claude Code

Windows-native personal AI assistant. Read `docs/ARCHITECTURE.md` first; `docs/ROADMAP.md` holds the
honest status of every capability and must be kept true.

## Build & test

- Dashboard: `cd ui && npm ci && npm run build` (outputs to `src/Jarvis.Runtime/wwwroot`, git-ignored).
- Everything: `dotnet build Jarvis.slnx`. Windows projects compile on Linux (`EnableWindowsTargeting`).
- Tests on any OS: `dotnet test tests/Jarvis.Core.Tests` and `dotnet test tests/Jarvis.Runtime.Tests`.
- Windows-only tests (`tests/Jarvis.Platform.Windows.Tests`) and the packaged-app smoke test run in CI
  on `windows-latest`; check the Actions run after pushing.
- Run the runtime on Linux for API/UI work: `JARVIS_DATA_DIR=/tmp/jd dotnet run --project src/Jarvis.Runtime -f net10.0`.

## Rules of the codebase

- Every capability is an `ITool` routed through `ToolExecutor` (permission → approval → offline queue →
  execute → audit). Never call platform code from the agent directly.
- Grade risk per call in `Assess`; Critical always requires approval — don't add bypasses.
- Never report success that wasn't observed; never mock a feature and call it done. Update
  `Capabilities.cs` and `docs/ROADMAP.md` when a capability's status changes.
- User-facing text is bilingual (English + Egyptian Arabic) via `ctx.T(en, ar)`.
- Prefer deterministic intents (`IntentEngine`) over AI for simple commands; add tests for new patterns.
- Free-first/local-first: no mandatory paid services; cloud features are optional and off by default.
- Secrets only via `ISecretStore`; never in settings, logs or responses.
