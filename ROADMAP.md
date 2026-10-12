# Roadmap

## v0.2: shipped

- [x] **Smart add:** drop a launcher or project folder; name, port(s), URL, start/stop and working directory are detected (launchers + the scripts they call, npm/pnpm/yarn/bun projects, monorepo `dev:*` scripts, Vite/Next/Astro/… defaults, Django)
- [x] **Scan a folder for apps** with a checklist import
- [x] **Add from Discovered** traces the process back to its project
- [x] Explorer right-click "Add to Localhost Dock" and `--add <path>` (single instance, forwarded over a pipe)
- [x] **Start groups:** ordered start (waits for each port), reverse stop
- [x] **Crash / failed-start / started toasts** with click actions
- [x] **Live log tail:** captured output or an existing log file
- [x] Uptime and memory per app; icon buttons; ⋯ menu (logs, copy URL, folder, VS Code, terminal, groups, reorder, duplicate)
- [x] Search with Enter-to-open; tray quick-control menu; global hotkey; dark dialogs and menus
- [x] Port checks in the Add dialog (used by another app, in use, reserved by Windows)

## v0.1

- [x] Registry, status dots, Open/Start/Stop/Restart, existing launchers reused as-is
- [x] *Who's on this port?* with confirmed Kill and reserved-range detection
- [x] Discovery of unknown listening ports

## Next

- [ ] Optional HTTP health check (`"health": "/healthz"`) for apps that listen before they're ready
- [ ] Per-app environment variables and "open URL after start"
- [ ] Restart automatically after a crash (opt-in, with back-off)
- [ ] Drag to reorder rows
- [ ] Light theme following the Windows setting

## Later / ideas

- [ ] Docker containers as apps (`docker ps` ports)
- [ ] WSL services
- [ ] Signed release builds and a winget manifest

Have an idea? [Open an issue](../../issues).
