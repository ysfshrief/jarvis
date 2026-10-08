# Architecture

JARVIS is a **modular monolith**: one always-on runtime process with clear internal modules, plus a
separate desktop interface process. No microservices, no cloud backend. The boundaries are drawn so
modules could move into separate processes later (e.g. a sandboxed plugin host) without rewrites.

## Technology choices (and why)

| Concern | Choice | Why |
| --- | --- | --- |
| Language/runtime | **C# / .NET 10 (LTS)** | First-class Windows APIs (Win32, COM, WinRT, UI Automation, DPAPI), fast, one language for runtime and desktop, self-contained deployment, free. |
| Runtime host | ASP.NET Core minimal API on Kestrel, `127.0.0.1` only | Battle-tested HTTP + WebSocket server; the same API serves the dashboard, the desktop shell and (later) the Android companion. |
| Desktop shell | **WPF** + Windows Forms tray icon | Native, mature, transparent always-on-top windows for the orb, global hotkeys. |
| Dashboard UI | React + TypeScript + Vite, hosted in **WebView2** | Rich, fast-to-build UI; WebView2 (Edge) ships with Windows 10/11; the same UI opens in any browser. |
| Storage | **SQLite** (Microsoft.Data.Sqlite) with FTS5 | Local, single file, zero admin, full-text search with Arabic normalisation. |
| Local AI | **Ollama native API** (default) or any **OpenAI-compatible** server (LM Studio, llama.cpp) | Free, private, swappable models. The native API exposes model capabilities, context size, downloads and embeddings. |
| Cloud AI (optional) | Official Anthropic SDK; any OpenAI-compatible API | Off by default; user's own key; never required. |
| Speech-to-text | **whisper.cpp** via Whisper.net | Offline, multilingual (Arabic included), MIT licensed; models downloaded on demand. |
| Text-to-speech | Windows OneCore voices (WinRT) | Offline, free, Arabic voices available as Windows language features. |
| Audio input | NAudio (WinMM) | Simple, reliable 16 kHz capture. |
| Secrets | Windows DPAPI (current user) | Bound to the Windows account, no key management. |
| Packaging | Self-contained `win-x64` publish, Inno Setup installer, portable zip | No admin rights, no .NET install, easy to locate artifacts. |
| CI | GitHub Actions (`windows-latest` + `ubuntu-latest`) | Builds, tests on real Windows, packages, smoke-tests the installed layout, publishes a pre-release. |

## Processes

```
jarvis-core.exe  (Jarvis.Runtime)    – always on; starts at sign-in (HKCU Run key, "--background")
  ├─ hosts the agent, background services and the local API on 127.0.0.1:47321 (falls forward if busy)
  ├─ writes %LOCALAPPDATA%\JARVIS\runtime.json {port, token, pid} for local clients
  └─ launches JARVIS.exe --tray (if enabled) and restarts it if it crashes (exit code ≠ 0)

JARVIS.exe       (Jarvis.Desktop)    – orb, tray, global hotkeys (from Settings → Shortcuts),
                                       command console + dashboard windows (WebView2, shared profile);
                                       native quick bar as fallback when WebView2 is missing
  └─ talks to the runtime over HTTP + WebSocket; restarts the runtime if it dies unexpectedly
```

If the interface crashes the runtime keeps going (reminders, voice, notifications continue). A clean
"Close interface" exits with code 0 and is not restarted. "Shut down JARVIS" stops both.

## Projects

