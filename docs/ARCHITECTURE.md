# How it works

Localhost Dock is a WinForms tray app plus a read-only CLI, both on one engine library (.NET 10). It has no service and needs no admin rights. Network traffic is restricted to configured local health checks and the opt-in loopback Aura status interface. See [Aura's contract](AURA.md).

```
src/LocalhostDock.Core/              the engine (no UI): used by the app, the CLI and the tests
├── Config/Registry.cs               apps.json model (schema v3: ids, workspaces), migration, normalization,
│                                    backups, corruption recovery, conflict-safe atomic save, read-only load
├── Core/
│   ├── AppController.cs             polling → Snapshot (status + ownership + health, uptime, memory, crashes,
│   │                                workspaces, discovered); serialized Start/Stop/Restart; readiness;
│   │                                dependency-aware workspace start/stop
│   ├── Launcher.cs                  command lines; windowed and log-capturing launches; log rotation
│   ├── AppDetector.cs               launcher / project folder / running process → suggested AppEntry; scan
│   └── PortAdvisor.cs               free-port suggestions; rewriting explicit ports in start commands
├── Health/HealthMonitor.cs          local-only HTTP health checks, bounded history, latency, availability
├── Workspace/
│   ├── WorkspacePlan.cs             dependency graph: start levels, stop order, cycles, dependents
│   └── SessionStore.cs              which apps were running (session restore)
├── Cli/CliRunner.cs                 `ldock` commands and versioned JSON schemas, secret redaction
└── Net/
    ├── ProcessGuard.cs              THE termination path: identity revalidation, protection, graceful → force
    ├── ProcessIdentity.cs           PID + creation time + image; verified process trees (PID-reuse safe)
    ├── ProcessCwd.cs                another process's working directory (ownership evidence)
    ├── PortTable.cs                 TCP listener table (IPv4 + IPv6, per binding)
    ├── ProcessInfo.cs               process details via WMI, cached by identity
    ├── ExcludedPorts.cs             Windows-reserved port ranges via netsh
    └── Native.cs                    Win32 declarations

src/LocalhostDock/                   the tray app
├── Program.cs                       single instance; forwards --show / --add / --exit; --selftest
├── Integration/                     single-instance pipe, global hotkey, Explorer menu
└── UI/
    ├── TrayApp.cs                   tray icon/menu, poll timer, toasts, open-on-ready, session save/restore
    ├── DockForm.cs                  the popup: search, sections, add flows, drag-drop, conflicts, port lookup
    ├── Rows.cs                      AppRow (health, conflict), WorkspaceRow (readiness), DiscoveredRow
    ├── AppEditor.cs                 add/edit with live detection, port checks, health, stop policy
    ├── WorkspaceEditor.cs           members, per-member dependencies, failure policy, parallelism, live order
    ├── ConflictWizard.cs            port conflict: who holds it, stop it, or move to a free port; RestoreDialog
    ├── ImportDialog.cs, LogViewer.cs, DarkMenu.cs, Theme.cs, Shell.cs

src/ldock/                           thin console host for CliRunner
```

## Health and readiness

`HealthMonitor` runs each app's check in the background at its interval, but only while the app's own process holds its port. Each check is a `GET` with its own timeout. It uses no proxy and follows no redirects (3xx counts as healthy, but never leads off-machine). `localhost` is connected on IPv4 and IPv6 at once, and the first to answer wins. That avoids Windows' slow IPv6-first fallback when a dev server bound only `127.0.0.1`. The last 120 samples are kept per app.

**Ready** means verified ownership of every primary binding *and* healthy (when configured), with no foreign secondary binding. Unverified ownership never satisfies readiness. Health state resets on listener identity or health target changes, and each probe uses a fresh connection. Ready settles a Start and gates workspace dependents. Two failed checks make a running app **degraded**. Every start records its time-to-ready.

## Workspaces

`WorkspacePlan.Levels` is Kahn's algorithm, with member order breaking ties. `StartWorkspaceAsync` starts each member as soon as its dependencies are ready, with at most `maxParallel` in flight. Failed dependencies block their dependents. With `onFailure: "stop"`, nothing new launches; in-flight starts finish. The app gate remains held for the full readiness wait. Stops walk reverse dependency order one app at a time. A failed stop preserves that member's prerequisites.

`WorkspacePresets` imports selected workspaces from registry-shaped JSON, remapping IDs, reusing matching services and preserving configuration. New commands are unverified and require first-use confirmation. The review dialog shows commands before saving; import has no lifecycle effects.

`Integration/AuraStatusServer` takes published snapshots rather than a controller reference. It binds an exclusive `TcpListener` to 127.0.0.1, implements bounded one-request HTTP, authenticates per-start credentials and serializes allowlisted `AuraStatus` records. Tray startup/reload/exit owns its lifetime. Poll failures invalidate the cache. HTTP requests cannot invoke controller methods. The Node adapter in `integrations/aura` is tested against the Windows server.

Session restore builds a temporary workspace from the apps to restore (`WorkspacePlan.Combine`). It keeps only the dependencies your workspaces declare between those apps, so a restore starts things in a sensible order.

## CLI

`CliRunner` loads the registry with `Registry.Load(readOnly: true)`. That reads, migrates and normalizes **in memory only**: it creates, backs up, repairs and saves nothing, and `Save()` throws. It then polls once with a fresh `AppController`, which has no launch records, so ownership comes from the working directory and command line. Output records are versioned (`localhost-dock/status@1`, `who@1`, `manifest@1`). Start/stop commands pass through pattern-based secret redaction.
## The poll loop

Every `pollMs` (2 s by default), `TrayApp` runs `AppController.Poll()` on a thread-pool thread:

1. **Read listeners.** `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_LISTENER)` for `AF_INET` and `AF_INET6` returns every listening socket and its owning PID in well under a millisecond. This is the same data `netstat -ano` shows, without spawning a process.
2. **Match apps, with ownership.** Each listener on an app's ports is classified as *owned*, *matched*, *unverified* or *foreign* (see [Ownership](CONFIGURATION.md#ownership)). Owned means a verified descendant of the process the dock launched; matched means its working directory (read from the process's PEB), command line or executable is in the app's folder. The app is *up* if a non-foreign process holds its port, and in *conflict* if only foreign ones do. The controller's explicit Starting/Stopping states override that until they settle. Uptime and memory count only the app's own processes. While an app is up, the dock holds handles to its processes, so when it goes down it knows their exit codes and whether you asked for it (crash detection by identity).
3. **Groups** are computed from their members' statuses.
4. **Discover.** Listeners on unregistered ports are filtered (see [discovery rules](CONFIGURATION.md#discovery-rules)) and their process details are looked up through WMI (`Win32_Process`), cached by identity (PID + creation time) so polling stays cheap and a recycled PID never inherits stale details. System processes and services are never listed.

The resulting `Snapshot` goes back to the UI thread, which updates three things:
- **The tray:** icon colour and tooltip.
- **Toasts:** it compares each app's state with the previous poll. Up → down without a recent user Stop is a crash; Starting → Down is a failed start; Starting → Up is a successful start.
- **The panel:** rows are rebuilt only when the set of apps, groups or discovered ports changes. Otherwise dots, subtitles and button states update in place, so nothing flickers.

## Detection

`AppDetector` is pure file reading: no processes are started, nothing is written.

- **Launchers:** a reference-following reader collects the launcher plus scripts it calls (`%~dp0x.ps1`, `$PSScriptRoot\x`, quoted script names) two levels deep. One regex then finds port mentions (`port … N`, `-p N`, `host:N`, `http.server N`, `runserver N`), skipping comparisons and range checks. Ports are ranked by frequency with first mention breaking ties, and a port that appears in a local URL is preferred as primary.
- **`package.json`:** script selection, then a resolution chain that ends at the first answer: explicit port flag → delegated workspace (`-w`, `--prefix`, `cd`) → local file the script runs → bundler config → framework default from dependencies.
- **Running processes:** absolute paths in the command line are cut at `\node_modules`, then walked upwards until a folder yields an app. That's how `node C:\x\web\node_modules\vite\bin\vite.js` becomes the `web` project's `npm run dev`.

## Starting and stopping

`Launcher.Build` turns a `start`/`stop` string into an executable plus arguments:
- `.cmd`/`.bat` → `cmd.exe /d /s /c ""<path>""`;
- `.ps1` → `powershell -File`;
- `.exe` → itself;
- anything else → `cmd.exe /d /s /c "<command>"`.

`/s` makes cmd strip only the outermost quotes, which keeps quoting predictable.

- **Windowed starts** use `ShellExecute`, so each launcher gets its own console, the same as double-clicking it.
- **Captured starts** call `CreateProcess` directly with the log file as inherited stdout/stderr and NUL as stdin. There's no shell redirection to quote, and a `pause` can't hang.
- **Pinning:** every launched process is pinned by identity *while .NET still holds its handle*, so the PID can't have been reused.

**Operations are serialized per app.** A per-app gate allows one Start/Stop/Restart at a time. Restart starts again only after Stop has verified the ports are free.

**Stop** runs the app's stop script, then hands each remaining listener to `ProcessGuard` according to its ownership. Owned/matched processes go straight to the guard. Unverified/foreign ones go only after the user confirms.

**`ProcessGuard`** is the only code that terminates processes:
1. It revalidates the captured identity (a PID now belonging to another program is refused).
2. It refuses protected processes. That includes processes it can't open whose name is critical or that run in session 0 (services).
3. It asks the process to exit (closing its main window and sending Ctrl+C to its console, which works from the GUI dock; a test host can't send it).
4. It waits `gracefulTimeoutSec`.
5. Then, if the stop policy allows, it force-kills the verified tree leaves-first, re-checking each process's identity immediately before killing it. A child only counts if it was created after its parent, so a recycled parent PID can't adopt unrelated processes.

**Groups** start members in order. Each waits for *readiness* (its own process listening) or fails fast on a non-zero launcher exit code, honouring `onFailure`. They stop one at a time in reverse order.

## Registry

All writes go through `AppController.Mutate`, under the registry lock. Polling reads cloned snapshots, so the UI can't race with the background poll.

**Saving** is atomic (write a temp file, then move it). It first compares the file's hash with what was last read or written. If someone else changed the file, the save is refused (`RegistryConflictException`), and the dock reloads instead of overwriting. Each save keeps the previous file in `backups\` (the last 20).

**Loading:**
- A corrupt file is quarantined, and the newest parseable backup is restored.
- Schema 1 files (no `schemaVersion`) are backed up, migrated (ids assigned, group members converted from names to ids) and written back once.
- Data is normalized, and any problems are reported as load warnings.
- A file from a newer schema is refused.
## Single instance and the Explorer menu

The first instance owns a per-user mutex and listens on a named pipe created with `PipeOptions.CurrentUserOnly`. Only the same Windows user can connect, and another user can't squat the name. Messages are capped at 8 KB with a read timeout, and only `--show`, `--exit` and `--add <existing fully-qualified path>` survive validation. A second launch (from Explorer's *Add to Localhost Dock*, a script, or a double-click) writes its arguments to the pipe and exits, and the running dock handles them on the UI thread. The Explorer entries live under `HKCU\Software\Classes` (folders, `.cmd`, `.bat`, `.ps1`), so they need no admin rights, and they're off until you enable them.

## Why WinForms

It ships in the .NET Desktop Runtime, starts in well under a second, idles at a few MB, and `NotifyIcon` is the most dependable tray API on Windows. The panel is drawn with standard controls in a borderless window (rounded corners and a drop shadow from DWM on Windows 11). Icons come from Segoe Fluent Icons / Segoe MDL2 Assets, so no image assets are needed. Layout values go through a DPI helper (`Theme.S`), and the app runs *system-DPI aware*, so Windows scales the whole window uniformly on monitors with a different scale factor.

## Testing

`tests/LocalhostDock.Tests` (xUnit, 117 tests, run by CI before every release):

- **Safety** (`SafetyTests`, 15 tests). An unrelated process on an app's port survives Stop, with or without a confirmation callback, unless the user explicitly confirms. Working-directory ownership is stopped without asking. Recycled PIDs and changed executables are refused. Protected/service processes are refused, even unopenable ones. Graceful-only never forces. Concurrent operations are serialized. Restart doesn't start after an incomplete stop. Port takeover becomes a conflict and blocks Start. Crash detection uses exit code and identity, and a user stop isn't a crash. Group stop is sequential in reverse order; group start stops at a failed dependency, or continues by policy.
- **Registry** (`RegistryTests`): seeding, v1 → v2 migration with backup, normalization of bad data, corrupt-file recovery, outside edits never overwritten, newer schema refused, backup rotation, collision-free log names.
- **Detection** (`DetectorTests`): launchers and the scripts they call, paths with spaces, two-port launchers, PowerShell-only projects, `.cmd`-over-`.ps1` preference, npm/monorepo/Vite, process → project tracing with unverified flag and port-mismatch note, folder scans, port grammar positives and false-positive rejections.
- **Batch 2** (`BatchTwoTests`, `UiSmokeTests`): health URL rules (local only), real HTTP checks (200/503/timeout, latency, bounded history, no slow localhost fallback), degraded state, readiness waiting for health, dependency levels/stop order/cycles, parallel starts with dependents waiting and `maxParallel` enforced, v2 → v3 migration, read-only load, session candidates, port suggestions and start-command rewriting, CLI schemas/ownership/health/redaction/exit codes/never-writes, FastAPI/Flask/ASP.NET/Spring/Rails/Laravel detection. The four dialogs are also rendered **off-screen** and checked for controls that collapse or overflow.
- **Core/features:** listener table (including separate IPv4/IPv6 bindings), process details with identity, end-to-end Start → up → Stop → down with a launcher whose path has spaces, `&` and parentheses, duplicate-launch refusal, discovery (no system processes), log capture without shell redirection (stdout, stderr, `pause` not hanging), log rotation, command-line quoting, and pipe argument sanitizing.

Test processes are real (`node`/`powershell` listeners). `TestProcesses` records the identity of everything a test starts and cleans up **only those**, through `ProcessGuard`. The test assembly points `LOCALHOST_DOCK_HOME` at a throwaway folder before any test runs and disables parallelism, so a test run can never touch your real registry.
