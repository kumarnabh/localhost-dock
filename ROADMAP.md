# Roadmap

## v0.1: shipped

- [x] Registry of apps (`apps.json`) with name, port, extra ports, URL, start/stop commands and working directory
- [x] Green/red/amber status dot per app; Open, Start, Stop and Restart
- [x] Reuse of existing `.cmd` / `.bat` / `.ps1` / `.exe` launchers as-is
- [x] *Who's on this port?* lookup with a confirmed Kill (process tree)
- [x] Detection of Windows-reserved (excluded) port ranges in the lookup
- [x] Auto-discovery of unknown listening ports, with Add / Ignore / Kill
- [x] Add / edit / remove apps from the dock
- [x] Start with Windows toggle; single instance; per-monitor DPI

## Next

- [ ] **Start groups.** Named sets of apps started and stopped together, e.g. `"Shop dev": ["Shop API", "Shop Web", "Shop Worker"]`, with an optional order and a wait for each port before the next starts.
- [ ] **Live log tail per app.** An opt-in `"logs": "capture"` mode that runs the launcher with redirected output and shows the last N lines in a flyout; plus `"logFile"` to tail a file your launcher already writes.
- [ ] **Crash toast.** A Windows notification when an app goes from up to down without you clicking Stop, with Restart and Open log actions.

## Later / ideas

- [ ] Optional HTTP health check (`"health": "/healthz"`) for apps that listen before they're ready
- [ ] Global hotkey to open the dock
- [ ] Copy URL / open folder / open in editor actions per app
- [ ] Import launchers by scanning a folder for `Start-*.cmd`
- [ ] Light theme following the Windows setting
- [ ] Signed release builds and a winget manifest

Have an idea? [Open an issue](../../issues).