```
src/Jarvis.Core               platform-neutral core (net10.0)
  Agent/        AgentOrchestrator (the loop), IntentEngine (EN/AR deterministic commands),
                ToolExecutor (permission → approval → offline queue → execute → audit), Persona, ConversationStore
  AI/           IChatProvider (+ IModelCatalog/IModelPuller/IEmbeddingProvider), OllamaProvider,
                OpenAiCompatibleProvider, AnthropicProvider, ProviderRegistry, ModelRouter, ModelManager
  Tools/        ITool, ToolDefinition (JSON schema), ToolRegistry, built-in tools (files, commands, memory,
                tasks, reminders, web search/read, system info)
  Permissions/  PermissionService (Safe/Sensitive/Critical policy), ApprovalBroker
  Memory/ Tasks/ Scheduling/ Notifications/ Presence/ Connectivity/ Activity/ Persistence/ Security/ Settings/
  Voice/        ITextToSpeech, ISpeechToText, IAudioInput, SpeechSegmenter (VAD), WakeWordMatcher
src/Jarvis.Voice              Whisper STT, model manager, VoiceService (push-to-talk, wake word, follow-up)
src/Jarvis.Platform.Windows   Windows tools and services (apps, windows, processes, audio, screenshots,
                              clipboard, keyboard, power, presence, TTS, mic, DPAPI, Recycle Bin, toasts,
                              start-with-Windows, desktop-shell watchdog)
src/Jarvis.Runtime            host: DI wiring, local API, WebSocket hub, background services, security middleware
src/Jarvis.Desktop            WPF desktop shell
ui/                           React dashboard (built into src/Jarvis.Runtime/wwwroot)
tests/                        Core (unit + agent loop), Runtime (HTTP integration), Platform.Windows (real Windows)
```

The runtime also builds for plain `net10.0` (no Windows layer) so the whole agent, API and dashboard
can be developed and tested on any OS.

## The agent loop

```
input (text / voice / quick bar / API)
 → language detection (Arabic vs English by words; Egyptians code-switch, so "افتح VS Code" is Arabic)
 → pending-approval answer? ("yes", "أيوه", "لأ")
 → deterministic intent? (open/close apps, volume, reminders, tasks, memory, time, commands…)   ← no AI
 → otherwise AI router: classify (general / reasoning / coding / vision) → first available provider
      (local preferred; cloud only if allowed and online); with no model pinned, the best installed
      model is chosen by capability (tools > family > ~8B size)
 → context budget: fit system prompt + tools + history into the model's window (old tool output
      shortened, oldest turns replaced by an extractive recap)
 → tool-calling loop (max N steps): model proposes tool calls; text streams to the UI
      (agent.turn.delta); a failing model is excluded and the router picks the next one
      (max 2 fallbacks); models without tool support converse without tool schemas
 → ToolExecutor for every call:
      assess risk of *this* call → permission policy → approval (if needed, with timeout)
      → offline? queue → execute with timeout → verify → activity log → event
 → reply in the user's language → conversation history → events to all UIs
```

Every turn publishes `agent.turn.phase` events (understanding → analyzing → selecting_tool → executing →
completed/failed) so the UI can show progress without exposing any model reasoning. Conversations are
stored locally and restored from the database after a restart.

Deterministic commands that fail (e.g. an unknown app) are handed to the AI when one is available.
Requests that need reasoning over something real ("summarize the CityCrep proposal", "why is the build
failing") run their tool first; the model then answers from that tool result, presented as if it had
called the tool itself (or as context for models without tool support), so it never summarises a file
it hasn't been shown.
Without any model, JARVIS still answers direct commands and explains how to enable conversation.

## User interface

The dashboard, the command console (`#/console`, shown by the desktop shell in a borderless window)
and any browser tab are the same React app talking to the same API:

- **Live turn store** (`ui/src/lib/turns.ts`): one subscription to the event stream builds the state of
  every request (phase, tools running, streamed text). The orb, assistant, console and context panel all
  read it, so a request started by voice shows progress everywhere.
- **Orb state** is derived, in one place, from connection, pause, recent failure, pending approvals,
  voice state and the current turn phase. The desktop orb (WPF) applies the same priority rules.
- **Appearance** settings become attributes on `<html>` (theme, accent, motion, HUD effects, density,
  text scale, language/`dir`); CSS does the rest. Motion uses transform/opacity only.
- **Sounds** are synthesized with Web Audio (no files) and de-duplicated across open windows with a
  BroadcastChannel so two windows never double a cue.
- **System metrics** are sampled by the runtime only when a page asks (`/api/system/metrics`), so a
  closed dashboard costs nothing.

## Permissions

Each tool has a base risk; each *call* is assessed (`git status` is safe, `npm run build` is
sensitive, `Remove-Item -Recurse` is critical). Policy:

1. Blocked tools never run (and are hidden from the AI).
2. Critical → always asks. No setting overrides this.
3. Per-tool override (Always allow / Always ask) for safe/sensitive.
4. Safe runs; sensitive asks unless "auto-approve sensitive" is on.

Approvals are resolved from any surface (dashboard, quick bar, voice, future phone) and time out to "no".

## Memory model

`memories` table + FTS5 index over Arabic-normalised text. Each item has a **kind** (fact, preference,
person, project, context, pattern, assumption), a **source** (user / confirmed / learned / derived), a
**confidence** and **provenance** (surface, conversation, the user's words, or for inferences the
evidence). Explicit user statements are facts (1.0). Anything inferred — by the AI on its own or by the
pattern learner — stays *unconfirmed* until the user confirms it (it becomes `confirmed`) or rejects it
(deleted, and the learner never proposes it again). Unconfirmed items reach the AI only as labelled hints.

Knowledge around memories (`KnowledgeService`):

- **Entities** (person, organization, project, place, file, event, topic) with aliases (e.g. the Arabic
  spelling), **relations** between them (`Ahmed works_at CityCrep`), and links from memories to the
  entities they mention. "What do you know about CityCrep" gathers linked memories, relationships and
  related tasks/reminders.
- **Semantic recall**: memories are embedded by a local Ollama embedding model (`bge-m3` by default,
  multilingual) in the background and stored as float32 vectors; recall ranks keyword hits, entity
  links and cosine similarity together. Without an embedding model, recall is keyword + entity only
  and the UI says so.
- **Pattern learner** (opt-in): reads JARVIS's own activity log for routines (apps opened at regular
  times, start of day, repeated requests) and preferences (language, brevity), with the evidence
  attached to each proposal.

