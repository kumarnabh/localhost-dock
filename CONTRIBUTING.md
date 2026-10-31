# Contributing

Thanks for helping! Issues and pull requests are welcome.

## Getting set up

You need Windows 10/11, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and Node.js on PATH (the tests use small `node` listeners).

```powershell
dotnet build
dotnet test
dotnet run --project src/LocalhostDock -- --show   # --show opens the dock immediately
dotnet run --project src/ldock -- status            # the read-only CLI
```

Projects:
- `src/LocalhostDock.Core`: the engine. It has no UI and is where most changes and tests belong.
- `src/LocalhostDock`: the tray app.
- `src/ldock`: the CLI host.
- `tests/LocalhostDock.Tests`: the tests.

UI tests must never show windows where you could click them; render off-screen like `UiSmokeTests`.

To try changes without touching your real registry, point the app at a scratch folder:

```powershell
$env:LOCALHOST_DOCK_HOME = "$env:TEMP\ldock-dev"
dotnet run --project src/LocalhostDock -- --show
```

Only one instance runs at a time, so quit the installed copy first (tray menu → Exit).

## Guidelines

- Keep it dependency-light: the app ships as one small exe on the .NET Desktop Runtime.
- **Process safety is non-negotiable.** Never terminate a process except through `ProcessGuard`, and never stop a process that isn't verifiably the app's (owned/matched) without explicit user confirmation. Any change near process control needs a test in `SafetyTests`; a release can't ship while those fail.
- Never write to a user's launcher scripts. Localhost Dock reuses them; it doesn't manage them.
- Add or update tests in `tests/LocalhostDock.Tests` for changes to `Core/`, `Net/` or `Config/`. Tests that need their own registry folder must use `TestSandbox.Use(dir)`: the test run is sandboxed so it can never touch a real `%APPDATA%\LocalhostDock`.
- Detection improvements are very welcome: add a test with a small folder layout that reproduces the launcher or project shape.
- Update `CHANGELOG.md` under an *Unreleased* heading and the relevant doc in `docs/`.

## Releasing

Push a tag like `v0.3.0`. CI builds, runs the dependency vulnerability scan and every test (including the process-safety suite), publishes framework-dependent and self-contained single-file builds, self-tests both, and only then attaches the zips and `SHA256SUMS.txt` to a GitHub release.
