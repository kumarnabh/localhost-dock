# Configuration reference

## Optional Aura status

`"aura": { "enabled": false, "port": 0 }` controls the read-only status endpoint. It is off by default; port 0 selects a free IPv4 loopback port. Invalid ports disable it. Enable from tray Settings or edit and reload `apps.json`. Each start uses a new bearer token; there are no commands or lifecycle routes. See [Aura integration](AURA.md) for discovery, the Node adapter, security, presets and recovery.

Localhost Dock reads a single file:

```
%APPDATA%\LocalhostDock\apps.json
```

Set the `LOCALHOST_DOCK_HOME` environment variable to use a different folder, for example to keep the registry in a synced folder or to try out a demo registry. Captured logs go in a `logs` folder next to it.

You rarely need to edit this file. Adding (drag-drop, *Scan a folder*, *Discovered → Add*, Explorer's right-click), editing (**⋯ → Edit…**), workspaces, ordering, Ignore and notification settings are all saved for you. To edit by hand, use tray menu → **Edit apps.json**. The dock notices the change and reloads it.

**Your edits are safe:**
- The dock never overwrites a change made outside it. If you edited the file while the dock had unsaved changes, it keeps yours on disk, reloads, and tells you.
- Every save first keeps the previous version in `backups\` (the last 20).
- If `apps.json` becomes unreadable, it's kept as `apps.corrupt-<time>.json` and the newest readable backup is restored. A toast tells you.
- Problems the dock can fix (duplicate names, invalid ports, unknown values, workspace members that don't exist, dependency loops) are fixed on load and listed in a toast.

JSON comments (`//`, `/* */`) and trailing commas are allowed. Note that saving from the dock rewrites the file without comments, and leaves out fields that are at their default.

## Top level

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `schemaVersion` | number | `3` | File format version. Older files are migrated on load (schema 2 → 3 turns groups into workspaces), with the original backed up as `backups\apps-…-pre-v3.json`. A file from a *newer* version is refused rather than misread |
| `apps` | array | `[]` | Your apps, in the order they appear in the dock |
| `workspaces` | array | `[]` | Workspaces: apps started together, dependencies first (see below) |
| `ignoredPorts` | number[] | `[]` | Ports never offered under *Discovered* (filled by **Ignore**) |
| `pollMs` | number | `2000` | How often to refresh status, in milliseconds (minimum 500) |
| `hotkey` | string | `"Ctrl+Alt+L"` | Global shortcut that opens the dock: modifiers `Ctrl`, `Alt`, `Shift`, `Win` plus one key (`L`, `F12`, `D1`/`1`, `Space`, …). `""` disables it |
| `notifications` | string | `"all"` | `"all"` (crashes, failed starts, successful starts), `"crashes"` (crashes and failed starts only), or `"off"` |
| `restoreSession` | string | `"ask"` | After a reboot/crash: `"ask"`, `"auto"` or `"off"` (see [Session restore](#session-restore)) |
| `scanFolders` | string[] | `[]` | The last few folders you scanned (pre-fills *Scan a folder*) |

## App fields

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `id` | string | auto | Stable identity, generated if missing. Workspaces and runtime state use it, so renaming an app is safe. Don't reuse one |
| `name` | string | yes | Display name. Must be unique |
| `port` | number | no* | Primary port. The dot is green when *this app's* process listens on it (see [Ownership](#ownership)) |
| `extraPorts` | number[] | no | Other ports that belong to the app, e.g. the API behind a web front end. Shown on the row and freed by Stop |
| `url` | string | no | What **Open** launches. Defaults to `http://localhost:{port}` |
| `start` | string | no | Start command (see below). Without it, Start and Restart are disabled |
| `stop` | string | no | Stop command (see below). Runs before anything is terminated |
| `cwd` | string | no | Working directory for `start` and `stop`. Defaults to the launcher's folder |
| `window` | string | no | `"minimized"` (default), `"normal"` or `"hidden"`: how the launched console window appears |
| `captureLogs` | bool | no | Record everything `start` prints to `logs\<name>.<id>.log` (no window; stdin is empty, so `pause` can't hang). **View logs** tails it |
| `logFile` | string | no | A log file your app already writes; **View logs** tails this instead |
| `stopPolicy` | string | no | `"graceful"` (default: ask the app to exit, force after the timeout), `"graceful-only"` (never force-kill; Stop reports failure instead) or `"force"` |
| `gracefulTimeoutSec` | number | no | Seconds to wait for a graceful exit (default 8, 1–300) |
| `startTimeoutSec` | number | no | Seconds to wait for the port after Start (default 90, 5–1800) |
| `startUnverified` | bool | no | Set when the start command was reconstructed from a running process. You're asked before it first runs, and it clears itself after a start that works |
| `health`, `healthIntervalSec`, `healthTimeoutMs`, `openOnReady` | | no | See [Health checks](#health-checks) |

\* An app without a `port` still shows in the dock (grey dot) so you can keep a placeholder. Open, Stop and Restart need a port.

## Workspaces

```json
"workspaces": [
  {
    "id": "…", "name": "Shop dev",
    "apps": ["<api id>", "<web id>", "<worker id>"],
    "dependsOn": { "<web id>": ["<api id>"], "<worker id>": ["<api id>"] },
    "onFailure": "stop",
    "maxParallel": 4
  }
]
```

| Field | Default | Meaning |
| --- | --- | --- |
| `apps` | | Member app **ids**. When editing by hand you may write app **names**; they're converted to ids on load |
| `dependsOn` | `{}` | app → the members it needs to be *ready* before it starts. Missing means no dependencies. Loops are removed on load, with a warning |
| `onFailure` | `"stop"` | When a member fails to become ready: `"stop"` starts nothing more; `"continue"` starts everything that doesn't (directly or indirectly) depend on the failed app |
| `maxParallel` | `4` | How many members may be starting at the same time (1–16) |

- **Start** starts each member as soon as everything it depends on is **ready**, so independent members start in parallel. *Ready* means its own process is listening on its port and, if it has a `health` check, the check passes, within its `startTimeoutSec`.
- A member whose start command exits with an error fails immediately. Nothing that depends on a failed member is ever started. Members that are already ready are skipped.
- **Stop** stops members one at a time, dependents before what they depend on, each verified before the next.
- After Start, the toast says how many are ready and which member was slowest; the workspace row tooltip shows the start order.
- An app can be in several workspaces. Renaming an app changes nothing in them, and removing an app removes it (and its dependencies) from them.

**From groups (schema 1–2):** each old group becomes a workspace where every member depends on the one before it, so it starts in exactly the old order.

## Health checks

| App field | Default | Meaning |
| --- | --- | --- |
| `health` | — | A path (`/healthz`, checked at `http://localhost:{port}`) or a full URL on **this machine** (`localhost`, `*.localhost`, `127.x.x.x`, `[::1]`). Anything else is rejected, and turned off on load |
| `healthIntervalSec` | `10` | Seconds between checks while the app is running (2–3600) |
| `healthTimeoutMs` | `3000` | Per-check timeout (200–60000) |
| `openOnReady` | `false` | Open the app's URL in the browser once a Start from the dock becomes ready |

**How checks work:**
- A `GET` that answers 2xx or 3xx is healthy. Redirects are not followed, and no proxy is used.
- `localhost` is tried on IPv4 and IPv6 at the same time, so a server bound to only one of them doesn't cost a slow fallback.
- The last 120 results are kept for latency and availability.

**What it changes:**
- **Ready** (for Start, Restart and workspaces) means healthy, not just listening.
- **Degraded:** a running app whose last two checks failed is shown as degraded (orange, with the status) and triggers a toast. Another toast says when it recovers.

## Session restore

`restoreSession` (top level): `"ask"` (default), `"auto"` or `"off"`.

The dock remembers which apps are running (`session.json` next to `apps.json`). When it starts after a reboot or a crash, the apps that were running but are now stopped are offered back. It never stops anything, never touches apps that are already up, and starts them using the dependencies your workspaces declare between them.
- **Ask:** a toast; click it to pick which to start. Unverified start commands are unticked.
- **Auto:** starts them without asking, skipping unverified start commands.
- **Tray menu → *Restore last session…*** works anytime there's something to restore.
## How Start works

`start` is either a **path to a file** or **any command line**.

| `start` value | Runs as |
| --- | --- |
| Path to an existing `.cmd` / `.bat` | `cmd.exe /d /s /c ""<path>""`. Your launcher runs exactly as if you'd double-clicked it |
| Path to an existing `.ps1` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<path>"` |
| Path to an existing `.exe` | The exe itself |
| Anything else (`npm run dev`, `python -m http.server 8000`, …) | `cmd.exe /d /s /c "<command>"` (`/s`: only the outermost quotes are removed, so quoting is predictable) |

Bare file paths may contain spaces. With arguments, quote the file path (`"C:\path\tool.exe" --flag`). Existing `.exe`/`.ps1` files run directly with those arguments and default to their own folder; `.cmd`/`.bat` use cmd's outer quoting rules. Relative file paths resolve against explicit `cwd`. Missing explicit working directories fail with a diagnostic.

The process starts in its own console window (minimized by default), in `cwd`, or in the launcher's own folder if `cwd` isn't set. That matches launchers that do `cd /d "%~dp0"`.

With `captureLogs`, the same command runs without a window. Its stdout and stderr go straight to the log file through inherited Windows handles (no shell redirection), and stdin is empty, so `pause` returns immediately. Logs rotate at 5 MB (`.1.log` … `.3.log` are kept). The app keeps logging even if the dock exits.

**Start is refused when:**
- the app is already running or already starting (no duplicate launches);
- another program holds the port. The message names it; free the port first.

After Start, the dot turns amber until the app's own process listens on the primary `port`, for up to `startTimeoutSec`. If the start command exits with an error code first, the start fails right away with that code.

> **Tip:** if your launcher ends with `pause` on error, keep `window` as `minimized` or `normal` so you can see the message. With `"hidden"` it would wait forever; with `captureLogs` it doesn't wait at all.

## How Stop works

1. If `stop` is set, it runs (minimized) and the dock waits up to 20 s for it. A non-zero exit code is reported.
2. If the app's ports are free within 5 s after that, you're done.
3. Otherwise, each process still listening on `port`/`extraPorts` is checked for [ownership](#ownership):
   - **The app's own** (started by the dock, or running from the app's folder): it's stopped according to `stopPolicy`. That means a graceful request (window close and Ctrl+C), then a force-stop of it *and its child processes* after `gracefulTimeoutSec`. Identity (PID + creation time + executable) is re-checked right before each process is stopped.
   - **Unverified or someone else's**: you're shown what it is and why it doesn't look like this app, and it's stopped only if you say **Yes** (the default is No). Otherwise it's left running and Stop reports that.
4. The console the dock itself started for the app is closed (same rules).
5. Stop succeeds only if the ports are actually free afterwards.

**Restart** is Stop, then Start, and only if the stop completed. Only one Start/Stop/Restart runs per app at a time.

Protected processes (Windows system processes, services, Localhost Dock itself and its parent) are never stopped.

## Ownership

How the dock decides a listening process belongs to an app:

| Evidence | Ownership | Stop without asking? |
| --- | --- | --- |
| A verified descendant of the process the dock launched for the app (parent chain checked by creation time) | **owned** | yes |
| Its working directory, executable or interpreter entry script is inside the app's folder (directory-bounded; arbitrary command arguments do not count) | **matched** | yes |
| Its details can't be read (elevated / other user), or the app has no folder to compare | **unverified** | asks |
| Readable, but nothing points to the app's folder | **foreign** → the app shows a *port conflict* | asks |

Tip: give apps a `cwd` (or a launcher path) so the dock can verify them. An app with only `"start": "npm run dev"` and no `cwd` will always be *unverified*.

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
LocalhostDock.exe [--show] [--add <launcher-or-folder>] [--exit] [--selftest]
```

Only one dock runs per Windows user. Launching it again hands the arguments to the running instance:
- `--show` opens the dock.
- `--add` opens the Add flow for that path. It must be an existing, fully qualified file or folder.
- `--exit` closes the running dock cleanly; the installer uses it.

`--selftest` doesn't start the dock. It checks that this build can run on this machine and exits with 0, and the installer runs it before replacing a working installation.

The hand-off uses a named pipe that only the same Windows user can connect to. Messages are size-limited, and anything other than these arguments is ignored.
