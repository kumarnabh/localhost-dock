# How it works

Localhost Dock is a single-process WinForms app (.NET 8) with no service, no admin rights and no network access.

```
src/LocalhostDock/
├── Program.cs               single-instance guard, --show flag
├── Config/Registry.cs       apps.json model, load/save (atomic write), first-run seeding
├── Core/AppController.cs    polling → Snapshot, discovery, Start/Stop/Restart
├── Net/PortTable.cs         TCP listener table via iphlpapi!GetExtendedTcpTable (IPv4 + IPv6)
├── Net/ProcessInfo.cs       PID → name/path/command line via WMI; kill-tree via taskkill
├── Net/ExcludedPorts.cs     Windows-reserved port ranges via netsh
└── UI/
    ├── TrayApp.cs           NotifyIcon, tray menu, poll timer, Start with Windows (HKCU Run key)
    ├── DockForm.cs          the popup panel: app rows, discovered rows, port lookup
    ├── AppEditor.cs         add/edit dialog
    └── Theme.cs             colours, DPI scaling helper, generated tray icon
```

## The poll loop

Every `pollMs` (2 s by default), `TrayApp` runs `AppController.Poll()` on a thread-pool thread:

1. **Read listeners.** `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_LISTENER)` for `AF_INET` and `AF_INET6` returns every listening socket and its owning PID in well under a millisecond. This is the same data `netstat -ano` shows, without spawning a process.
2. **Match apps.** An app is *up* if anything listens on its `port`. Pending states (*starting* / *stopping*) set by the buttons override that until the port changes or the timeout expires.
3. **Discover.** Listeners on unregistered ports are filtered (see [discovery rules](CONFIGURATION.md#discovery-rules)) and their process details are looked up through WMI (`Win32_Process`), cached per PID for 30 seconds so polling stays cheap.

The resulting `Snapshot` goes back to the UI thread, where it updates the tray icon and tooltip and the panel. The panel rebuilds its rows only when the set of apps or discovered ports changes. Otherwise it just updates the dots, subtitles and button states in place, so nothing flickers.

## Starting and stopping

`AppController.Run` turns a `start`/`stop` string into a `ProcessStartInfo` with `UseShellExecute = true`, so each launcher gets its own console window, the same as double-clicking it. `.cmd`/`.bat` files go through `cmd.exe /c ""<path>""` (the doubled quotes are how `cmd` handles paths with spaces).

Stop prefers your own stop script, then falls back to `taskkill /PID <pid> /T /F` on whatever holds the app's ports. Using `/T` matters: `npm run dev` is `cmd → npm → node`, and killing only the listener's parent (or only the listener) tends to leave orphans.

## Why WinForms

It ships in the .NET Desktop Runtime, starts in well under a second, idles at a few MB, and `NotifyIcon` is the most dependable tray API on Windows. The panel is drawn with standard controls in a borderless window (rounded corners and a drop shadow from DWM on Windows 11), with layout values passed through a DPI scale helper so it stays crisp at 125%/150%.

## Testing

`tests/LocalhostDock.Tests` (xUnit) covers the parts that touch the OS:

- the listener table sees IPv4 and IPv6 sockets with the right PID;
- WMI process lookups and the kill guard (refuses PID 4 and itself);
- registry round-trip and first-run seeding (using `LOCALHOST_DOCK_HOME`);
- **end-to-end Start → up → Stop → down** using a real `.cmd` launcher with spaces in its path;
- discovery of a real child `node` server, and that Ignore and registration hide it;
- parsing of `netsh` excluded-port output.

Run them with `dotnet test`.
