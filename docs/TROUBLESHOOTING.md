# Troubleshooting

### My dev server fails with `EACCES` / "permission denied", but *Who's on this port?* says nothing is listening

Windows reserves blocks of TCP ports for Hyper-V, WSL2, Docker Desktop and Windows Sandbox. Nothing can bind inside those blocks, and the blocks can move after a reboot. Localhost Dock checks this for you: when nothing is listening on the port you look up, it tells you if the port sits in a reserved range.

To see the ranges yourself:

```powershell
netsh int ipv4 show excludedportrange protocol=tcp
```

Fixes, easiest first: pick a port outside the ranges (e.g. `vite --port 5280`); or, from an elevated prompt, `net stop winnat` then `net start winnat` to re-roll the ranges; or permanently reserve your port *before* Hyper-V grabs it with `netsh int ipv4 add excludedportrange protocol=tcp startport=5173 numberofports=1` (also elevated, and run while winnat is stopped).

### An app is running but its dot is red

The dot follows the app's **`port`** only. Check that the port in `apps.json` is the one the app actually listens on: type it into *Who's on this port?*, or look under *Discovered* for the real port. If the app listens on several ports, put the main one in `port` and the rest in `extraPorts`.

### Start does nothing / the dot stays amber, then goes red

- Open the minimized console window the launcher started; the error is usually there. Set `"window": "normal"` while you debug.
- Make sure `start` points at a file that exists, or is a valid command line for `cwd`.
- Launchers with `pause` at the end won't finish when `"window": "hidden"` is set.

### Stop says it worked but the app comes back

Something is restarting it, such as a watcher, a supervisor (`pm2`, `nodemon`), or a Windows service. Stop the supervisor itself, or point `stop` at a script that does.

### Kill fails with "Access is denied"

The process runs as another user or elevated (as Administrator). Localhost Dock runs without admin rights and can't kill it. Use an elevated Task Manager instead.

### The command line column is empty for some processes

WMI can't read the command line of elevated or other users' processes from a non-elevated app. The port and PID are still correct.

### Where is my registry?

`%APPDATA%\LocalhostDock\apps.json` (tray menu → **Edit apps.json** opens it). Set `LOCALHOST_DOCK_HOME` to move it.

### It says it's already running

Only one instance runs per Windows user. Look for the icon in the tray overflow (the `^` arrow), and drag it onto the taskbar to keep it visible.
