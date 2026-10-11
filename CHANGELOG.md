# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

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
