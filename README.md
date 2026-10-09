# JARVIS

A Windows-native personal AI executive assistant: always on, local-first, free-first, bilingual
(English and Egyptian Arabic). JARVIS understands what you're trying to do, acts on your computer
through permission-checked tools, remembers what matters, and tells you what it did.

> **Status: Phase 1–2 foundation (v0.1).** What works today, what is partial and what is planned is
> listed honestly in [docs/ROADMAP.md](docs/ROADMAP.md) and inside the app (System → “What this
> build can do”).

## Download and run

**Latest build:** open the repository's **Releases** page and download from the **“JARVIS latest
build”** pre-release:

| File | What it is |
| --- | --- |
| `JARVIS-Setup-x64.exe` | Installer. Per-user, no admin rights needed. Recommended. |
| `JARVIS-Portable-x64.zip` | Unzip anywhere and run `JARVIS.exe`. Nothing is installed. |

Every push also produces these files as an artifact on the **Actions** tab (run → *Artifacts*).

Requirements: Windows 10 (2004) or Windows 11, x64. No .NET installation is needed — it's bundled.

> Windows SmartScreen may warn because the build is not code-signed yet. Choose *More info → Run anyway*.

## First run (5 minutes)

1. Install and start JARVIS. A glowing **orb** appears at the bottom-right and a **tray icon** next to the clock.
2. Click the orb (or press **Ctrl+Alt+J**) to open the **command console** and type a command — no AI needed for these:
   - `open calculator` · `افتح VS Code` · `close notepad`
   - `remind me in 20 minutes to call Ahmed` · `فكرني بعد ربع ساعة أكلم أحمد`
   - `add task send the CityCrep proposal with high priority` · `what are my tasks`
   - `what's happening today?` · `check my priorities` · `إيه اللي ورايا النهارده؟` (a briefing from your real tasks and reminders)
   - `remember that the CityCrep meeting is on Sunday` · `what do you know about CityCrep`
   - `volume 40` · `mute` · `next song` · `take a screenshot` · `how's the system`
   - `run git status` (read-only commands run; anything else asks you first)
   - `open my project citycrep` · `build citycrep` · `why is the build failing?` (uses the project open in your editor)
   - `track the CityCrep deal` · `where are we with CityCrep?` · `what am I tracking` (workflows with steps, follow-ups and deadlines)
   - `schedule a meeting with Ahmed tomorrow at 3pm` · `what's on my calendar tomorrow` · `prepare me for my CityCrep meeting` ·
     `حط اجتماع مع سارة بكرة الساعة 11` (subscribe to your Google/Outlook calendar in **Settings → Accounts**)
   - `what's on my screen?` (with a local vision model such as `qwen2.5vl`) · JARVIS can also read and press buttons in other apps
   - `make a plugin that converts currencies` — JARVIS writes a sandboxed plugin; you review what it may do and approve
     the install (samples in `samples/plugins/`)
   - `record this meeting` → a REC indicator appears; `stop recording` → decisions and action items (`what did we decide?`)
   - after connecting email in **Settings → Accounts**: `check my email` · `شوف الإيميل` · then in the Inbox, reply or
     *Draft with JARVIS* — nothing is sent until you approve it
   - after turning on **Settings → Files**: `find documents about the renewal fee` · `what's my latest PDF` ·
     `summarize the CityCrep proposal.docx` · `compare Proposal v2.docx with the previous version` · `دور على ملفات عن سيتي كريب`
3. **Enable conversation (free, private):** install [Ollama](https://ollama.com), then download a model from
   **Settings → AI** (one click; `qwen2.5:7b` recommended) — or run `ollama pull qwen2.5:7b`. JARVIS detects
   it, reads what each model can do (tools, vision, embeddings) and streams replies as they're written. Now
   you can ask open questions and multi-step requests such as *“open my project folder and tell me why the
   build is failing”* or *“go to the CityCrep portal and find this quarter's prices”* — JARVIS drives its own
   visible Edge window, and anything that sends, buys, publishes, deletes or submits a form asks you first.
4. **Enable voice (optional):** Settings → Voice → download a speech model (`base`, or `small` for better
   Arabic). Then press **Ctrl+Alt+Space** to talk, or turn on the **“Jarvis” wake word**.
5. Double-click the orb for the **dashboard**: a HUD-style home with live system health, your priorities and
   upcoming reminders, the assistant (with live progress: understanding → analyzing → selecting tool →
   executing → completed), executive inbox, calendar, meetings, tasks, workflows, memory, files, system monitor, activity log and a settings control center
   (appearance, sounds, shortcuts, privacy…). The interface is available in English and Arabic (right-to-left).

## What makes it different

- **Real actions, verified.** Opening an app waits for its window to appear; commands report real exit
  codes; nothing claims success it didn't observe.
- **You stay in control.** Every action is graded Safe / Sensitive / Critical. Sensitive actions ask
  (unless you allow them), critical ones — deleting, power, destructive commands, sending — *always* ask.
  Everything is in the Activity log.
- **Local-first and free-first.** Memory, tasks and history live in a local SQLite database. Speech
  recognition (Whisper) and AI (Ollama) run on your PC. Cloud AI is optional, off by default, and uses
  your own key.
- **Works offline.** Local commands keep working; internet actions are queued and wait for your OK.
- **Knows when not to interrupt.** Notifications are prioritised, deduplicated, and held during meetings
  and fullscreen presentations, then summarised.

## Architecture in one picture

```
 JARVIS.exe (desktop shell)          jarvis-core.exe (always-on runtime, 127.0.0.1 only)
 ┌──────────────────────┐   HTTP +   ┌───────────────────────────────────────────────────┐
 │ orb · tray · hotkeys │ WebSocket  │ Agent loop ─ intents (EN/AR) ─ AI router ─ tools    │
 │ quick bar            │ ─────────▶ │ permissions · approvals · activity log             │
 │ dashboard (WebView2) │            │ memory · tasks · reminders · notifications         │
 └──────────────────────┘            │ voice (Whisper, Windows TTS) · presence · offline  │
                                     │ Windows platform layer (Win32, Core Audio, DPAPI…) │
                                     └───────────────────────────────────────────────────┘
```

Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Build it yourself

```powershell
# Windows, with .NET 10 SDK, Node 22 and (for the installer) Inno Setup 6
./build/package.ps1            # → artifacts/JARVIS-Setup-x64.exe and JARVIS-Portable-x64.zip
./build/smoke-test.ps1         # drives the packaged app end to end
```

Development setup, tests and conventions: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
Releases: [docs/RELEASING.md](docs/RELEASING.md). Security model: [docs/SECURITY.md](docs/SECURITY.md).

## Your data

Everything lives in `%LOCALAPPDATA%\JARVIS` — database, settings, encrypted secrets (Windows DPAPI),
logs and downloaded speech models. Uninstalling JARVIS keeps this folder so reinstalling doesn't lose your
memory; delete it to wipe everything.
