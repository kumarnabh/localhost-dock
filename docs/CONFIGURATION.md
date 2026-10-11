# Configuration reference

Localhost Dock reads a single file:

```
%APPDATA%\LocalhostDock\apps.json
```

Set the `LOCALHOST_DOCK_HOME` environment variable to use a different folder, for example to keep the registry in a synced folder or to try out a demo registry.

The file is created from [`apps.example.json`](../apps.example.json) on first run. You can edit it in the dock (right-click an app → **Edit…**, or **+ Add app**) or by hand (tray menu → **Edit apps.json**, then **Reload apps.json**). Edits made in the dock are saved right away. Hand edits take effect on reload, and if the file doesn't parse, the dock keeps the previous registry and shows you the error.

JSON comments (`//`, `/* */`) and trailing commas are allowed. Note that saving from the dock rewrites the file without comments.

## Top level

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `apps` | array | `[]` | Your apps, in the order they appear in the dock |
| `ignoredPorts` | number[] | `[]` | Ports never offered under *Discovered* (filled by **Ignore**) |
| `pollMs` | number | `2000` | How often to refresh status, in milliseconds (minimum 500) |

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

\* An app without a `port` still shows in the dock (grey dot, "not configured") so you can keep a placeholder. Open, Stop and Restart need a port.

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

After you click Start, the dot turns amber until the primary `port` starts listening, for up to 90 seconds.

> **Tip:** if your launcher ends with `pause` on error, keep `window` as `minimized` or `normal`. A `hidden` window that's waiting at `pause` will never finish.

## How Stop works

1. If `stop` is set, it runs the same way as `start` (always minimized) and the dock waits up to 20 s for it to exit.
2. If the app's ports are free within 5 s after that, you're done.
3. Otherwise (or if there's no `stop`), every process listening on `port` and `extraPorts` is killed along with its child processes (`taskkill /PID <pid> /T /F`).

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

**An npm script in a monorepo**

```json
{
  "name": "Web app",
  "port": 3001,
  "start": "npm run dev:web",
  "cwd": "C:\\dev\\suite"
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

## Discovery rules

A listening port appears under *Discovered* when **all** of these are true:

- it isn't any registered app's `port` or `extraPorts`, and isn't in `ignoredPorts`;
- the owning process isn't a system process (`System`, `svchost.exe`, `lsass.exe`, `services.exe`, `spoolsv.exe`, …) and its executable isn't under `C:\Windows`;
- the port is below 49152, **or** the process is a known dev runtime (`node`, `python`, `deno`, `bun`, `java`, `dotnet`, `ruby`, `php`, `go`, …). High ephemeral ports are usually editor and agent plumbing, so they're hidden unless a dev runtime owns them.

The suggested name comes from the command line (`vite` → "Vite", `uvicorn` → "Uvicorn", `next` → "Next.js", …), falling back to the process name.
