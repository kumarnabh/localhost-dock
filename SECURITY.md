# Security

Localhost Dock runs commands from your own `apps.json` and can terminate processes, so it's designed to be conservative:

- It never runs a command that isn't in your registry, and it never edits your launcher scripts.
- Killing a process from *Discovered* or *Who's on this port?* always needs confirmation. System processes and the dock itself can't be killed.
- It doesn't need admin rights, and it can't affect elevated processes.
- It makes no network requests, and your registry never leaves your machine.

Treat `apps.json` like a script: anyone who can write to it can make Start run a command as you.

## Reporting a vulnerability

Please **don't** open a public issue. Use GitHub's private vulnerability reporting instead: the repository's **Security** tab → **Report a vulnerability**. You'll get a response within a few days.
