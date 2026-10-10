# JARVIS implementation audit — v0.3.0 (2026-10-09)

An honest check of the build against the original vision. A capability counts as **verified** only when a test
or a real run exercises the real thing (real model, real server, real browser, real Windows). Code that exists
but has only been exercised with doubles is **unverified**. Evidence comes from:

- **Local runs (Linux):**
  - core tests: 431 passed, 6 skipped (real-Ollama tests, which run in CI);
  - runtime tests: 18 passed;
  - live mail tests against a real GreenMail IMAP/SMTP server;
  - every dashboard page captured at 1440/1024/390 px in English and Arabic, checked for overflow and JS errors.
- **CI run 32 (green):**
  - Linux tests with GreenMail and real Chromium;
  - real Ollama (qwen2.5:1.5b, all-minilm);
  - Windows tests with Whisper, UI Automation and OCR, then packaging and a smoke test of the installed app;
  - the Android build and unit tests.
- **Code review:** two independent read-throughs of every capability and every setting.

## Re-verification after v0.3.1 (CI runs 37–38)

Gaps from the table below that have since been closed, each by a test that passed in CI:

- **Vision:** the screen tool sent a picture to a real `qwen2.5vl` (configured as plain `qwen2.5vl`, resolved to
  `:latest`) and the answer named the red square (run 38). An empty answer from a vision model is now a reported
  failure, not a blank “description” — run 37 caught `moondream` returning nothing.
- **Arabic semantic recall:** a real `bge-m3` recalled an Arabic memory from Arabic and English queries (runs 37–38).
- **Real model in Arabic:** a real model answered an Egyptian-Arabic request in Arabic (runs 34, 37, 38).
- **Android pinning:** JVM tests against a real local HTTPS server — the paired server works, a server with another
  certificate is refused before the device token is sent, a wrong token is reported, plain http is never used (run 38).
- **Smoke test:** checks the actual reply text of each command (time in English and Arabic, system, tasks, memory,
  screenshot, `git --version`), not just success (runs 37–38).
- **Arabic interface:** the build fails if any interface string lacks an Arabic translation (runs 37–38).
- **Model tags:** two tags of one Ollama model (e.g. `qwen2.5vl` and `qwen2.5vl:3b`) were listed under one name, so the
  untagged name never resolved; fixed with a unit test and exercised live in run 38.

- **A real model's tool call through the agent:** a real `qwen2.5:3b` (the lightest model JARVIS offers) chose
  `task_create` for a request no deterministic command matches, and JARVIS executed it (run 39). Models below 3B are
  not reliable at this: `qwen2.5:1.5b` asked questions instead of creating the task in runs 37–38.

## Local AI on a PC without a GPU (reported on v0.3.2, fixed in v0.3.3)

Every AI message on the user's PC failed with "timed out" after 120 s. Causes and the measured effect of the fixes
(CI, CPU-only runner, `qwen2.5:3b`, runs 42–43):

- The system prompt changed every message (time, active window, memories), so Ollama re-read the whole prompt and
  tool list (~1,900 tokens) each time. Now it stays identical and the changing part travels with the message: the
  next message started answering after **4.7 s instead of 63.5 s**.
- The first message after starting still had to read it all. JARVIS now loads the model and has it read the fixed
  part at startup (Settings → AI, on by default): a first message started after **3.2 s instead of 62.8 s**.
- Loading a model, and reading a long prompt, no longer count against the silence limit; a timed-out local model no
  longer falls back to a second local model (which evicted the first and doubled the wait).
- "set a timer for 3 seconds" and "what's my name" are direct commands now (verified in the packaged app, run 43).

A 7B model on a slow CPU is still roughly 2–3× slower than these figures; `qwen2.5:3b` is the faster choice there.

## Verdict by area

