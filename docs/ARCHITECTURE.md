# How it works

Localhost Dock is a single-process WinForms app (.NET 8) with no service, no admin rights and no network access.

```
src/LocalhostDock/
├── Program.cs                   single instance; forwards --show / --add to the running dock
├── Config/Registry.cs           apps.json model (apps, groups, settings), load/save (atomic write)
├── Core/
│   ├── AppController.cs         polling → Snapshot (status, uptime, memory, groups, discovered),
│   │                            Start/Stop/Restart, group start/stop, log capture
│   └── AppDetector.cs           launcher / project folder / running process → suggested AppEntry; folder scan
├── Net/
│   ├── PortTable.cs             TCP listener table via iphlpapi!GetExtendedTcpTable (IPv4 + IPv6)
│   ├── ProcessInfo.cs           PID → name/path/command line via WMI; kill-tree via taskkill
│   └── ExcludedPorts.cs         Windows-reserved port ranges via netsh
├── Integration/
│   ├── SingleInstance.cs        per-user mutex + named pipe for forwarded arguments
│   ├── Hotkey.cs                global shortcut (RegisterHotKey)
│   └── ExplorerMenu.cs          optional per-user "Add to Localhost Dock" context-menu entries
└── UI/
    ├── TrayApp.cs               NotifyIcon, tray quick-control menu, poll timer, toasts, settings
    ├── DockForm.cs              the popup: search, sections, add flows, drag-drop, port lookup
    ├── Rows.cs                  AppRow, GroupRow, DiscoveredRow (hover rows with icon buttons)
    ├── AppEditor.cs             add/edit dialog with live detection and port checks
    ├── ImportDialog.cs          "Scan a folder for apps" checklist
    ├── GroupEditor.cs           create/edit start groups
    ├── LogViewer.cs             live tail of captured or app-written logs
    ├── DarkMenu.cs, Theme.cs    dark palette, icon font, DPI scaling helper, tray icon
    └── Shell.cs                 VS Code / Windows Terminal helpers
```

## The poll loop

Every `pollMs` (2 s by default), `TrayApp` runs `AppController.Poll()` on a thread-pool thread:

1. **Read listeners.** `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_LISTENER)` for `AF_INET` and `AF_INET6` returns every listening socket and its owning PID in well under a millisecond. This is the same data `netstat -ano` shows, without spawning a process.
2. **Match apps.** An app is *up* if anything listens on its `port`. Pending states (*starting* / *stopping*) set by the buttons override that until the port changes or the timeout expires. Uptime is the start time of the oldest listening process. Memory is the sum of their working sets.
3. **Groups** are computed from their members' statuses.
4. **Discover.** Listeners on unregistered ports are filtered (see [discovery rules](CONFIGURATION.md#discovery-rules)) and their process details are looked up through WMI (`Win32_Process`), cached per PID for 30 seconds so polling stays cheap.

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

`AppController.Run` turns a `start`/`stop` string into a `ProcessStartInfo` with `UseShellExecute = true`, so each launcher gets its own console window, the same as double-clicking it. `.cmd`/`.bat` files go through `cmd.exe /c ""<path>""` (the doubled quotes are how `cmd` handles paths with spaces). With `captureLogs`, the same command is wrapped as `cmd /c "<command> >> "<log>" 2>&1"` in a hidden window.

Stop prefers your own stop script, then falls back to `taskkill /PID <pid> /T /F` on whatever holds the app's ports, and finally closes the console the dock launched. Using `/T` matters: `npm run dev` is `cmd → npm → node`, and killing only the listener's parent (or only the listener) tends to leave orphans.

## Single instance and the Explorer menu

The first instance owns a per-user mutex and listens on a named pipe. A second launch (from Explorer's *Add to Localhost Dock*, a script, or a double-click) writes its arguments to the pipe and exits, and the running dock handles them on the UI thread. The Explorer entries live under `HKCU\Software\Classes` (folders, `.cmd`, `.bat`, `.ps1`), so they need no admin rights, and they're off until you enable them.

## Why WinForms

It ships in the .NET Desktop Runtime, starts in well under a second, idles at a few MB, and `NotifyIcon` is the most dependable tray API on Windows. The panel is drawn with standard controls in a borderless window (rounded corners and a drop shadow from DWM on Windows 11). Icons come from Segoe Fluent Icons / Segoe MDL2 Assets, so no image assets are needed. Layout values go through a DPI helper (`Theme.S`), and the app runs *system-DPI aware*, so Windows scales the whole window uniformly on monitors with a different scale factor.

## Testing

`tests/LocalhostDock.Tests` (xUnit, 30 tests) covers:

- **Listener table:** IPv4 and IPv6 sockets are seen with the right PID.
- **Process info:** WMI lookups, and the kill guard (refuses PID 4 and itself).
- **Registry:** round-trip, first-run seeding, groups, rename/remove keeping groups consistent, defaults omitted.
- **Detection:** launchers that call `.ps1` files, paths with spaces, two-port launchers, range-check noise, npm scripts that run local server files, monorepo workspaces, Vite config ports, generic launcher names, folder scans that skip `node_modules`, and process → project tracing.
- **End-to-end:** Start → up → Stop → down with a real `.cmd` launcher; log capture of stdout and stderr; group start in order and stop.
- **Discovery** of a real child `node` server; `netsh` excluded-range parsing.

The test assembly points `LOCALHOST_DOCK_HOME` at a throwaway folder before any test runs and disables parallelism, so a test run can never touch your real registry. Run them with `dotnet test`.