## Workflows

`WorkflowStore` keeps long-running goals as ordered steps with dependencies (by default each step
follows the previous one). A step is *ready* when its dependencies are done or skipped. The workflow's
status is derived — `waiting` when every ready step waits on someone, `blocked`, `active`, `completed` —
never set by hand except to cancel. Steps can wait on an external party with a follow-up date;
`WorkflowService.CheckDueAsync` (every minute) sends one nudge per follow-up, near deadline and overdue
step, through the notification centre (so meetings/quiet hours still hold them). Steps flagged as business
actions run their tool action only after an explicit approval, recorded in the history. A completed
recurring workflow clones itself for the next cycle. Workflows link to the person/organisation entity
they're about, and appear in the daily briefing.

## File knowledge

Opt-in (Settings → Files). `FileIndexer` scans the chosen folders gently (one file at a time), then
follows file-system notifications; protected locations from `FilePolicy` are never read.
`DocumentExtractor` turns PDF (PdfPig), OOXML/OpenDocument (ZIP + `XmlReader` with DTDs prohibited —
nothing embedded is ever executed), RTF, text/code and ZIP listings into text plus metadata; images go
through `IOcrEngine` (Windows.Media.Ocr on Windows). `FileIndex` (schema v4) stores one row per file and
its text in 1,200-character chunks with an Arabic-normalised FTS index; chunks are embedded by the same
`SemanticIndex` as memories (owner `file_chunk`), so search is hybrid words + meaning. Files mentioning
known entities are linked to them. `TextAnalysis` provides extractive key points (labelled as extracted)
and line-level version comparison; versions are found by name stem (`v2`, `final`, dates, `- Copy`).

## Notification intelligence

`NotificationCenter.Decide` (pure, unit-tested): critical always delivered; quiet hours hold the rest;
in a meeting, high priority shows silently and normal is held; fullscreen holds below high; low priority
goes to a digest. Duplicates within 5 minutes are suppressed. Held items are delivered as one digest when
the user becomes active again. Sinks: Windows toast, voice, dashboard.

## Presence

Sampled every 2 s from Windows: foreground window/process, idle time, fullscreen/presentation state
(`SHQueryUserNotificationState`), microphone use (privacy consent store), session lock. A call app using
the microphone ⇒ "in a meeting". No camera is used.

## Local API (device protocol)

All endpoints are under `/api`, JSON, camelCase, require the token (`X-Jarvis-Token` or `Authorization:
Bearer`), and only accept loopback Host headers. Events stream on `/ws?access_token=…` as
`{type, data, timestamp}`. This is deliberately the same contract the Android companion will use, via a
paired-device token instead of the local token (see roadmap Phase 11).

## Extensibility

Everything JARVIS can do is an `ITool` registered in DI. Platform layers replace generic tools by name
(e.g. Windows `system_info`). The plugin system (Phase 9) will load tools from plugin assemblies with
declared permissions; generated plugins will never receive more than they declare.
