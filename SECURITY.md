# Security

Localhost Dock runs commands from your own `apps.json` and can terminate processes, so it's designed to be conservative:

- It never runs a command that isn't in your registry, and it never edits your launcher scripts.
- It never stops a process that isn't verifiably part of the app (started by the dock, or running from the app's folder) without your explicit confirmation. Every termination re-checks the process's identity (PID + creation time + executable), so a recycled PID is never hit. System processes, services, the dock itself and its parent can't be stopped.
- Killing a process from *Discovered* or *Who's on this port?* always needs confirmation.
- The single-instance pipe only accepts the same Windows user, limits message size and accepts only known arguments.
- It doesn't need admin rights, and it can't affect elevated processes.
- Health checks connect only to loopback and never follow redirects or use proxies. The optional Aura status listener also binds only to loopback, requires a rotating bearer token, rejects browser origins, and exposes an allowlist with no control routes. See [Aura security and contract](docs/AURA.md).

Treat `apps.json` like a script: anyone who can write to it can make Start run a command as you.

Treat workspace presets like scripts too: review their commands before import and first use. Keep `apps.json` and `aura-endpoint.json` in your private Windows profile. Same-user programs can read those files; the status interface is not a sandbox against a compromised local account. Never expose discovery credentials in browser bundles, LAN routes, logs or commits.

## Reporting a vulnerability

Please **don't** open a public issue. Use GitHub's private vulnerability reporting instead: the repository's **Security** tab → **Report a vulnerability**. You'll get a response within a few days.
