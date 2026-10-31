# Roadmap

Detailed status of the 110-item upgrade backlog: **[docs/BACKLOG-STATUS.md](docs/BACKLOG-STATUS.md)**.

## v0.3: shipped (safety and correctness)

- [x] Ownership-verified Stop: unrelated processes are never stopped without confirmation
- [x] One guarded termination path: identity revalidation (PID reuse safe), protected processes, graceful → force, stop policies
- [x] Serialized per-app operations; verified restart; sequential reverse group stop; group failure policies
- [x] Crash detection with exit codes; port-conflict state
- [x] Registry schema v2 with ids, migrations, normalization, backups, corruption recovery, conflict-safe saves
- [x] Safe log capture (no shell redirection), rotation; hardened single-instance pipe
- [x] .NET 10; staged installer with self-test and rollback; self-contained release; hashes; CI vulnerability gate

## Next (batch 2: workspaces and health)

- [ ] HTTP health/readiness checks (`health`), degraded state, latency
- [ ] Workspace profiles, dependency graphs, session restore
- [ ] Port-conflict wizard (reassign / free the port)
- [ ] Auto-restart with back-off and crash-loop protection
- [ ] Machine-readable CLI (`status --json`) and a read-only MCP interface
- [ ] Per-monitor DPI v2, keyboard navigation, light theme

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

- [ ] Per-app environment variables and "open URL after start"
- [ ] Drag to reorder rows
- [ ] Light theme following the Windows setting

## Later / ideas

- [ ] Docker containers as apps (`docker ps` ports)
- [ ] WSL services
- [ ] Signed release builds and a winget manifest

Have an idea? [Open an issue](../../issues).
