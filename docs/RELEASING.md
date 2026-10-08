# Releasing

## Automatic builds

Every push runs `.github/workflows/build.yml`:

1. Linux job: core + runtime tests.
2. Windows job: build the dashboard, build the solution, run all tests (including real-Windows
   integration tests), package (`build/package.ps1`), and **smoke-test the packaged app** (`build/smoke-test.ps1`:
   starts it, checks auth, drives bilingual commands, opens/closes Notepad, takes screenshots, shuts down).
3. Artifacts: `JARVIS-Setup-x64.exe`, `JARVIS-Portable-x64.zip`, plus smoke-test evidence (screenshots, logs).
4. Pushes to the **default branch** replace the **`latest-build`** pre-release on the Releases page.

Version: `Directory.Build.props` `<Version>` + `.{run number}` for CI builds.

## Tagged releases

```bash
# bump <Version> in Directory.Build.props if needed, commit, then:
git tag v0.2.0
git push origin v0.2.0
```

The workflow builds, tests, smoke-tests and creates the GitHub Release `v0.2.0` with both files and
generated notes.

## Local packaging

```powershell
./build/package.ps1 -Version 0.2.0      # artifacts/JARVIS-Setup-x64.exe, artifacts/JARVIS-Portable-x64.zip
./build/smoke-test.ps1
```

## Checklist before tagging

- [ ] CI green on the commit (including the smoke test).
- [ ] ROADMAP.md and the capability matrix (`src/Jarvis.Runtime/Capabilities.cs`) reflect reality.
- [ ] Install over the previous version on a real PC: settings and memory survive; orb, quick bar,
      dashboard, a reminder, and one voice command work.
