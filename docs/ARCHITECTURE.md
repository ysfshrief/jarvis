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

JARVIS.exe       (Jarvis.Desktop)    – orb, tray, quick bar, hotkeys, dashboard window (WebView2)
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
Without any model, JARVIS still answers direct commands and explains how to enable conversation.

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
person, project, context, pattern, assumption), a **source** (user / confirmed / learned / derived) and a
**confidence**. Explicit user statements are 1.0; learned patterns start low and are labelled as hints in
the AI prompt. Context items can expire. Users can view, search, edit, delete, clear by kind, and choose
which kinds may be stored.

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
