# Aura status integration

Dock provides an optional, authenticated, read-only HTTP status interface for Aura's **Node server on this PC**. It is disabled by default and exposes no lifecycle or command execution routes. Services remain controlled through Dock's UI. The interface reuses the running tray controller's status, including process identities and exit diagnostics.

## Enable and connect

1. Choose **Tray → Settings → Aura: read-only localhost status**. Alternatively add `"aura": { "enabled": true, "port": 0 }` to `apps.json`. Zero selects a free port; a fixed port may also be configured. Reloading applies changes without restarting any projects.
2. Dock writes `%APPDATA%\LocalhostDock\aura-endpoint.json` with the loopback URL and a random bearer token. `LOCALHOST_DOCK_HOME` overrides the folder for isolated installations. This file inherits the folder's permissions: use your private Windows profile, not a shared/network folder. Other programs running as your Windows user can read it; this is not isolation from a compromised local account.
3. Verify from the repository: `node integrations/aura/localhost-dock.mjs`. This prints status only. Import the adapter from Aura's server code:

```js
import { readLocalhostDockStatus } from '/path/to/localhost-dock/integrations/aura/localhost-dock.mjs';

// Use inside Aura's existing owner-authorized server route or briefing builder.
const dock = await readLocalhostDockStatus();
// dock.services: id, name, state, ready, port, listeners, uptimeSec,
// memoryBytes, healthy, httpStatus, latencyMs, diagnostic, exitCode, recoveryRequired
// dock.workspaces: id, name, member IDs, dependencies, ready count, total
```

Never import this module into browser code or return discovery credentials to a client. The standalone ESM adapter has no package dependencies or subprocess calls. It reads fresh credentials each time, bypasses proxy environment settings using direct Node HTTP, and rejects redirects, remote URLs, stale data, incompatible schemas, excessive responses and deadlines. Aura should display **unavailable** when reads fail. Any Aura route must retain its existing owner access controls; do not expose local service inventory to LAN clients implicitly.

This repository includes and tests the producer and consumer adapter. It does not modify or deploy the separate Aura checkout, and does not enable integration in the installed user's registry.

## HTTP contract

Only `GET /v1/status` over HTTP/1.1 is supported. Dock binds exclusively to **127.0.0.1**, with no wildcard, LAN or IPv6 binding. Send `Host: 127.0.0.1:<port>` and `Authorization: Bearer <token>` from discovery. The status version is `localhost-dock/aura-status@1`; discovery uses `localhost-dock/aura-endpoint@1`.

- `200`: cached successful Dock poll, with UTC `generatedAt`.
- `401`: missing/invalid credentials.
- `403`: wrong Host, browser Origin, transfer encoding, or request body.
- `404`: other paths, including query strings or proposed control routes.
- `405`: methods other than GET, including POST and OPTIONS.
- `431`: headers exceed 8 KiB.
- `503`: no snapshot, failed poll, or expired status. The server expires status after at least 15 seconds (or three poll intervals). The adapter conservatively rejects status older than 15 seconds.

Responses use `Cache-Control: no-store`, close the connection, and supply no CORS headers. Eight concurrent requests and three seconds per connection bound resource use. Requests never trigger polling or health probes. Each endpoint restart rotates the 256-bit token. Disable/exit closes the socket and removes discovery. After a crash, a leftover file does not prove availability; the next enable replaces its credentials.

The allowlist excludes commands, folders, URLs, environment, logs, raw errors and unregistered discovered processes. `diagnostic` is a stable reason code; detailed text stays in Dock. Display names are owner-provided, so keep secrets out of names.

`ready` requires verified primary-port ownership and successful configured health. `up` can still describe an unverified listener; use `ready` for dependency readiness. `memoryBytes` sums verified listening processes' working sets, not full process trees or peak memory. `uptimeSec` uses their earliest process creation time, not the TCP socket's age. `healthy` is null until a configured check completes. `recoveryRequired` reports observed surviving launch processes; Aura can report this but cannot act on it.

## Project presets

Choose **+ Add → Import workspace presets…** and select `presets/nabh-projects.example.json` from the installed folder or repository. Review commands, folders, ports and health URLs, then tick desired workspaces:

- **Local tools:** Aura (4780, `/api/health`) and AssetLab (8765, `/api/status`).
- **Vyora development:** web app (3001) and marketing site (3000).
- **NNPlays development:** existing preview command (8088).

Paths and commands were checked against local sources on 2026-11-03. Adjust a copy if projects move. Import adds missing entries, reuses matching command/folder/port entries and skips existing workspace names. It never replaces `apps.json`, overwrites existing services, enables integration, changes Windows startup or starts anything. Imported starts require first-use confirmation and are excluded from automatic session restore. Stop scripts are not imported; Dock uses ownership-checked stops.

Independent projects have no invented dependencies. Use the workspace editor to declare actual dependencies. Start waits for verified ownership and health. Stop runs dependents first and retains prerequisites if a dependent cannot stop.

## Recovery and diagnostics

While a launcher lives, Dock observes and records verified descendant identities. After its parent exits, even a worker with no port can be shown as **surviving processes; Stop to recover**. Duplicate starts are refused. Choose Stop or Restart to recover; every termination revalidates PID and creation time. Failed/graceful-only stops retain records for retry. Detection never triggers termination or restart.

After Dock itself restarts, it can match listeners by working directory, executable or entry script; session restore offers down apps. A portless orphan that Dock never observed cannot safely be identified and stays untouched. Inspect it manually; do not infer ownership from a reused PID/port.

Start preflight checks secondary ports and split IPv4/IPv6 bindings. Foreign/unreadable owners block launch. Stop scripts are skipped when a configured port has an unverified owner. Ownership matching rejects sibling directory prefixes and project paths in unrelated command arguments. TCP table read failures are unknown-status errors, never free-port results.

Ctrl+C is sent only when every process attached to the target console belongs to the verified tree. A shared terminal containing unrelated processes is not signaled; the app's configured stop policy still applies to its own verified processes.

## Validation

```powershell
dotnet test -c Release
node --test integrations/aura/localhost-dock.test.mjs
```

Windows tests exercise the real endpoint with its Node adapter, credentials, stale status, crashes, port reuse, ownership, orphan recovery, readiness, dependency stops, relative/quoted launchers, startup command quoting and off-screen UI layout/navigation. Adapter tests cover redirects, remote endpoints, response bounds and deadlines. Tests isolate registries and only stop their own processes. They do not start preset projects or change real Windows sign-in settings.
