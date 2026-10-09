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
| Camera | Not used. |

## Known limitations (v0.x)

- Builds are not code-signed yet (SmartScreen warning).
- Tools run in the runtime process with the user's rights; there is no OS-level sandbox yet.
- Browser button grading reads labels and form methods; a page can disguise a consequential action as a
  harmless-looking button driven by script (graded "sensitive", which asks unless you allow sensitive actions).
- Command risk grading is pattern-based; unusual destructive commands may be graded "sensitive"
  (still requires approval by default) rather than "critical".
- Any process running as your Windows user can read `runtime.json` and call the API (same trust boundary
  as the user account).

## Reporting

Open a private security advisory on the repository.
