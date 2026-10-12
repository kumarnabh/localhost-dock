# Configuration reference

Localhost Dock reads a single file:

```
%APPDATA%\LocalhostDock\apps.json
```

Set the `LOCALHOST_DOCK_HOME` environment variable to use a different folder, for example to keep the registry in a synced folder or to try out a demo registry. Captured logs go in a `logs` folder next to it.

You rarely need to edit this file. Adding (drag-drop, *Scan a folder*, *Discovered → Add*, Explorer's right-click), editing (**⋯ → Edit…**), groups, ordering, Ignore and notification settings are all saved for you. To edit by hand, use tray menu → **Edit apps.json**, then **Reload apps.json**. If the file doesn't parse, the dock keeps the previous registry and shows you the error.

JSON comments (`//`, `/* */`) and trailing commas are allowed. Note that saving from the dock rewrites the file without comments, and leaves out fields that are at their default.

## Top level

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `apps` | array | `[]` | Your apps, in the order they appear in the dock |
| `groups` | array | `[]` | Start groups (see below) |
| `ignoredPorts` | number[] | `[]` | Ports never offered under *Discovered* (filled by **Ignore**) |
| `pollMs` | number | `2000` | How often to refresh status, in milliseconds (minimum 500) |
| `hotkey` | string | `"Ctrl+Alt+L"` | Global shortcut that opens the dock: modifiers `Ctrl`, `Alt`, `Shift`, `Win` plus one key (`L`, `F12`, `D1`/`1`, `Space`, …). `""` disables it |
| `notifications` | string | `"all"` | `"all"` (crashes, failed starts, successful starts), `"crashes"` (crashes and failed starts only), or `"off"` |
| `scanFolders` | string[] | `[]` | The last few folders you scanned (pre-fills *Scan a folder*) |

## App fields

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `name` | string | yes | Display name. Must be unique |
| `port` | number | no* | Primary port. The status dot follows this port |
| `extraPorts` | number[] | no | Other ports that belong to the app, e.g. the API behind a web front end. Shown on the row and freed by Stop |
| `url` | string | no | What **Open** launches. Defaults to `http://localhost:{port}` |
| `start` | string | no | Start command (see below). Without it, Start and Restart are disabled |
| `stop` | string | no | Stop command (see below). Without it, Stop kills whatever holds the app's ports |
| `cwd` | string | no | Working directory for `start` and `stop`. Defaults to the launcher's folder |
| `window` | string | no | `"minimized"` (default), `"normal"` or `"hidden"`: how the launched console window appears |
| `captureLogs` | bool | no | Record everything `start` prints to `logs\<name>.log` (the window runs hidden). **View logs** tails it |
| `logFile` | string | no | A log file your app already writes; **View logs** tails this instead |

\* An app without a `port` still shows in the dock (grey dot) so you can keep a placeholder. Open, Stop and Restart need a port.

## Groups

```json
"groups": [
  { "name": "Shop dev", "apps": ["Shop API", "Shop Web", "Shop Worker"] }
]
```

- **Start all** starts members **in list order**. After each one, it waits up to 45 s for that app's port before starting the next, so an API is up before the web app that calls it. Apps that are already up are skipped.
- **Stop all** stops members in **reverse** order, all at once.
- An app can be in several groups. Renaming an app in the dock updates its group memberships, and removing an app removes it from its groups.

## How Start works

`start` is either a **path to a file** or **any command line**.

| `start` value | Runs as |
| --- | --- |
| Path to an existing `.cmd` / `.bat` | `cmd.exe /c "<path>"`. Your launcher runs exactly as if you'd double-clicked it |
| Path to an existing `.ps1` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<path>"` |
| Path to an existing `.exe` | The exe itself |
| Anything else (`npm run dev`, `python -m http.server 8000`, …) | `cmd.exe /c <command>` |

Paths can contain spaces and don't need quotes. Arguments after a file path aren't supported. If you need them, write the whole thing as a command line (`"C:\path\tool.exe" --flag`), which runs through `cmd.exe`.

The process starts in its own console window (minimized by default), in `cwd`, or in the launcher's own folder if `cwd` isn't set. That matches launchers that do `cd /d "%~dp0"`.

With `captureLogs`, the same command runs inside `cmd /c "… >> logs\<name>.log 2>&1"` in a hidden window. The log is restarted once it passes 5 MB.

After you click Start, the dot turns amber until the primary `port` starts listening, for up to 90 seconds.

> **Tip:** if your launcher ends with `pause` on error, keep `window` as `minimized` or `normal` (and `captureLogs` off). A hidden window that's waiting at `pause` will never finish.

## How Stop works

1. If `stop` is set, it runs the same way as `start` (always minimized) and the dock waits up to 20 s for it to exit.
2. If the app's ports are free within 5 s after that, you're done.
3. Otherwise (or if there's no `stop`), every process listening on `port` and `extraPorts` is killed along with its child processes (`taskkill /PID <pid> /T /F`). The console window the dock itself started for the app is closed too.

**Restart** is Stop followed by Start.

## Examples

**Reuse an existing pair of launchers**

```json
{
  "name": "Dashboard",
  "port": 4780,
  "url": "http://127.0.0.1:4780",
  "start": "C:\\Tools\\My Dashboard\\Start Dashboard.cmd",
  "stop": "C:\\Tools\\My Dashboard\\Stop Dashboard.cmd"
}
```

**A launcher that starts two servers** (a front end on 3000 and an API on 8000)

```json
{
  "name": "Studio",
  "port": 3000,
  "extraPorts": [8000],
  "start": "C:\\dev\\studio\\Start-Studio.cmd"
}
```

**An npm script in a monorepo, with captured logs**

```json
{
  "name": "Web app",
  "port": 3001,
  "start": "pnpm run dev:web",
  "cwd": "C:\\dev\\suite",
  "captureLogs": true
}
```

**Avoiding a port clash.** Two Next.js apps would both default to `:3000`, so pin one of them:

```json
{
  "name": "Marketing site",
  "port": 3002,
  "start": "npm run dev:marketing -- -p 3002",
  "cwd": "C:\\dev\\suite"
}
```

## Detection rules

Used by drag-drop, *Add from a launcher or folder*, `--add`, Explorer's menu and *Scan a folder*.

**A launcher file** (`.cmd`, `.bat`, `.ps1`, also `.py`/`.js`/`.exe`):
- **Name:** the file name minus `Start`/`Launch`/`Run` (`Start-Billing.cmd` → *Billing*). Generic names (`start.bat`, `run.cmd`) use the folder name instead.
- **Port:** read from the launcher and the scripts it references (`%~dp0…`, `$PSScriptRoot\…`, `"x.ps1"`), up to two levels deep. The port in the first local URL (`http://127.0.0.1:4780`) wins; otherwise the most-mentioned one does. Other ports become `extraPorts`. Range checks (`-lt 1024`, `ValidateRange`) are ignored.
- **Stop:** a sibling named `Stop-X`, `Stop X`, `X-Stop`, or the launcher's name with `Start` → `Stop`.
- **Fallback:** if no port is found and there's a `package.json` beside it, that project's port is used.

**A folder:**
- Launchers in it (skipping `stop`, `build`, `test`, `install`, `setup`, … scripts). Several launchers that resolve to the same port count as one app.
- `package.json`:
  - If it has `dev:<name>` scripts, it's treated as a monorepo, one app per script. Otherwise the first of `dev`, `start`, `serve`, `preview` is used.
  - The port comes from the script itself (`-p`, `--port`, `PORT=`), the workspace it delegates to (`-w`, `--workspace`, `--prefix`, `cd`), the local file it runs (`node scripts/server.js`), `vite.config.*` / `astro.config.mjs`, or the framework default.
- `manage.py` → Django on `:8000`.

**Scan a folder** walks up to 3 levels deep. It skips `node_modules`, `.git`, `bin`, `obj`, `dist`, `build`, virtualenvs, and hidden or system folders, and it doesn't look *inside* a folder once it has found an app there. Apps already in your dock are left out.

## Discovery rules

A listening port appears under *Discovered* when **all** of these are true:

- it isn't any registered app's `port` or `extraPorts`, and isn't in `ignoredPorts`;
- the owning process isn't a system process (`System`, `svchost.exe`, `lsass.exe`, `services.exe`, `spoolsv.exe`, …) and its executable isn't under `C:\Windows`;
- the port is below 49152, **or** the process is a known dev runtime (`node`, `python`, `deno`, `bun`, `java`, `dotnet`, `ruby`, `php`, `go`, …). High ephemeral ports are usually editor and agent plumbing, so they're hidden unless a dev runtime owns them.

The suggested name comes from the command line (`vite` → "Vite", `uvicorn` → "Uvicorn", `next` → "Next.js", …), falling back to the process name. **Add** then traces the process to its project folder (see above) so Start can relaunch it properly. If that fails, it falls back to re-running the exact command line.

## Command line

```
LocalhostDock.exe [--show] [--add <launcher-or-folder>]
```

Only one dock runs per Windows user. Launching it again hands the arguments to the running instance: `--show` opens the dock, and `--add` opens the Add flow for that path.