| Area | Status | Evidence | Gaps found |
| --- | --- | --- | --- |
| Agent loop, permissions, approvals, audit, offline queue | Verified | ~400 unit/agent tests; runtime API tests | — |
| AI conversation with a real local model | Partly verified | CI: real streaming, a real tool call at provider level, a plain English agent turn | No test where a real model's tool call is executed through the agent; no Arabic turn with a real model |
| Voice | Partly verified | Windows CI: Whisper transcribes synthesized speech; wake-word match on that transcript | Live microphone loop, follow-up, push-to-talk and TTS playback are never exercised; follow-up mode accepts any speech for a few seconds without the wake word; wake word stays live during meeting recording; Arabic speech untested. **Real microphone needs your device.** |
| Memory and recall | Verified (English) | Hybrid recall in the agent prompt; real-embedding recall test in CI | CI uses all-minilm (English) while the default is bge-m3; Arabic semantic recall and file meaning-search embeddings untested |
| Browser automation | Verified for driving, unverified for AI-driven browsing | Real Chromium over CDP against a local site | Tests silently pass if no browser is found; multi-step browsing only tested with a scripted model; tests are headless |
| Email (IMAP/SMTP) | Verified | Real GreenMail: sync, sort, link, draft, approval-only send, Sent-folder style learning | TLS ports and real providers untested; **Inbox “Ask JARVIS” put the attacker-controlled subject into a trusted prompt** (fixed in v0.3.1) |
| WhatsApp / Telegram / social | Not built | Honest “official API required” table | Telegram's free Bot API isn't offered |
| Calendar, meetings | Verified (ICS, local events, notes); recording partial | ICS over HTTP; Whisper meeting transcript on Windows CI | Live capture needs real audio devices |
| Workflows | Verified | Real store tests, monitor, smoke-test reply | Templates are fixed; “waited N days” resets on any edit |
| Vision | **Unverified for its main path** | OCR is real on Windows CI | No test sends an image to a vision model; camera availability is cached forever |
| Files and documents | Verified | Extraction, index, OCR of images and scanned PDFs on Windows CI | — |
| Plugins | Verified, with one security hole | Real Jint sandbox, escape tests, update diffs | **Redirects bypassed the host allow-list; response size checked after download** (fixed in v0.3.1) |
| Phone companion server | Verified | End-to-end pinned TLS pairing test | — |
| Android app | Unverified beyond the build | CI builds the APK; 3 link-parsing tests | Pinning, Keystore, polling and notifications have no tests. **Needs a physical phone.** |
| Desktop shell (orb, tray, hotkeys) | Partly verified | Smoke test: the installed app starts, the dashboard and console connect | Orb rendering and states, tray and push-to-talk untested; several smoke checks don't look at the reply; push-to-talk could crash the shell |

## Cinematic UI

| Item | Status | Evidence / gap |
| --- | --- | --- |
| Orb (8 states) | Working | All 8 states reachable from real events (web and WPF); “speaking” only with TTS on Windows. Error flash 4 s on the web vs 3 s on the desktop |
| Animations, reduced motion | Partial | transform/opacity only; *reduced* motion stopped only ambient layers (arcs, waveform and pulses kept moving) |
| Holographic HUD layout | Working | All pages render at 1440/1024/390 px without horizontal overflow or JS errors |
| Phone layout | Partial | Top status chips are clipped at 390 px; the Activity table wasn't wrapped for small screens |
| Arabic / RTL | **Partial** | RTL layout is solid (logical CSS, `dir=auto`), but only about 15–20% of interface text was translated; most pages in Arabic mode were mostly English with flipped punctuation; calendar arrows don't flip |
| Backend language | Bug | “Auto” language never gave Arabic for dashboard actions, reminders and alerts even with an Arabic interface |
| Command console | Basic | No history recall or keyboard navigation; suggestions are a fixed list, not real commands |
| System monitor | Working with honest gaps | CPU, memory, network, disks, processes, battery real; GPU missing on Linux and intermittently on Windows; CPU temperature not available on Windows |
| Settings | Working, two problems | Every setting is used by something, but saving sent the whole object and could overwrite changes made elsewhere (e.g. a downloaded speech model); meeting options, file read limit and microphone device have no controls |

## Hygiene

- Twelve Python wheel files (pip downloads) had been committed to the repository root; removed.
- Docs contradicted themselves on writing-style learning (“planned” in one row, “working” in another).

## Priorities (in the order they are being done)

1. **v0.3.1 — security and correctness:**
   - plugin redirect hole;
   - untrusted email text in prompts;
   - voice safety (no wake word during recordings; stricter follow-up; push-to-talk cleanup);
   - language “auto”;
   - settings saving only what changed;
   - desktop push-to-talk crash;
   - browser tests that can't pass vacuously in CI;
   - doc contradictions.
2. **v0.4 — cinematic UI completion:**
   - full Arabic interface;
   - command console with history and real suggestions;
   - complete reduced-motion;
   - phone header;
   - calendar arrows;
   - consistent orb timings.
3. **Real-model verification in CI:**
   - an agent turn where a real model's tool call is executed;
   - an Arabic turn;
   - a real vision model describing a screenshot;
   - the voice state machine driven by real Whisper.
4. Remaining items in ROADMAP.md.

## Needs you

- **Physical devices:**
  - a real microphone and speakers (voice loop, meeting capture);
  - a camera;
  - an Android phone (pairing, notifications, approvals).
- **Credentials:**
  - Microsoft or Google app registration for Outlook/Google sign-in and two-way calendar;
  - optionally a Telegram bot token, if you want Telegram.
- **Code signing certificate** (SmartScreen warning) — optional and paid.
