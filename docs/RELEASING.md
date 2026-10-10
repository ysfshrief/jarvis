# Releasing

## Automatic builds

Every push runs `.github/workflows/build.yml`:

1. Linux job: core + runtime tests (with a real mail server and a real headless Chrome).
2. Live-AI job: real Ollama models — tool calling through the agent, an Arabic turn, a vision question about a
   picture (`qwen2.5vl`), and Arabic semantic recall with `bge-m3`.
3. Android job: companion unit tests (including certificate pinning against a real local HTTPS server) and the
   debug APK.
4. Windows job: build the dashboard (with the Arabic translation check), build the solution, run all tests
   (including real-Windows integration tests), package (`build/package.ps1`), and **smoke-test the packaged app**
   (`build/smoke-test.ps1`: starts it, checks auth, drives bilingual commands and checks the actual replies,
   opens/closes Notepad, takes screenshots, shuts down).
5. Artifacts: `JARVIS-Setup-x64.exe`, `JARVIS-Portable-x64.zip`, `JARVIS-companion-android` (the APK), plus
   smoke-test evidence (screenshots, logs).
6. Release job — only if **all four** jobs above passed, on a push to the **default branch**: replaces the
   **`latest-build`** pre-release with `JARVIS-Setup-x64.exe`, `JARVIS-Portable-x64.zip` and
   `JARVIS-Companion-Android-debug.apk`.

Version: `Directory.Build.props` `<Version>` + `.{run number}` for CI builds.

## Milestone releases

A push to the default branch whose commit message contains **`[release]`** is built with the exact version in
`Directory.Build.props` (no run number) and, after every test and the smoke test pass, published as the GitHub
Release `v<version>` with all three files. An existing release is never replaced, so bump `<Version>` (and
`installer/jarvis.iss`, `ui/package.json`) first.

## Tagged releases

```bash
# bump <Version> in Directory.Build.props if needed, commit, then:
git tag v0.3.6
git push origin v0.3.6
```

The workflow builds, tests, smoke-tests and creates the GitHub Release `v0.3.6` with all three files and
generated notes.

The APK is debug-signed (no release keystore is configured), so phones install it as a sideloaded app.

## Local packaging

```powershell
./build/package.ps1 -Version 0.3.6      # artifacts/JARVIS-Setup-x64.exe, artifacts/JARVIS-Portable-x64.zip
./build/smoke-test.ps1
```

## Checklist before tagging

- [ ] CI green on the commit (including the smoke test).
- [ ] ROADMAP.md and the capability matrix (`src/Jarvis.Runtime/Capabilities.cs`) reflect reality.
- [ ] Install over the previous version on a real PC: settings and memory survive; orb, quick bar,
      dashboard, a reminder, and one voice command work.
