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
| AI conversation with tool calling | Working | Needs a local model (Ollama) or an optional cloud key. Verified in CI: a real qwen2.5:3b picks the right tool for a request no direct command matches and JARVIS executes it. Use 3B parameters or more: qwen2.5:1.5b often asked a question instead of acting |
| AI router (no-AI / local / cloud) | Partial | Keyword task classes + capability-aware model choice; no cost/latency model yet |
| Native Ollama: model discovery, capabilities (tools/vision/embedding), context sizing, downloads | Working | Verified in CI against a real Ollama (qwen2.5 chat, qwen2.5vl vision, all-minilm and bge-m3 embeddings) |
| Streaming replies, model fallback chain, context budgeting for small models, conversation restore after restart | Working | |
| Visible turn phases (understanding → analyzing → selecting tool → executing → completed) | Working | No chain-of-thought is exposed |
| Egyptian Arabic replies | Working | Prompted; quality depends on the model (Qwen 2.5 7B+ recommended) |
| Push-to-talk voice (Whisper) | Partial | Model download required; verified end to end in CI with synthesized speech; Arabic accuracy improves with `small` |
| Wake word “Jarvis” | Partial | VAD + Whisper keyword spotting; CPU-heavier than a dedicated model |
| Spoken replies | Working | Windows voices; Arabic voice must be installed in Windows |
| Presence (active app, idle, fullscreen, meeting via mic) | Working | Never uses the camera |
| Offline detection + queued internet actions | Working | |

## Interface ✅ (v0.2)

| Capability | Status | Notes |
| --- | --- | --- |
| HUD dashboard: home (orb + clock, system health, priorities, upcoming, recent), context panel | Working | Responsive from phone width to 4K; verified with screenshots at 390 px, 1024, 1366, 1440, 1920, 2560 |
| Orb with 8 states (idle, listening, thinking, speaking, executing, warning, error, offline) | Working | Dashboard (SVG) and desktop (WPF); GPU-friendly; reduced motion stops every animation (states stay distinct by colour and shape) and follows the OS / Windows animation preference; motion can be turned off |
| Command console (Ctrl+Alt+J on the desktop, Ctrl+K in the dashboard) | Working | ↑/↓ history of your requests, live completions from your history and real built-in commands (EN/AR), Tab to complete, live progress, approvals, voice |
| Assistant: streaming replies, tool cards, approvals inline, memories used, fallback badge | Working | |
| System monitor: CPU, memory, GPU, network, disks, battery, processes, JARVIS footprint | Working | GPU via Windows performance counters; CPU temperature isn't exposed to normal apps on Windows, so it's shown as unavailable |
| Settings control center (general, voice, AI incl. model downloads, memory, security, notifications, appearance, shortcuts, tools & plugins, privacy, system) | Working | Every control maps to a setting the runtime or shell honours |
| Interface sounds (wake, accepted, processing, completed, warning, error, notification) | Working | Synthesized; per-cue toggles; off entirely with one switch; play from open dashboard/console windows |
| Arabic interface (RTL) | Working | Every page, setting, hint, status and the phone companion in Egyptian Arabic (1,000+ strings); dates and relative times in Arabic; a build check fails if any interface string lacks Arabic. Data from the server (email, memories, process names, capability notes) stays as written |

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
| UI Automation: read numbered controls and text of any app window, press buttons/menus/tabs/checkboxes, put text in fields with read-back verification | Working — tested on Windows CI against Notepad |
| Risk per control (send/delete/buy/publish labels and password fields are critical); controls re-read before acting | Working |
| Mouse click (identifies the control under the point first) and scroll | Working |

## Phase 3 — Memory ✅ (core)

| Capability | Status |
| --- | --- |
| Local memory with kinds, sources, confidence, expiry; Arabic-aware search | Working |
| Memory UI: view, search, add, edit, delete, clear, allowed kinds | Working |
| Conversation history with retention | Working |
| Provenance on every memory ("why is this here?"): surface, conversation, your words, or the evidence for an inference | Working |
| Confirmed vs inferred: AI-initiated and learned items stay unconfirmed until you confirm or reject them | Working |
| People, organisations, projects and relationships ("Ahmed works at CityCrep"), entity profiles with related tasks | Working |
| Semantic (meaning-based) recall with a local embedding model (bge-m3), hybrid with keyword search | Working — verified in CI with real embedding models: all-minilm (English) and bge-m3 (Arabic and English queries recall an Arabic memory) |
| Documents linked to people and organisations (from the file index) | Working |
| Conversation digest (opt-in): when a conversation goes quiet, the model proposes lasting facts the user stated; each must quote the user's own words (verified against the transcript) and is saved unconfirmed for review | Working |

## Phase 4 — Web agent

