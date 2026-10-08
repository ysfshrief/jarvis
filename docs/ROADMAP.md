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
| AI router (no-AI / local / cloud) | Partial | Keyword task classes; no cost/latency model yet |
| Egyptian Arabic replies | Working | Prompted; quality depends on the model (Qwen 2.5 7B+ recommended) |
| Push-to-talk voice (Whisper) | Partial | Model download required; verified end to end in CI with synthesized speech; Arabic accuracy improves with `small` |
| Wake word “Jarvis” | Partial | VAD + Whisper keyword spotting; CPU-heavier than a dedicated model |
| Spoken replies | Working | Windows voices; Arabic voice must be installed in Windows |
| Presence (active app, idle, fullscreen, meeting via mic) | Working | No camera |
| Offline detection + queued internet actions | Working | |

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
| **Next:** entities & relationships (people ↔ projects ↔ files), semantic (embedding) search, conversation summarisation into memory | Planned |

## Phase 4 — Web agent

| Capability | Status |
| --- | --- |
| Web search (key-less) and page reading with untrusted-content handling | Partial |
| Browser automation (Playwright + Edge), forms, downloads, comparison workflows, source ranking | Planned |

## Phase 5 — Executive assistant

| Capability | Status |
| --- | --- |
| Tasks (6 states, priorities, due dates) and reminders | Working |
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

Memory already separates confirmed preferences from learned patterns and derived assumptions; a "What
JARVIS learned" review screen and opt-in pattern learning come next. **Foundation.**

## Phase 9 — Plugins

Plugin API on top of the tool registry with declared permissions, lifecycle, sandboxing for generated
plugins. **Foundation.**

## Phase 10 — Continuous learning

Research → knowledge ingestion with source attribution and conflict detection. **Planned.**

## Phase 11 — Android companion

Device pairing (per-device tokens, explicit permissions), notifications, tasks, commands, conversation
continuation over the existing API/event protocol. **Foundation** (protocol in place).
