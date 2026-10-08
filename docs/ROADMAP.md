# Roadmap

Built in vertical slices: every phase ends with something you can install and use. Statuses are honest:

- **Working** — real, tested, in the current build.
- **Partial** — works, with stated limits.
- **Foundation** — architecture/interfaces exist; the user-facing feature is not there yet.
- **Planned** — not started.

The same matrix is visible in the app under **System → What this build can do**.

## Phase 0 — Environment & architecture ✅

- Stack chosen (.NET 10, WPF, WebView2 + React, SQLite, Whisper, Ollama-compatible AI) — see ARCHITECTURE.md.
- Repository structure, coding conventions, CI on real Windows, installer + portable packaging, smoke test.

## Phase 1 — JARVIS core ✅ (v0.1)

| Capability | Status | Notes |
| --- | --- | --- |
| Always-on runtime, starts with Windows, survives UI crashes | Working | `jarvis-core.exe` + watchdog both ways |
| Desktop presence: orb, tray, quick bar (Ctrl+Alt+J), dashboard | Working | WebView2; falls back to browser |
| Agent loop with deterministic EN/AR commands | Working | ~40 command patterns, no AI needed |
| AI conversation with tool calling | Working | Needs a local model (Ollama) or an optional cloud key |
| AI router (no-AI / local / cloud) | Partial | Keyword task classes + capability-aware model choice; no cost/latency model yet |
| Native Ollama: model discovery, capabilities (tools/vision/embedding), context sizing, downloads | Working | Verified in CI against a real Ollama with qwen2.5:1.5b |
| Streaming replies, model fallback chain, context budgeting for small models, conversation restore after restart | Working | |
| Visible turn phases (understanding → analyzing → selecting tool → executing → completed) | Working | No chain-of-thought is exposed |
| Egyptian Arabic replies | Working | Prompted; quality depends on the model (Qwen 2.5 7B+ recommended) |
| Push-to-talk voice (Whisper) | Partial | Model download required; verified end to end in CI with synthesized speech; Arabic accuracy improves with `small` |
| Wake word “Jarvis” | Partial | VAD + Whisper keyword spotting; CPU-heavier than a dedicated model |
| Spoken replies | Working | Windows voices; Arabic voice must be installed in Windows |
| Presence (active app, idle, fullscreen, meeting via mic) | Working | No camera |
| Offline detection + queued internet actions | Working | |

## Interface ✅ (v0.2)

| Capability | Status | Notes |
| --- | --- | --- |
| HUD dashboard: home (orb + clock, system health, priorities, upcoming, recent), context panel | Working | Responsive from phone width to 4K; verified with screenshots at 390 px, 1024, 1366, 1440, 1920, 2560 |
| Orb with 8 states (idle, listening, thinking, speaking, executing, warning, error, offline) | Working | Dashboard (SVG) and desktop (WPF); GPU-friendly; motion can be reduced or turned off |
| Command console (Ctrl+Alt+J on the desktop, Ctrl+K in the dashboard) | Working | Suggestions, recent requests, live progress, approvals, voice |
| Assistant: streaming replies, tool cards, approvals inline, memories used, fallback badge | Working | |
| System monitor: CPU, memory, GPU, network, disks, battery, processes, JARVIS footprint | Working | GPU via Windows performance counters; CPU temperature isn't exposed to normal apps on Windows, so it's shown as unavailable |
| Settings control center (general, voice, AI incl. model downloads, memory, security, notifications, appearance, shortcuts, tools & plugins, privacy, system) | Working | Every control maps to a setting the runtime or shell honours |
| Interface sounds (wake, accepted, processing, completed, warning, error, notification) | Working | Synthesized; per-cue toggles; off entirely with one switch; play from open dashboard/console windows |
| Arabic interface (RTL) | Partial | Navigation, home, assistant, console, status and settings sections are translated; detailed setting descriptions are English |