| Capability | Status |
| --- | --- |
| Web search (key-less) and page reading with untrusted-content handling | Partial — the key-less search can be rate-limited |
| Browser agent: JARVIS's own visible Edge/Chrome window (separate profile) driven over the DevTools protocol — open, read text and numbered elements, click, type, press Enter, back, screenshot | Working — tested against a real browser in CI |
| Per-element risk grading (EN/AR): buttons that send/buy/publish/delete/book/subscribe/change accounts and POST forms are critical; password/payment fields critical; links are only navigation (a “Delete account” link is sensitive — the button on the next page is critical); re-check before acting | Working |
| Tainted requests: after reading untrusted content, sensitive actions in that request always need approval | Working |
| File downloads, multi-tab research, source ranking | Planned |

## Phase 5 — Executive assistant

| Capability | Status |
| --- | --- |
| Tasks (6 states, priorities, due dates) and reminders | Working |
| Daily briefing / priorities ("what's happening today?", "check my priorities") from real data | Working |
| Notification intelligence (priority, dedupe, meeting/fullscreen holding, digest, quiet hours) | Working |
| Workflows: "track the CityCrep deal" — templates (deal, project, hiring, follow-up, custom), ordered steps with dependencies, deadlines, waiting-for with follow-up nudges, approval-gated business steps, recurring cycles, history, links to people/organisations | Working |
| Recurring tasks ("water the plants every monday", "كل جمعة") | Working |
| Proactive suggestions beyond follow-ups and deadlines | Planned |
| Calendar: see Phase 7 | Working |

## Phase 6 — Knowledge & communication

