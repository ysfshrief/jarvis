# Sample plugins

Each folder is a plugin: `plugin.json` (what it is, its tools, the permissions it asks for, its tests) and
`main.js` (one function per tool). Import one in the dashboard (Plugins → Import), review what it may do,
run its checks, and approve the install.

- `text-tools` — counts words and keeps notes (uses its own storage; no network).
- `currency` — converts currencies with the free Frankfurter API (reads from `api.frankfurter.app` only).

The JavaScript runs in a sandbox with only the `jarvis` object:
`jarvis.http.get/getJson` (declared hosts, https), `jarvis.http.post/postJson` (hosts in `httpSend`),
`jarvis.storage.get/set`, `jarvis.notify`, `jarvis.log`. There is no file, process or other network access.
