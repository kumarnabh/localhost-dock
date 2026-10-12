# Contributing

Thanks for helping! Issues and pull requests are welcome.

## Getting set up

You need Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build
dotnet test
dotnet run --project src/LocalhostDock -- --show   # --show opens the dock immediately
```

To try changes without touching your real registry, point the app at a scratch folder:

```powershell
$env:LOCALHOST_DOCK_HOME = "$env:TEMP\ldock-dev"
dotnet run --project src/LocalhostDock -- --show
```

Only one instance runs at a time, so quit the installed copy first (tray menu → Exit).

## Guidelines

- Keep it dependency-light: the app ships as one small exe on the .NET Desktop Runtime.
- Anything that kills a process must stay behind a confirmation, except Stop on a registered app (see the README's Safety section).
- Never write to a user's launcher scripts. Localhost Dock reuses them; it doesn't manage them.
- Add or update tests in `tests/LocalhostDock.Tests` for changes to `Core/`, `Net/` or `Config/`. Tests that need their own registry folder must use `TestSandbox.Use(dir)`: the test run is sandboxed so it can never touch a real `%APPDATA%\LocalhostDock`.
- Detection improvements are very welcome: add a test with a small folder layout that reproduces the launcher or project shape.
- Update `CHANGELOG.md` under an *Unreleased* heading and the relevant doc in `docs/`.

## Releasing

Push a tag like `v0.2.0`. CI builds, tests, publishes a single-file exe and attaches a zip to a GitHub release.
