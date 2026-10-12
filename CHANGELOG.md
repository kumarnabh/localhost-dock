# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

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
