# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [0.3.0] - 2026-10-31

Batch 1 of the upgrade backlog: process-control safety and correctness. Per-item status is in [docs/BACKLOG-STATUS.md](docs/BACKLOG-STATUS.md).

### Security / safety

- **Stop never kills an unrelated process just because it holds the app's port.** Listening processes are classified as owned (started by the dock), matched (working directory, command line or executable in the app's folder), unverified, or foreign. Only owned/matched are stopped without asking; anything else needs explicit confirmation (default No).
- All process termination goes through one guard. It revalidates PID + creation time + executable immediately before each kill (recycled PIDs are never hit) and refuses system processes, services, the dock itself and its parent, even when they can't be inspected.
- Graceful shutdown first (window close + Ctrl+C), force only after `gracefulTimeoutSec`; new `stopPolicy`: `graceful` / `graceful-only` / `force`.
- Single-instance pipe: current-user-only, size-limited, argument whitelist.

### Added

- `Conflict` state when another program holds an app's port; Start is refused and the holder is named.
- Crash detection by process identity, with exit codes; failed-start reasons (launcher exit code, timeout).
- Group `onFailure` policy (`stop` / `continue`) with real readiness checks.
- Registry schema v2: immutable app and group ids, automatic migration (original backed up), normalization of bad data, rotating backups, corruption recovery, refusal of files from newer versions.
- `startUnverified` for start commands reconstructed from running processes (confirmed before the first run).
- Per-app `startTimeoutSec`, `gracefulTimeoutSec`; stop policy in the Edit dialog.
- Detection of PowerShell-only `.ps1` launchers; scan-limit warning.
- Installer: staged build + self-test before stopping the running version, automatic rollback, `-Rollback`, `-SelfContained`; `--exit` and `--selftest` switches.
- Release: self-contained download and `SHA256SUMS.txt`.

### Changed

- **.NET 10** (was .NET 8).
- Group Stop all runs one app at a time, in reverse order; Restart only starts after a verified stop; one operation at a time per app.
- Captured logs go to the file through inherited handles instead of nested `cmd` redirection; stdin is NUL (no `pause` hangs); 5 MB rotation, 3 kept; collision-free file names.
- Port detection is stricter (no more timeouts, ranges, comparisons, sizes or versions mistaken for ports).
- IPv4 and IPv6 listeners on the same port are kept separate.
- "Edit apps.json" no longer saves first; outside edits are never overwritten and are reloaded automatically.
- Toast clicks can no longer run another toast's action.
- Add dialog detection runs in the background.
- CI: dependency vulnerability gate, coverage, self-test of both builds before release.

### Fixed

- GDI handle leak on every tray-icon colour change.
- Process details could be attributed to a recycled PID.
- Registry changes from the UI could race with background polling.

## [0.2.0] - 2026-10-12

### Added

- **Smart add.** Drop a launcher (`.cmd`/`.bat`/`.ps1`) or project folder on the dock, use *+ Add → Add from a launcher or project folder…*, Explorer's *Add to Localhost Dock*, or `LocalhostDock.exe --add <path>`. Name, ports, URL, start/stop scripts and working directory are detected from launchers and the scripts they call, `package.json` scripts (npm/pnpm/yarn/bun, monorepo `dev:*` workspaces, bundler configs, framework defaults) and Django projects.
- **Scan a folder for apps:** a checklist of everything found up to 3 levels deep, skipping apps already in the dock.
- **Add** on a discovered port traces the process back to its project and launcher.
- Add dialog: live detection, multiple-candidate picker, port checks (used by another app / in use / reserved by Windows), drag-drop.
- **Start groups** (`groups` in apps.json): Start all in order, waiting for each port; Stop all in reverse. Group editor, *Add to group* menu, tray menu entries.
- **Notifications:** crash (up → down without Stop, click to restart), failed start (click for logs), started (click to open). Configurable: all / crashes / off.
- **Logs:** `captureLogs` records the start command's output; `logFile` points at an existing log; live log viewer with follow and clear.
- Uptime and memory per app; per-app ⋯ menu (View logs, Copy URL, Open folder, Open in VS Code, Open terminal here, Add to group, Move up/down, Edit, Duplicate, Remove).
- Search box (Enter opens the first match), <kbd>Ctrl</kbd>+<kbd>N</kbd> to add, global hotkey (`hotkey`, default <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>L</kbd>).
- Tray right-click menu with quick Start/Stop/Restart/Open per app and group, and a Settings submenu.
- Single instance forwards arguments to the running dock over a named pipe.

### Changed

- Redesigned panel: icon buttons with tooltips, hover rows, dark dialogs, menus and title bars.
- Stop also closes the console window the dock started for the app.
- The app is now system-DPI aware, so the panel scales consistently on multi-monitor setups with different scale factors.
- `install.ps1 -Uninstall` also removes the Explorer menu entries.

### Fixed

- Tests can no longer touch the real `%APPDATA%\LocalhostDock\apps.json` (sandboxed home folder, no parallel runs).

## [0.1.0] - 2026-10-12

### Added

- Tray icon with an at-a-glance status dot and an "x/y up" tooltip.
- Popup dock listing registered apps with up/down/starting/stopping status and Open, Start, Stop and Restart.
- `apps.json` registry in `%APPDATA%\LocalhostDock` (or `LOCALHOST_DOCK_HOME`), seeded from `apps.example.json`.
- Start/stop through existing `.cmd`, `.bat`, `.ps1` and `.exe` launchers, or any command line.
- Stop falls back to killing the process tree on the app's ports.
- *Who's on this port?* lookup with a confirmed Kill, and detection of Windows-reserved port ranges.
- Discovery of unknown listening ports with Add, Ignore and Kill.
- Add/edit/remove apps from the dock; Start with Windows; single-instance guard; per-monitor DPI support.
- `install.ps1` for per-user install/uninstall; CI build, test and tagged releases.
