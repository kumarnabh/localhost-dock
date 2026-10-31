# `ldock`: command line

`ldock.exe` ships next to `LocalhostDock.exe`. It gives scripts and AI coding agents a **read-only**, machine-readable view of your local apps:

- It never starts, stops or changes anything.
- It never writes `apps.json`, not even to migrate or repair it.
- It works whether or not the tray app is running.

```
ldock status [--json] [--no-health]   every app: state, ownership, processes, health, uptime
ldock who <port> [--json]             what's listening on a port (or whether Windows reserves it)
ldock workspaces [--json]             workspaces and their start order
ldock manifest                        versioned JSON manifest of apps and workspaces (secrets redacted)
ldock version | help
```

**Exit codes:** `0` ok, `1` error (e.g. `apps.json` missing or unreadable), `2` usage.

**Running it from anywhere:** add the install folder (`%LOCALAPPDATA%\Programs\LocalhostDock`) to your `PATH`, or call it by its full path.

## Schemas

Every JSON document carries a `schema` field such as `"localhost-dock/status@1"`. Fields are only ever *added* within a major version. A breaking change bumps the `@N`.

Enum values are kebab-case:
- **state:** `up`, `down`, `starting`, `stopping`, `not-configured`, `conflict`, `degraded`
- **ownership:** `owned`, `matched`, `unverified`, `foreign`. See [Ownership](CONFIGURATION.md#ownership).

### `ldock status --json`: `localhost-dock/status@1`

```jsonc
{
  "schema": "localhost-dock/status@1",
  "generatedAt": "2026-10-31T12:00:00Z",
  "apps": [
    {
      "id": "a1b2c3d4e5", "name": "Shop API", "state": "up",
      "port": 8000, "extraPorts": [], "url": "http://localhost:8000",
      "processes": [ { "pid": 4242, "name": "node.exe", "address": "127.0.0.1", "port": 8000, "ownership": "matched" } ],
      "uptimeSec": 5400, "memoryBytes": 81264640,
      "health": { "healthy": true, "status": 200, "latencyMs": 3.1, "consecutiveFailures": 0, "availability": 1.0 },
      "lastStartSec": 4.2,          // time from Start to ready, if started from the dock
      "lastError": null,            // why the last start failed, if it did
      "lastExitCode": null, "lastExitExpected": null,
      "startUnverified": false
    }
  ],
  "workspaces": [
    { "id": "…", "name": "Shop dev", "ready": 1, "total": 3, "apps": ["Shop API", "Shop Web", "Shop Worker"], "notReady": ["Shop Web", "Shop Worker"] }
  ]
}
```

By default, `status` runs one fresh health check for each running app that has one. The checks are local only, each with its own timeout. `--no-health` skips them and reports the last known state.

### `ldock who <port> --json`: `localhost-dock/who@1`

```jsonc
{
  "schema": "localhost-dock/who@1", "port": 5173, "free": false, "reservedByWindows": null,
  "holders": [
    { "port": 5173, "pid": 9001, "name": "node.exe", "address": "[::1]",
      "commandLine": "node …\\vite.js", "workingDirectory": "C:\\dev\\web",
      "protected": null,     // why it can't be stopped (system/service), if so
      "restriction": null }  // why details are missing (elevated / other user), if so
  ]
}
```

### `ldock manifest`: `localhost-dock/manifest@1`

This is a description of *how to run* your workspace, so an agent can explain or plan it without guessing:

```jsonc
{
  "schema": "localhost-dock/manifest@1",
  "apps": [
    { "id": "api", "name": "Shop API", "port": 8000, "extraPorts": [], "url": "http://localhost:8000",
      "start": "API_KEY=*** node server.js", "stop": null, "cwd": "C:\\dev\\shop\\api",
      "health": "/healthz", "stopPolicy": "graceful", "startTimeoutSec": 90 }
  ],
  "workspaces": [
    { "id": "shop", "name": "Shop dev", "apps": ["api", "web", "wrk"],
      "dependsOn": { "web": ["api"], "wrk": ["api"] },
      "startOrder": [["api"], ["web", "wrk"]],   // each inner list can start in parallel
      "onFailure": "stop", "maxParallel": 4 }
  ]
}
```

**Secret redaction:** values of secret-looking variables and flags in `start`/`stop` are replaced with `***`. That covers anything matching `KEY`, `TOKEN`, `SECRET`, `PASSWORD`, `PWD` or `CREDENTIAL`, as in `API_KEY=…`, `--token …` or `DB_PASSWORD=…`. Command lines in `who` are redacted the same way. Redaction is pattern-based, so don't rely on it for unusual secret formats.

## Using it from an AI agent

A typical loop:
1. Run `ldock status --json` to see what's running and healthy.
2. Run `ldock who <port> --json` when something fails to bind.
3. Run `ldock manifest` to learn how an app is started and what it depends on.

Starting and stopping stay with you in the tray app. A permission-gated control interface for agents is on the [roadmap](../ROADMAP.md).
