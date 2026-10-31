# Upgrade backlog: status

Status of the 110-item upgrade backlog (audited against `afa0912`) as of **v0.4.0**.
- **Batch 1 (v0.3.0)** followed the backlog's own rule, *P0 safety and correctness before new product functionality*.
- **Batch 2 (v0.4.0)** added workspaces, health/readiness, the port-conflict wizard, session restore, more framework detection and the `ldock` CLI.

**Legend:** ✅ done (with tests where testable) · 🟡 partly done (what's missing is stated) · ⬜ not started

## A. Critical bugs & safety

| # | Item | Status | Notes / acceptance evidence |
|---|---|---|---|
| 1 | Stop never kills an unrelated process on the port | ✅ | Ownership is classified (*owned* = started by the dock and a verified descendant; *matched* = working directory / command line / executable in the app's folder; *unverified*; *foreign*). Only owned/matched are stopped without asking; anything else needs explicit confirmation (default **No**). Tests: `Stop_never_kills_an_unrelated_process…`, `…without_a_confirmation_callback…`, `…only_after_explicit_confirmation` |
| 2 | Centralized critical-process protection | ✅ | `ProcessGuard` is the only code path that terminates processes (Stop, Restart, Who's-on-port Kill, Discovered Kill, test cleanup). Protects PID ≤ 4, the dock and its ancestors, critical names, and session-0 services, including processes it can't open. Tests: `Protected_processes_are_refused`, `Discovery_never_lists_system_or_service_processes` |
| 3 | Verified ownership instead of port-only status | ✅ | New `Conflict` state when a foreign process holds the port; *owner unverified* shown when it can't be told. HTTP health is item 44 |
| 4 | Sequential reverse-order group shutdown | ✅ | Test: `Group_stops_members_one_at_a_time_in_reverse_order` (non-interleaved begin/end markers) |
| 5 | Stop group startup on failed dependencies; policies | ✅ | Readiness = the member's *own* process listens (or its launcher exits non-zero → immediate failure). `onFailure: "stop"` (default) / `"continue"`. Test: `Group_start_stops_at_a_failed_dependency…` |
| 6 | Verify shutdown before restart | ✅ | Restart starts only if Stop verified the ports are free. Test: `Restart_does_not_start_when_the_stop_did_not_complete` |
| 7 | Serialize operations per app | ✅ | Per-app gate; a second Start/Stop/Restart while one runs returns "already starting or stopping". Group operations go through the same per-app gates. Test: `Concurrent_operations_on_one_app_are_serialized` |
| 8 | Don't overwrite external apps.json edits | ✅ | *Edit apps.json* no longer saves first; saves detect outside changes (hash) and refuse to overwrite; a file watcher reloads outside edits. Test: `Never_overwrites_an_edit_made_outside_the_dock` |
| 9 | Validate & normalize registry data | ✅ | Null lists, invalid/duplicate ports, duplicate names/ids, unknown enum values, dangling group members. Test: `Normalizes_bad_data_from_hand_edits` |
| 10 | Icon handle ownership / GDI leak | ✅ | HICON cloned into an owning `Icon`, original destroyed with `DestroyIcon` |

## B. Additional bug fixes

| # | Item | Status | Notes |
|---|---|---|---|
| 11 | Revalidate identity right before termination | ✅ | PID + creation time + image path, rechecked for the root and again for every child right before it's killed |
| 12 | Stale/recycled PID metadata | ✅ | Process-details cache keyed by (PID, creation time); descendants must be created after their parent. Test: `A_recycled_pid_is_never_terminated` |
| 13 | Detect PowerShell-only `.ps1` launchers | ✅ | `Start-X.ps1`, `X-Start.ps1`, `run.ps1`…; a `.cmd` wrapper is preferred over the `.ps1` it calls. Tests: `PowerShell_only_project_is_detected`, `Cmd_wrapper_is_preferred…` |
| 14 | Registry mutations vs. background polling | ✅ | All writes go through `AppController.Mutate` under the registry lock; polling works on cloned snapshots |
| 15 | Prevent duplicate launches | ✅ | Start refuses if the app is already listening, already starting, or the port is held by something else. Test: `Start_refuses_to_launch_a_second_copy` |
| 16 | Captured-log filename collisions | ✅ | `<sanitized name>.<app id>.log`. Test: `Captured_log_names_never_collide` |
| 17 | Fragile nested `cmd` redirection | ✅ | Captured output goes to the log through inherited Win32 handles (`CreateProcess` + `STARTF_USESTDHANDLES`), stdin from NUL. The command itself still runs via `cmd /d /s /c` with the documented `/s` quoting rule. Test: path with spaces, `&` and parentheses |
| 18 | Mark reconstructed start commands uncertain | ✅ | `startUnverified`; shown on the row, confirmed before first Start, cleared after a verified start |
| 19 | Discovered apps' restart command vs. actual port | 🟡 | The actual port is used and a mismatch with the scripts is flagged in the note (and the command stays *unverified*); commands aren't rewritten automatically |
| 20 | Warn when scanning hits the 4,000-folder limit | ✅ | `ScanResult.Truncated`, shown in the import dialog |
| 21 | Port detection false positives | ✅ | Strict grammar: the number must follow `port` with only separators or known fallbacks (`process.env.PORT \|\| 8088`). Rejects comparisons, ranges, durations, sizes, versions. Tests: `PortsIn_rejects_numbers_that_are_not_ports` (6 cases) |
| 22 | Preserve IPv4/IPv6 bindings | ✅ | One listener per (port, PID, address). Test: `PortTable_keeps_ipv4_and_ipv6_bindings…` |
| 23 | Notification-action mismatch | ✅ | A toast's action only runs while it's the sole "live" toast; overlapping or late clicks just open the dock |
| 24 | Staged installer upgrade with rollback | ✅ | `install.ps1`: publish to staging → `--selftest` → only then stop the old version (via `--exit`), keep it as `.previous`, swap, verify it stays up, roll back automatically otherwise; `-Rollback` restores manually. *Rollback path is not covered by an automated test* |
| 25 | Test cleanup only terminates its own processes | ✅ | `TestProcesses` tracks identities of what tests started and stops only those, via `ProcessGuard` |

## C. Reliability & operations

| # | Item | Status | Notes |
|---|---|---|---|
| 26 | Validate ports, duplicates, references on load | ✅ | Part of normalization; clashes reported as load warnings |
| 27 | Backups, corruption recovery, schema migrations | ✅ | Rotating backups (20) before every save; corrupt file kept as `apps.corrupt-*.json` and newest readable backup restored; schema 1 → 2 migration with a `pre-v2` backup; files from newer versions refused. Tests: `Recovers_from_a_corrupt_file…`, `Migrates_a_v1_file…`, `Refuses_a_file_from_a_newer_version`, `Backups_are_rotated` |
| 28 | Atomic lifecycle transitions & cancellation | 🟡 | Explicit Starting/Stopping states owned by the controller; operations accept `CancellationToken`. No *Cancel* button in the UI yet |
| 29 | Graceful before forceful termination | 🟡 | Close-window + Ctrl+C to the app's console, then force after `gracefulTimeoutSec`. Ctrl+C delivery can't be exercised from the test host (it has its own console); verified only through the force path and policy tests |
| 30 | Stop policies (never force-kill) | ✅ | `stopPolicy`: `graceful` / `graceful-only` / `force`. Test: `Graceful_only_policy_never_force_kills` |
| 31 | Exit codes, startup exceptions, failure reasons | ✅ | Launcher exit codes, start exceptions and timeouts become `lastError`; crashes record the process and exit code |
| 32 | Per-app timeouts | ✅ | `startTimeoutSec`, `gracefulTimeoutSec`, `healthIntervalSec`, `healthTimeoutMs` |
| 33 | Detached children / ancestry changes | 🟡 | Ownership falls back from the launch tree to working-directory/command-line evidence when a launcher detaches. No continuous ancestry-change monitoring |
| 34 | WMI / restricted processes / inaccessible ports | ✅ | WMI timeout + exception handling; restricted processes get a plain explanation instead of failing |
| 35 | Async App Editor detection | ✅ | Runs on a worker thread; stale results are discarded |
| 36 | Log size limits & rotation | ✅ | Rotates at 5 MB, keeps 3. Test: `Logs_rotate_and_keep_three` |
| 37 | Log encodings / high-volume output | ⬜ | |
| 38 | Lifecycle event history | ⬜ | |
| 39 | Incremental discovery rows | ⬜ | |
| 40 | Explain permission failures | 🟡 | Access-denied and protected-process messages; not yet a dedicated "why" panel |

## D. Intelligent workspace management

| # | Item | Status | Notes / acceptance evidence |
|---|---|---|---|
| 41 | Workspace profiles | ✅ | Named workspaces with members, dependencies, failure policy and parallelism; editor with live start order. Schema v3, v2 groups migrated as dependency chains. Test: `Migrates_v2_groups_to_workspace_chains…` |
| 42 | One-click workspace start/stop + session restore | ✅ | Workspace row and tray menu Start/Stop; session restore (ask / auto / off) |
| 43 | Dependency-aware startup/shutdown graphs | ✅ | Members start as soon as their dependencies are *ready*, in parallel up to `maxParallel`; dependents of a failed member never start; stop is dependents-first. Tests: `Independent_members_start_in_parallel…`, `Levels_start_dependencies_first…`, `Cycles_are_found_and_removed_on_load`, `Group_start_stops_at_a_failed_dependency…` |
| 44 | HTTP health & readiness checks | ✅ | `health` path/URL, interval, timeout; readiness = listening + healthy; degraded state. Local-only (loopback) by design. Tests: `HealthTests` (8) |
| 45 | Port-conflict resolution wizard | ✅ | Names the holder (process, folder, command line, or the other registered app). Stop it through `ProcessGuard`, stop the owning app, or move this app to a suggested free port (not listening, not reserved by Windows, not registered, not privileged/ephemeral), rewriting explicit `--port`/`-p`/`PORT=` in the start command. Buttons ignore clicks for 0.6 s after appearing. Tests: `PortAdvisorTests` (9), `Conflict_wizard_renders_and_fits` |
| 46 | More framework discovery | ✅ | + FastAPI (uvicorn, venv-aware), Flask, ASP.NET Core (launchSettings), Spring Boot (Maven/Gradle wrappers, server.port), Rails, Laravel; Next/Vite/Astro/Angular/Django from earlier. Tests: `FrameworkDetectionTests` |
| 47 | Docker / Compose | ⬜ | |
| 48 | WSL | ⬜ | |
| 49 | Workspace templates | ⬜ | |
| 50 | Restore sessions after reboot/crash | ✅ | Only apps that were running, are down now and have a start command; never stops anything; keeps workspace dependencies; unverified commands unticked (skipped in auto). The previous session is held until you answer. Test: `Session_is_saved_and_only_down_apps…` |

## E. Monitoring & observability

| # | Status | Notes |
|---|---|---|
| 51 | 🟡 | Memory and uptime per app; no CPU or per-tree breakdown |
| 52 | 🟡 | States: up, degraded, down, crashed (exit code), failed (reason), conflict, starting, stopping; no persisted history |
| 53 | ✅ | Crash detection by process identity with exit code (v0.3) |
| 54 | ⬜ | Auto-restart with back-off |
| 55, 56 | ⬜ | Log search/filters, merged timeline |
| 57 | ✅ | Health latency and availability per app (row, tooltip, CLI) |
| 58 | ⬜ | |
| 59 | 🟡 | Bounded health history (120 samples per app); no CPU/memory history |
| 60 | ✅ | Time-to-ready measured per start; a workspace start reports its slowest member. Test: `StartAndWait_waits_for_health…` |
| 61 | 🟡 | `ldock status` / `who` / `manifest` give a redacted snapshot; no one-click diagnostic report yet |

## F. Developer productivity

| # | Status | Notes |
|---|---|---|
| 63 | ✅ | `openOnReady`: opens the URL once a dock Start becomes ready (healthy if a check is configured) |
| 64 | 🟡 | Windows Terminal / cmd; not configurable |
| 65 | 🟡 | VS Code; not configurable |
| 70 | 🟡 | Secret redaction for command lines in CLI output; no environment-variable inspector |
| 71 | 🟡 | Automatic backups and corruption restore; no import/export UI |
| 72 | 🟡 | Duplicate detection by start command + working directory or folder + port |
| 75 | ✅ | Workspace start order (dependencies), concurrency (`maxParallel`) and failure handling (`onFailure`) |
| 62, 66–69, 73, 74 | ⬜ | |

## G. Desktop UI & accessibility

| # | Status | Notes |
|---|---|---|
| 77 | 🟡 | Panel sizes to content and screen; no responsive layout modes |
| 78 | ⬜ | Deliberately *system*-DPI aware (uniform scaling on other monitors) |
| 80 | 🟡 | **Fixed in v0.4:** dock buttons couldn't be activated by keyboard, screen readers or UI Automation (they were non-selectable, so `PerformClick` did nothing). Accessible names on icon buttons; no full tab navigation design yet |
| 81 | 🟡 | Status explanations in row tooltips (now with health and time-to-ready); onboarding empty state |
| 84 | 🟡 | Live start-order text in the workspace editor and row tooltip; per-member readiness marks; no graphical graph |
| 76, 79, 82, 83, 85 | ⬜ | |

## H. AI agent integration

| # | Status | Notes |
|---|---|---|
| 86 | ✅ | `ldock status / who / workspaces / manifest`, `--json`, exit codes. Read-only. Tests: `CliTests` |
| 87 | ✅ | `localhost-dock/manifest@1`: app ids, commands (redacted), cwd, ports, health, stop policy, workspace dependencies and computed start order. Documented in docs/CLI.md |
| 88 | ⬜ | MCP interface (the CLI is its read-only foundation) |
| 89 | ⬜ | Agent-triggered Start/Stop (deliberately not in the CLI) |
| 90 | 🟡 | Redacted status/manifest/who output; no bundled report with logs |
| 91, 92 | ⬜ | |
| 93 | 🟡 | Today's policy is simple: agents get read-only access only |
## I. Architecture, security & release

| # | Status | Notes |
|---|---|---|
| 94 | 🟡 | Engine split into `LocalhostDock.Core` (shared by the app, the CLI and the tests) with `ProcessGuard`, `ProcessIdentity`, `Launcher`, `Registry`, `HealthMonitor`, `WorkspacePlan`, `SessionStore`, `PortAdvisor`, `CliRunner`. Runtime state and execution still live together in `AppController` |
| 95 | ✅ | Immutable app and group ids; `schemaVersion: 2` |
| 96 | ✅ | `ProcessIdentity` + ownership classification |
| 97 | ✅ | `ProcessGuard` for every termination |
| 98 | ✅ | Per-app serialization, explicit Starting/Stopping transitions |
| 99 | ✅ | Pipe: `PipeOptions.CurrentUserOnly` (owner-only ACL, anti-squatting), 8 KB cap, read timeout, argument whitelist with path validation. Test: `Pipe_messages_keep_only_known_arguments…` |
| 100 | ⬜ | |
| 101 | ✅ | Launches pinned by identity while the handle is held; descendants verified by creation order |
| 102 | ✅ | .NET 10 (`net10.0-windows`), all tests pass |
| 103 | 🟡 | SHA-256 hashes published with releases; **not code-signed** (needs a certificate) and not proven reproducible |
| 104 | 🟡 | Safe staged installer with rollback; no winget package or update check |
| 105 | ✅ | Self-contained single-file release zip alongside the framework-dependent one |
| 106 | 🟡 | CI: dependency vulnerability gate, coverage collected and uploaded; no static-analysis gate yet |
| 107 | 🟡 | `UiSmokeTests` render the workspace editor, app editor, conflict wizard and restore dialog **off-screen** and fail on collapsed/overflowing controls (caught a real overflow in v0.4). No tray/context-menu/multi-monitor automation yet |
| 108 | ✅ | `SafetyTests` (15 tests) plus discovery/protection tests in `CoreTests` |
| 109 | 🟡 | Crash, PID reuse, port takeover, simultaneous actions, file corruption: tested. Upgrade rollback: not automated |
| 110 | 🟡 | Release gate: CI must pass build, vulnerability scan, all tests (incl. safety) and both builds' `--selftest` before a release is published. Windows 10 matrix, performance and accessibility gates not yet |
