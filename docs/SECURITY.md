# Security model

Baseline security for v0.x — practical now, designed to be tightened later (sandboxed plugins, signed
builds, stronger isolation).

## What is protected, and how

| Threat | Mitigation |
| --- | --- |
| Other machines reaching JARVIS | API listens on `127.0.0.1` only. |
| Malicious web pages calling the local API (CSRF, DNS rebinding) | Every `/api` and `/ws` call needs the per-start random token; Host header must be a loopback name. |
| Someone at your unlocked PC opening the dashboard | Optional PIN (PBKDF2-SHA256, 210k iterations); API returns 423 while locked; auto-lock after inactivity. |
| API keys leaking | Stored only in `secrets.dat`, encrypted with Windows DPAPI (current user). Write-only through the API; never in settings, logs or the UI. |
| The AI doing something harmful | Every action goes through `ToolExecutor`: per-call risk grading, permission policy, explicit approval for sensitive actions, **always** for critical ones, timeouts default to "no", full audit log. Blocked tools are hidden from the model. |
| Prompt injection from web pages, files or emails | Tool output is passed to the model as data; the system prompt instructs the model to ignore embedded instructions; no tool output can change settings or permissions; web reading refuses local/private addresses (incl. redirects). **Tainted requests:** once a request has read untrusted content (a web page, later emails), every sensitive or critical action in that request needs your explicit approval even if you normally allow sensitive actions, and the approval says why. A model-composed address with data in it, opened after reading a page, also needs approval (exfiltration through URLs). |
| Browser automation doing something you didn't intend | JARVIS's browser is a separate, visible Edge/Chrome profile. Pressing anything that sends, buys, publishes, deletes, books, subscribes or changes an account — in English or Arabic — and submitting any POST form is **critical** (always asks), whatever the button says; typing into password or payment fields is critical; JARVIS has no access to your passwords. Before acting, the element is re-read from the page: if it became riskier than what you approved, nothing happens. Page reading runs in an isolated script world the page can't tamper with. Links to local/private addresses are not followed. |
| Destructive shell commands | Pattern-based grading marks deletion, formatting, registry edits, force-push, pipe-to-shell, etc. as critical; only a short allow-list of read-only commands runs without approval. |
| File damage | Writes outside allowed folders are critical; system folders, credential files and JARVIS's own data are protected; deletes go to the Recycle Bin. |
| Email | Mailboxes are opened read-only; passwords/app passwords only in the encrypted secret store (never in settings, logs or API responses); an account is saved only after its credentials work. Nothing is ever sent automatically: sending is critical, always asks, and the approval shows the message. Email content is untrusted (see tainted requests above). |
| Microphone privacy | Mic is open only for push-to-talk or when the wake word is enabled (off by default); state is shown on the orb and in the dashboard; audio is never stored. |
| Meeting recording | Never automatic: starting is a critical action that asks every time and reminds you that everyone should know. Always visible while running (REC chip with Stop on every dashboard page, red dot on the orb, status API), stops by itself after the configured limit. Audio is transcribed locally and never written to disk; transcripts can be deleted. |
| Camera | Off by default (Settings → Privacy). When allowed, `camera_look` is critical — it asks every time — takes a single still photo, keeps it in memory for the vision model only, and never records video; the dashboard flashes "Camera used" and Windows' camera light comes on. Presence detection never uses the camera. |
| Learning from the web and documents | Source text is untrusted data: it reaches the model only as quoted sources, instruction-like “facts” are discarded, learned facts are stored unconfirmed with their source and shown to the AI as “unverified”, and reading it marks the request so that anything sensitive afterwards needs your confirmation. Nothing learned can change permissions or settings. |
| Writing-style learning | Off by default; reads the Sent folder read-only; keeps at most 200 samples locally and deletes them when turned off. |
| Phone companion | Off by default. A separate server (the dashboard API never leaves `127.0.0.1`) with JARVIS's own certificate, which phones pin by SHA-256 fingerprint shown on the PC. Pairing needs a one-time code from the PC (5 minutes, single use; ten wrong guesses cancel it); each phone gets its own random token, stored only as a hash, revocable in Settings → Devices. Only status, briefing, chat, approvals and alerts are exposed, rate limited and audited. Anything a phone asks for that changes something always needs confirmation, and approving from phones can be switched off. On Android the token is encrypted with a Keystore key and excluded from backups, and notification Approve/Refuse buttons require unlocking the phone (Android 12+). |
| Plugins (including ones JARVIS writes) | Run in a Jint sandbox with no access to .NET, files, processes or JARVIS's data; the only abilities are those declared and approved (read from listed https hosts, send to listed hosts, own storage, notifications). Before approval, checks run with the network off; installing is critical and shows the permissions; approved code is fingerprinted and kept in memory, and changed files stop the plugin from loading. Updates keep the old version running until you approve them, with a diff of what they may newly do; checks use throwaway storage. Plugin tools can't replace built-in tools and go through the same permission checks and audit log. |
| Operating other apps (UI Automation, mouse) | Pressing a control labelled send/delete/buy/publish/subscribe/… (EN/AR) and typing into password fields are critical; other presses and typing are sensitive. The control is re-read before acting, and the result is verified by reading the field back. |

## Known limitations (v0.x)

- Builds are not code-signed yet (SmartScreen warning).
- Tools run in the runtime process with the user's rights; there is no OS-level sandbox yet.
- Browser button grading reads labels and form methods; a page can disguise a consequential action as a
  harmless-looking button driven by script (graded "sensitive", which asks unless you allow sensitive actions).
- Command risk grading is pattern-based; unusual destructive commands may be graded "sensitive"
  (still requires approval by default) rather than "critical".
- Any process running as your Windows user can read `runtime.json` and call the API (same trust boundary
  as the user account).
- Anyone on your Wi-Fi can reach the companion port while it's on (they still need a pairing code or a
  device token). The Android APK built by CI is debug-signed.

## Reporting

Open a private security advisory on the repository.