## Phase 2 — Real computer agent ✅ (mostly)

| Capability | Status |
| --- | --- |
| Open/close/switch apps, Store apps, settings pages, folders, websites (EN/AR names) | Working |
| Windows list/focus/minimize/maximize, processes list/kill | Working |
| Volume, media keys, lock, shutdown/restart/sleep (approval + 30 s grace) | Working |
| Screenshots, clipboard, keyboard typing and shortcuts | Working |
| PowerShell/cmd/bash commands with risk grading | Working |
| Files: search, list, read, write, move/copy, delete to Recycle Bin | Working |
| Permission system (Safe/Sensitive/Critical, per-tool policies, approvals, audit) | Working |
| Project awareness: find projects by name (or from the editor window), open in VS Code, build/test with the project's own build system, extract errors | Working |
| **Next:** UI Automation (click buttons/read controls in other apps), mouse control | Planned |

## Phase 3 — Memory ✅ (core)

| Capability | Status |
| --- | --- |
| Local memory with kinds, sources, confidence, expiry; Arabic-aware search | Working |
| Memory UI: view, search, add, edit, delete, clear, allowed kinds | Working |
| Conversation history with retention | Working |
| Provenance on every memory ("why is this here?"): surface, conversation, your words, or the evidence for an inference | Working |
| Confirmed vs inferred: AI-initiated and learned items stay unconfirmed until you confirm or reject them | Working |
| People, organisations, projects and relationships ("Ahmed works at CityCrep"), entity profiles with related tasks | Working |
| Semantic (meaning-based) recall with a local embedding model (bge-m3), hybrid with keyword search | Working — verified in CI with a real Ollama embedding model |
| **Next:** conversation summarisation into memory, file entities (with the file index) | Planned |

## Phase 4 — Web agent

| Capability | Status |
| --- | --- |
| Web search (key-less) and page reading with untrusted-content handling | Partial |
| Browser automation (Playwright + Edge), forms, downloads, comparison workflows, source ranking | Planned |

## Phase 5 — Executive assistant

| Capability | Status |
| --- | --- |
| Tasks (6 states, priorities, due dates) and reminders | Working |
| Daily briefing / priorities ("what's happening today?", "check my priorities") from real data | Working |
| Notification intelligence (priority, dedupe, meeting/fullscreen holding, digest, quiet hours) | Working |
| Long-running workflows ("track the CityCrep deal"), proactive suggestions, daily briefing | Planned |
| Calendar integration | Planned (Phase 7 with meetings) |

## Phase 6 — Knowledge & communication

File content index (PDF/DOCX/XLSX/PPTX), semantic search, version comparison; Gmail and Outlook via
official APIs (OAuth), unified inbox classification, drafting, approval-based sending. Integrations will be
labelled *supported / partial / official API required / browser automation / not possible*. **Planned.**

## Phase 7 — Vision & meeting intelligence

Screen understanding with a local vision model, meeting preparation briefs, visible recording controls,
notes, decisions and action items. Screenshots already work. **Foundation.**

## Phase 8 — Personal adaptation

| Capability | Status |
| --- | --- |
| Opt-in pattern learning from JARVIS's activity log: app routines, start of day, repeated requests, preferred language, preference for short answers | Working |
| Review screen (Memory → To review) with evidence; confirm or reject; rejected patterns are never re-proposed | Working |
| Learned preferences shape replies only as labelled hints | Working |
| Writing-style learning from your sent messages (needs the inbox connectors) | Planned |

## Phase 9 — Plugins

Plugin API on top of the tool registry with declared permissions, lifecycle, sandboxing for generated
plugins. **Foundation.**

## Phase 10 — Continuous learning

Research → knowledge ingestion with source attribution and conflict detection. **Planned.**

## Phase 11 — Android companion

Device pairing (per-device tokens, explicit permissions), notifications, tasks, commands, conversation
continuation over the existing API/event protocol. **Foundation** (protocol in place).