| Capability | Status |
| --- | --- |
| Opt-in local file index (Settings → Files): PDF (PdfPig), DOCX/XLSX/PPTX and OpenDocument (safe XML parsing, no macros run), RTF, text and code, ZIP listings, metadata (title, author, pages) | Working |
| Image text via Windows OCR (Windows.Media.Ocr, offline) | Working on Windows — covered by the Windows CI test |
| Scanned PDFs (no text layer): pages rendered with Windows' PDF renderer and read with Windows OCR (first 20 pages), with a note saying so | Working on Windows — verified in Windows CI with a generated scan; elsewhere indexed by name and metadata only |
| Live updates from file-system changes; gentle background scan; protected folders never read | Working |
| Search by words (Arabic-normalised full text) and by meaning (local embeddings over 1,200-character passages) | Working |
| "Latest PDF/spreadsheet/file in Downloads", "find documents about X", "summarize X", "compare X with the previous version" (EN/AR) | Working |
| Key points (extracted sentences, labelled as such); AI summaries written from the document's real text when a model is available | Working |
| Version detection by name stem (v2, final, dates, "- Copy") and line-level comparison | Working |
| Files dashboard: index status, search with snippets, latest files, detail with key points, linked people/organisations, compare with earlier version | Working |
| Executive inbox over IMAP/SMTP (Gmail app password, Yahoo, iCloud, Zoho, company servers); read-only sync; password only in the encrypted secret store | Working — verified in CI against a real mail server (GreenMail) |
| Sorting into urgent / needs reply / important / FYI / noise with the reason shown (EN/AR rules, VIP senders, known people/organisations); your corrections teach it per sender | Working |
| Mail linked to people and organisations; urgent-mail alerts through the notification centre; inbox in the daily briefing; "check my email" works offline from the last sync | Working |
| Drafts (yours or JARVIS's) — never sent automatically; sending is critical and always shows you the message first; replies thread correctly | Working |
| AI-drafted replies (reads the email, saves a draft) | Partial — needs a local model; your confirmed writing style reaches the model as a preference; verified only with a test model |
| Outlook.com / Microsoft 365 and Google sign-in (OAuth) | Planned — Microsoft no longer allows password sign-in for Outlook mail |
| WhatsApp / Instagram / Messenger (business APIs only), X (paid API), LinkedIn (no API) | Shown honestly in Settings → Accounts; not connected |

## Phase 7 — Vision & meeting intelligence

| Capability | Status |
| --- | --- |
| Calendar subscriptions through the private iCal (ICS) address of Google Calendar, Outlook or iCloud — read-only, recurrences expanded, address encrypted | Working — tested against a real HTTP-served feed |
| JARVIS's own calendar: "schedule a meeting with Ahmed tomorrow at 3pm for 30 minutes", "حط اجتماع مع سارة بكرة الساعة 11" (EN/AR day, date, time and duration phrases) | Working |
| "What's on my calendar tomorrow", "my next meeting", "عندي اجتماعات النهارده؟"; meetings in the daily briefing; reminders N minutes before | Working |
| Meeting prep from real data: attendees (matched to known people/organisations), relationships and facts, tracked deals, open tasks, recent email, documents | Working |
| Calendar page (week view, event detail with prep, add/remove local events) | Working |
| Two-way sync / sending invites (Google, Microsoft, CalDAV) | Planned |
| Meeting recording: user-started (always asks), always visible (REC chip, red orb dot, status API), auto-stop after a limit; microphone + speakers (WASAPI loopback) on Windows; audio never stored | Working on Windows — capture needs real audio devices (CI has none) |
| Local transcript in chunks with Whisper; decisions, action items with owner and due date, open questions (EN/AR); action items → tasks on your choice | Working — transcription verified on Windows CI with synthesized speech |
| AI-written meeting summary | Working when a model is available (grounded on the transcript and extracted notes) |
| "What's on my screen?" with a local vision model; OCR fallback with an honest note; blocked when screen capture is off | Partial — OCR fallback and privacy gate verified on Windows; the vision-model step is verified in CI with a real qwen2.5vl answering about a test picture (configured without a tag, resolved to :latest); an empty model answer is reported as a failure. Not yet run end to end against a real Windows screen with a model |
| Camera: single photo on request for a vision question — off by default, asks every time, never saved, never video | Partial — needs a camera and a vision model; no camera in CI |

## Phase 8 — Personal adaptation

| Capability | Status |
| --- | --- |
| Opt-in pattern learning from JARVIS's activity log: app routines, start of day, repeated requests, preferred language, preference for short answers | Working |
| Review screen (Memory → To review) with evidence; confirm or reject; rejected patterns are never re-proposed | Working |
| Learned preferences shape replies only as labelled hints | Working |
| Writing-style learning (opt-in): from your Sent folder (read-only) and drafts you approved; quoted replies removed; proposes greeting, sign-off, length, sentence length, language and tone markers with the evidence; samples deleted when turned off | Working — verified against a real IMAP server (GreenMail) |

## Phase 9 — Plugins

| Capability | Status |
| --- | --- |
| Plugin format: `plugin.json` (id, tools with parameters and declared risk, permissions, tests) + `main.js` | Working |
| Sandbox (Jint): no CLR, files, processes or network of its own; capability API `jarvis.http/storage/notify/log`; HTTP only to declared hosts, https only, never local addresses; POST only to `httpSend` hosts; limits on time, statements, memory, recursion, requests and storage | Working — escape attempts are part of the test suite |
| Lifecycle: draft (written by JARVIS from a description, or imported) → validate → sandbox tests with the network off → review → explicit, critical approval → install; network tests only on your click | Working |
| Risk never lower than declared; anything that can send data is at least sensitive; plugin tools can never shadow built-in tools; every call goes through the normal permission/approval/audit path | Working |
| Tamper protection: approved code is fingerprinted and held in memory; changed files stop the plugin loading | Working |
| Plugins page: create, import, checks, permissions, code, install, disable, remove | Working |
| Plugin updates: a newer version (written by JARVIS or imported) waits beside the installed one, gets the same checks, shows a plain-language diff (new hosts, sending, storage, notifications, tools, risk changes) and replaces it only with critical approval; storage is kept; edits after the check block it | Working |
| Plugin checks run in throwaway storage, so they never touch a plugin's real data | Working |

## Phase 10 — Continuous learning

| Capability | Status |
| --- | --- |
| Research a topic (“research CityCrep's competitors”, «اتعلم عن …»): web search → read a few sources → the model extracts self-contained facts that cite their source | Working — needs an AI model; web search uses DuckDuckGo's free endpoint and fails honestly when it's rate-limited |
| Learn from one page or document (“learn from https://…”, a PDF/Word/text path) through the normal web/file tools (private addresses refused, files outside your folders ask first) | Working |
| Source attribution: each fact is an unconfirmed note with the address/path it came from; the AI sees it labelled “unverified, from …” | Working |
| Conflict detection: new facts are compared with related notes; contradictions are tagged and explained in Memory → To review; what you told JARVIS is never overwritten | Working — judged by the model, so it can miss subtle conflicts |
| Source text is untrusted: passed as data, instruction-like “facts” are dropped, and anything sensitive later in the same request needs your confirmation | Working |
| Following topics (“keep me updated on …”, «تابعلي …»): re-researched every N days while online; a notification only when there are genuinely new facts or contradictions | Working |

## Phase 11 — Phone companion

| Capability | Status |
| --- | --- |
| Opt-in companion server (off by default) on its own LAN port with JARVIS's self-generated certificate; the dashboard API stays this-PC-only | Working |
| Pairing: one-time 8-character code (5 minutes, single use, cancelled after ten wrong guesses), QR code with the certificate fingerprint; per-phone 256-bit tokens stored only as hashes; remove a phone any time | Working — verified end to end over pinned TLS in the runtime tests |
| Narrow, rate-limited phone API: status, briefing, chat, approvals (audited, can be switched off), alerts | Working |
| Phone requests: anything sensitive or critical always waits for confirmation, even with auto-approve on | Working |
| Mobile companion page (Today / Approvals / Ask / Alerts) for any phone browser | Working |
| Android app: QR pairing with certificate pinning, the companion page inside the app, token encrypted with the Android Keystore and never backed up, notifications with Approve/Refuse (screen unlock required on Android 12+), opt-in “Stay connected” | Partial — built and unit-tested in CI (debug APK, attached to releases): certificate pinning is tested against a real local HTTPS server, including refusing a server with another certificate before the token is sent. Not yet tried on a physical phone |
| Reaching JARVIS away from home (relay or VPN) | Not built — use your own VPN (e.g. WireGuard/Tailscale) to the PC if you need it |
| Push notifications without polling | Not built — would need Google's Firebase (a cloud service); polling keeps everything local |
