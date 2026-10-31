using System.Collections.Concurrent;
using System.Diagnostics;
using LocalhostDock.Config;
using LocalhostDock.Net;
using Microsoft.Win32.SafeHandles;

namespace LocalhostDock.Core;

public enum AppState
{
    Up,
    Down,
    Starting,
    Stopping,
    NotConfigured,
    /// <summary>The app's port is held by a process that doesn't belong to this app.</summary>
    Conflict,
}

/// <summary>How sure we are that a listening process belongs to an app.</summary>
public enum Ownership
{
    /// <summary>Started by the dock in this session (verified descendant of the launched process).</summary>
    Owned,
    /// <summary>Its working directory, command line or executable lives in the app's folder.</summary>
    Matched,
    /// <summary>Can't tell (details unreadable, or the app has no folder to compare against).</summary>
    Unverified,
    /// <summary>Evidence says it's something else.</summary>
    Foreign,
}

public sealed record OwnedListener(Listener Listener, Ownership Ownership, ProcessIdentity? Identity, string Evidence);

/// <summary>How an app last went down.</summary>
public sealed record ExitInfo(int? Code, DateTime AtUtc, bool Expected, string Process);

public sealed record AppStatus(
    AppEntry App, AppState State, IReadOnlyList<OwnedListener> Listeners, DateTime? UpSince, long MemoryBytes,
    ExitInfo? LastExit, string? LastError)
{
    /// <summary>Ownership of the process on the primary port (Unverified if several disagree).</summary>
    public Ownership? PrimaryOwnership =>
        Listeners.Where(l => l.Listener.Port == App.Port).Select(l => (Ownership?)l.Ownership).OrderBy(o => o).FirstOrDefault();

    public OwnedListener? ForeignHolder => Listeners.FirstOrDefault(l => l.Listener.Port == App.Port && l.Ownership == Ownership.Foreign);
}

public sealed record GroupStatus(GroupEntry Group, IReadOnlyList<AppStatus> Members)
{
    public int Up => Members.Count(m => m.State == AppState.Up);
    public bool Busy => Members.Any(m => m.State is AppState.Starting or AppState.Stopping);
}

/// <summary>A listening port that is not in the registry, with a best guess at what it is.</summary>
public sealed record DiscoveredPort(int Port, int Pid, string ProcessName, string? CommandLine, string? ExecutablePath, string Guess)
{
    public ProcessIdentity? Identity { get; init; }
}

public sealed record Snapshot(IReadOnlyList<AppStatus> Apps, IReadOnlyList<GroupStatus> Groups,
    IReadOnlyList<DiscoveredPort> Discovered, IReadOnlyList<Listener> AllListeners);

public sealed record OperationResult(bool Ok, string Message)
{
    public static OperationResult Success(string message = "") => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

/// <summary>
/// Asked before stopping a process that isn't verifiably part of the app. Return true to stop it anyway.
/// <paramref name="reason"/> explains why it's unverified.
/// </summary>
public delegate Task<bool> ConfirmStop(AppEntry app, ProcessDetails holder, string reason);

/// <summary>
/// Owns runtime state: polls ports, classifies who owns them, and runs Start/Stop/Restart. Each app's
/// operations are serialized (one at a time); lifecycle states are explicit; every termination goes through
/// <see cref="ProcessGuard"/>; and processes that aren't verifiably the app's are never stopped without asking.
/// </summary>
public sealed class AppController : IDisposable
{
    private static readonly HashSet<string> DevRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "python.exe", "pythonw.exe", "py.exe", "uvicorn.exe", "deno.exe", "bun.exe", "java.exe",
        "dotnet.exe", "ruby.exe", "php.exe", "go.exe", "cargo.exe", "esbuild.exe", "caddy.exe", "nginx.exe",
    };

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ops = new();
    private readonly Dictionary<string, (AppState State, DateTime Until)> _lifecycle = new();
    private readonly Dictionary<string, LaunchedProcess> _launched = new();
    private readonly Dictionary<string, DateTime> _userStopped = new();
    private readonly Dictionary<string, List<(ProcessIdentity Id, SafeProcessHandle Handle)>> _watch = new();
    private readonly Dictionary<string, ExitInfo> _lastExit = new();
    private readonly Dictionary<string, string> _lastError = new();
    private readonly HashSet<string> _verifiedStarts = new();
    private readonly HashSet<string> _startedHere = new();

    public Registry Registry { get; private set; }

    public AppController(Registry registry) => Registry = registry;

    public void Reload() => Registry = Registry.Load();

    /// <summary>Changes the registry under its lock so polling never sees a half-made change.</summary>
    public void Mutate(Action<Registry> change)
    {
        lock (Registry.SyncRoot) change(Registry);
    }

    // ================================================================ status

    public Snapshot Poll()
    {
        AppEntry[] apps;
        GroupEntry[] groups;
        HashSet<int> ignored;
        lock (Registry.SyncRoot)
        {
            apps = Registry.Apps.Select(a => a.Clone()).ToArray();
            groups = Registry.Groups.Select(g => new GroupEntry { Id = g.Id, Name = g.Name, Apps = g.Apps.ToList(), OnFailure = g.OnFailure }).ToArray();
            ignored = Registry.IgnoredPorts.ToHashSet();
        }

        var listeners = PortTable.GetListeners();
        var byPort = listeners.ToLookup(l => l.Port);
        var tree = ProcessTree.Snapshot();
        var statuses = new List<AppStatus>();

        foreach (var app in apps)
        {
            var mine = app.AllPorts.SelectMany(p => byPort[p]).Select(l => Classify(app, l, tree)).ToList();
            var primary = mine.Where(o => o.Listener.Port == app.Port).ToList();
            bool appOnPrimary = primary.Any(o => o.Ownership != Ownership.Foreign);
            AppState state = app.Port is null ? AppState.NotConfigured
                : appOnPrimary ? AppState.Up
                : primary.Count > 0 ? AppState.Conflict
                : AppState.Down;

            lock (_lock)
            {
                if (_lifecycle.TryGetValue(app.Id, out var lc))
                {
                    _launched.TryGetValue(app.Id, out var launched);
                    bool launcherFailed = lc.State == AppState.Starting && launched?.ExitCode is int code && code != 0 && !appOnPrimary;
                    bool settled = (lc.State == AppState.Starting && appOnPrimary) ||
                                   (lc.State == AppState.Stopping && primary.Count == 0) ||
                                   launcherFailed || DateTime.UtcNow > lc.Until;
                    if (settled)
                    {
                        _lifecycle.Remove(app.Id);
                        if (lc.State == AppState.Starting && !appOnPrimary)
                            _lastError[app.Id] = launcherFailed
                                ? $"The start command exited with code {launched!.ExitCode} before anything listened on :{app.Port}."
                                : $"Nothing listened on :{app.Port} within {app.StartTimeoutSec} s.";
                        if (lc.State == AppState.Starting && appOnPrimary)
                        {
                            _lastError.Remove(app.Id);
                            if (app.StartUnverified) _verifiedStarts.Add(app.Id);
                        }
                    }
                    else state = lc.State;
                }
            }

            var appProcs = mine.Where(o => o.Ownership != Ownership.Foreign && o.Identity is not null).Select(o => o.Identity!).DistinctBy(i => i.Pid).ToList();
            DateTime? since = appProcs.Count > 0 && state == AppState.Up ? appProcs.Min(i => i.CreatedUtc).ToLocalTime() : null;
            long memory = 0;
            foreach (var id in appProcs)
            {
                try
                {
                    using var p = Process.GetProcessById(id.Pid);
                    memory += p.WorkingSet64;
                }
                catch { /* exited or restricted */ }
            }

            TrackExits(app, state, appProcs);

            ExitInfo? lastExit;
            string? lastError;
            lock (_lock)
            {
                _lastExit.TryGetValue(app.Id, out lastExit);
                _lastError.TryGetValue(app.Id, out lastError);
            }
            statuses.Add(new AppStatus(app, state, mine, since, memory, lastExit, lastError));
        }

        var groupStatuses = groups
            .Select(g => new GroupStatus(g, g.Apps.Select(id => statuses.FirstOrDefault(s => s.App.Id == id)).Where(s => s is not null).Select(s => s!).ToList()))
            .ToList();

        return new Snapshot(statuses, groupStatuses, Discover(listeners, apps, ignored), listeners);
    }

    /// <summary>
    /// Keeps handles to an app's processes while it's up, so when it goes down we know their exit code and
    /// whether the user asked for it (crash detection by identity, not just "the port closed").
    /// </summary>
    private void TrackExits(AppEntry app, AppState state, List<ProcessIdentity> procs)
    {
        lock (_lock)
        {
            _watch.TryGetValue(app.Id, out var watched);
            if (state == AppState.Up)
            {
                watched ??= _watch[app.Id] = new();
                foreach (var id in procs.Where(id => watched.All(w => !w.Id.SameAs(id))))
                {
                    var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.SYNCHRONIZE, false, id.Pid);
                    if (!h.IsInvalid) watched.Add((id, h));
                    else h.Dispose();
                }
                return;
            }
            if (watched is null || watched.Count == 0 || state is AppState.Starting) return;

            // It was up and now isn't: record how it ended.
            int? code = null;
            string who = watched[0].Id.ToString();
            foreach (var (id, h) in watched)
            {
                if (Native.GetExitCodeProcess(h, out uint c) && c != 259) { code = unchecked((int)c); who = id.ToString(); }
                h.Dispose();
            }
            watched.Clear();
            bool expected = _userStopped.TryGetValue(app.Id, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(2);
            _lastExit[app.Id] = new ExitInfo(code, DateTime.UtcNow, expected, who);
        }
    }

    /// <summary>Starts that have now been seen to work (the caller clears StartUnverified and saves).</summary>
    public List<string> TakeVerifiedStarts()
    {
        lock (_lock)
        {
            var list = _verifiedStarts.ToList();
            _verifiedStarts.Clear();
            return list;
        }
    }

    /// <summary>True if the user clicked Stop/Restart on this app in the last two minutes.</summary>
    public bool StoppedByUser(string appId)
    {
        lock (_lock) return _userStopped.TryGetValue(appId, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(2);
    }

    public bool IsBusy(string appId) => _ops.TryGetValue(appId, out var s) && s.CurrentCount == 0;

    // ================================================================ ownership

    /// <summary>Decides how sure we are that the process behind <paramref name="l"/> belongs to <paramref name="app"/>.</summary>
    public OwnedListener Classify(AppEntry app, Listener l, Dictionary<int, (int Parent, string Exe)>? tree = null)
    {
        var id = ProcessIdentity.Capture(l.Pid);
        if (id is null) return new(l, Ownership.Unverified, null, "Windows doesn't allow inspecting this process.");

        LaunchedProcess? launched;
        lock (_lock) _launched.TryGetValue(app.Id, out launched);
        if (launched is not null && ProcessTree.IsDescendantOf(id, launched.Identity, tree))
            return new(l, Ownership.Owned, id, "started by Localhost Dock");

        var markers = Markers(app);
        if (markers.Count == 0) return new(l, Ownership.Unverified, id, $"{app.Name} has no folder to compare against.");

        var d = ProcessInfo.Get(l.Pid);
        string? cwd = ProcessCwd.TryGet(l.Pid);
        if (cwd is not null && markers.Any(m => IsUnder(cwd, m))) return new(l, Ownership.Matched, id, $"runs in {cwd}");
        string text = $"{d?.CommandLine} {d?.ExecutablePath ?? id.ImagePath}";
        if (markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase))) return new(l, Ownership.Matched, id, "its command line points into the app's folder");
        if (d?.CommandLine is null && cwd is null) return new(l, Ownership.Unverified, id, d?.Restriction ?? "its details aren't readable.");
        return new(l, Ownership.Foreign, id, cwd is not null ? $"it runs in {cwd}, not in the app's folder" : "nothing about it points to the app's folder");
    }

    private static List<string> Markers(AppEntry app)
    {
        var list = new List<string>();
        if (app.Folder is { } f && Path.GetPathRoot(f) != f) list.Add(f.TrimEnd('\\'));
        string? start = app.Start?.Trim().Trim('"');
        if (start is not null && File.Exists(start)) list.Add(Path.GetFullPath(start));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsUnder(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // ================================================================ discovery

    private static List<DiscoveredPort> Discover(List<Listener> listeners, AppEntry[] apps, HashSet<int> ignored)
    {
        var known = apps.SelectMany(a => a.AllPorts).ToHashSet();
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var found = new List<DiscoveredPort>();

        foreach (var l in listeners)
        {
            if (known.Contains(l.Port) || ignored.Contains(l.Port) || l.Pid <= 4 || l.Pid == Environment.ProcessId) continue;
            if (found.Any(f => f.Port == l.Port)) continue;

            var d = ProcessInfo.Get(l.Pid);
            if (d is null || d.IsCritical) continue;
            if (d.ExecutablePath is { } exe && exe.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) continue;

            bool devRuntime = DevRuntimes.Contains(d.Name);
            // Ephemeral ports are usually IDE/agent plumbing; keep them only for dev runtimes.
            if (l.Port >= 49152 && !devRuntime) continue;

            found.Add(new DiscoveredPort(l.Port, l.Pid, d.Name, d.CommandLine, d.ExecutablePath, Guess(d, l.Port)) { Identity = d.Identity });
        }
        return found;
    }

    /// <summary>Turns "node.exe ...\vite\bin\vite.js --port 5173" into something like "Vite :5173".</summary>
    public static string Guess(ProcessDetails d, int port)
    {
        string cmd = (d.CommandLine ?? "").ToLowerInvariant();
        (string Needle, string Label)[] hints =
        {
            ("vite", "Vite"), ("next", "Next.js"), ("nuxt", "Nuxt"), ("astro", "Astro"), ("remix", "Remix"),
            ("webpack", "Webpack"), ("storybook", "Storybook"), ("uvicorn", "Uvicorn"), ("gunicorn", "Gunicorn"),
            ("flask", "Flask"), ("django", "Django"), ("manage.py", "Django"), ("jupyter", "Jupyter"),
            ("http.server", "Python http.server"), ("wrangler", "Wrangler"), ("supabase", "Supabase"),
            ("ollama", "Ollama"), ("postgres", "Postgres"), ("redis", "Redis"),
        };
        foreach (var (needle, label) in hints)
            if (cmd.Contains(needle)) return $"{label} :{port}";
        return $"{Path.GetFileNameWithoutExtension(d.Name)} :{port}";
    }

    // ================================================================ actions

    public void Open(AppEntry app)
    {
        if (app.EffectiveUrl is { } url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private SemaphoreSlim Gate(AppEntry app) => _ops.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));

    private static OperationResult Busy(AppEntry app) =>
        OperationResult.Fail($"{app.Name} is already starting or stopping. Wait for that to finish.");

    public async Task<OperationResult> StartAsync(AppEntry app)
    {
        var gate = Gate(app);
        if (!await gate.WaitAsync(0)) return Busy(app);
        try { return StartCore(app); }
        finally { gate.Release(); }
    }

    public async Task<OperationResult> StopAsync(AppEntry app, ConfirmStop? confirm, CancellationToken ct = default)
    {
        var gate = Gate(app);
        if (!await gate.WaitAsync(0, ct)) return Busy(app);
        try { return await StopCoreAsync(app, confirm, ct); }
        finally { gate.Release(); }
    }

    /// <summary>Stop, verify it's really down, then Start. Never starts on top of a half-stopped app.</summary>
    public async Task<OperationResult> RestartAsync(AppEntry app, ConfirmStop? confirm, CancellationToken ct = default)
    {
        var gate = Gate(app);
        if (!await gate.WaitAsync(0, ct)) return Busy(app);
        try
        {
            var stopped = await StopCoreAsync(app, confirm, ct);
            if (!stopped.Ok) return OperationResult.Fail($"Restart cancelled because the stop didn't complete: {stopped.Message}");
            return StartCore(app);
        }
        finally { gate.Release(); }
    }

    /// <summary>Starts and waits until the app's own process listens on its port (or fails/times out).</summary>
    public async Task<OperationResult> StartAndWaitAsync(AppEntry app, CancellationToken ct = default)
    {
        var started = await StartAsync(app);
        if (!started.Ok) return started.Message.Contains("already running") ? OperationResult.Success(started.Message) : started;
        if (app.Port is not int port) return started;

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(app.StartTimeoutSec);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            var mine = PortTable.GetListeners().Where(l => l.Port == port).Select(l => Classify(app, l)).ToList();
            if (mine.Any(o => o.Ownership != Ownership.Foreign)) return OperationResult.Success($"{app.Name} is up on :{port}.");

            LaunchedProcess? launched;
            lock (_lock) _launched.TryGetValue(app.Id, out launched);
            if (launched?.ExitCode is int code && code != 0)
                return Fail(app, $"{app.Name}'s start command exited with code {code} before anything listened on :{port}.");
            await Task.Delay(400, ct);
        }
        return Fail(app, $"{app.Name} didn't start listening on :{port} within {app.StartTimeoutSec} s.");
    }

    private OperationResult StartCore(AppEntry app)
    {
        if (string.IsNullOrWhiteSpace(app.Start))
            return OperationResult.Fail($"{app.Name} has no start command. Use ⋯ → Edit… to add one.");

        // Never launch a second copy, and never start into a port someone else holds.
        foreach (int port in app.Port is int p ? new[] { p } : Array.Empty<int>())
        {
            var holders = PortTable.GetListeners().Where(l => l.Port == port).Select(l => Classify(app, l)).ToList();
            if (holders.FirstOrDefault(h => h.Ownership == Ownership.Foreign) is { } foreign && holders.All(h => h.Ownership == Ownership.Foreign))
                return OperationResult.Fail($"Port :{port} is held by {foreign.Identity?.ToString() ?? "pid " + foreign.Listener.Pid}, which isn't {app.Name} ({foreign.Evidence}). Free the port (Who's on port → Kill) or change {app.Name}'s port.");
            if (holders.Count > 0)
                return OperationResult.Fail($"{app.Name} is already running on :{port}.");
        }
        lock (_lock)
        {
            if (_lifecycle.TryGetValue(app.Id, out var lc) && lc.State == AppState.Starting &&
                _launched.TryGetValue(app.Id, out var running) && running.Identity.IsAlive())
                return OperationResult.Fail($"{app.Name} is already starting.");
        }

        LaunchedProcess? launched;
        try
        {
            launched = app.CaptureLogs
                ? Launcher.StartWithLog(app.Start, app.Cwd, Registry.CapturedLogPath(app),
                    $"{Environment.NewLine}──── {DateTime.Now:yyyy-MM-dd HH:mm:ss}  starting {app.Name}: {app.Start} ────{Environment.NewLine}")
                : Launcher.StartWithWindow(app.Start, app.Cwd, app.Window);
        }
        catch (Exception ex)
        {
            return Fail(app, $"Couldn't start {app.Name}: {ex.Message}");
        }

        lock (_lock)
        {
            if (_launched.Remove(app.Id, out var old)) old.Dispose();
            if (launched is not null) _launched[app.Id] = launched;
            _userStopped.Remove(app.Id);
            _lastError.Remove(app.Id);
            _startedHere.Add(app.Id);
            _lifecycle[app.Id] = (AppState.Starting, DateTime.UtcNow.AddSeconds(app.StartTimeoutSec));
        }
        return OperationResult.Success($"Starting {app.Name}…");
    }

    private async Task<OperationResult> StopCoreAsync(AppEntry app, ConfirmStop? confirm, CancellationToken ct)
    {
        lock (_lock)
        {
            _userStopped[app.Id] = DateTime.UtcNow;
            _lifecycle[app.Id] = (AppState.Stopping, DateTime.UtcNow.AddSeconds(app.GracefulTimeoutSec + 40));
        }
        var notes = new List<string>();
        try
        {
            // 1. The app's own stop script, if it has one.
            if (!string.IsNullOrWhiteSpace(app.Stop))
            {
                int? code;
                try { code = await Launcher.RunAndWaitAsync(app.Stop, app.Cwd, TimeSpan.FromSeconds(20)); }
                catch (Exception ex) { code = null; notes.Add($"The stop command failed to run: {ex.Message}"); }
                if (code is null) notes.Add("The stop command didn't finish within 20 s.");
                else if (code != 0) notes.Add($"The stop command exited with code {code}.");
                if (await WaitForPortsFreeAsync(app, TimeSpan.FromSeconds(5), ct))
                {
                    await StopLaunchedAsync(app, notes, ct);
                    return Done(OperationResult.Success(Join($"{app.Name} stopped.", notes)));
                }
            }

            // 2. Whatever still holds the app's ports, only if it's verifiably the app's (or you confirm).
            var tree = ProcessTree.Snapshot();
            var holders = PortTable.GetListeners().Where(l => app.AllPorts.Contains(l.Port))
                .Select(l => Classify(app, l, tree))
                .GroupBy(o => o.Listener.Pid).Select(g => g.OrderBy(o => o.Ownership).First())
                .ToList();
            foreach (var h in holders)
            {
                if (h.Identity is null)
                {
                    notes.Add($"pid {h.Listener.Pid} on :{h.Listener.Port} can't be inspected, so it was left running.");
                    continue;
                }
                bool verified = h.Ownership is Ownership.Owned or Ownership.Matched;
                if (!verified)
                {
                    var details = ProcessInfo.Get(h.Listener.Pid, fresh: true) ?? new ProcessDetails(h.Identity.Pid, h.Identity.Name, h.Identity.ImagePath, null, 0) { Identity = h.Identity };
                    string reason = h.Ownership == Ownership.Foreign
                        ? $"It doesn't look like {app.Name}: {h.Evidence}."
                        : $"Localhost Dock can't confirm it belongs to {app.Name}: {h.Evidence}";
                    if (confirm is null || !await confirm(app, details, reason))
                    {
                        notes.Add($"{h.Identity} on :{h.Listener.Port} was left running (not confirmed as {app.Name}).");
                        continue;
                    }
                }
                var r = await ProcessGuard.TerminateTreeAsync(h.Identity, app.Policy, TimeSpan.FromSeconds(app.GracefulTimeoutSec), ct);
                if (!r.Ok) notes.Add(r.Message);
            }

            // 3. The console/launcher the dock started for it.
            await StopLaunchedAsync(app, notes, ct);

            // 4. Verify.
            if (await WaitForPortsFreeAsync(app, TimeSpan.FromSeconds(5), ct))
                return Done(OperationResult.Success(Join($"{app.Name} stopped.", notes)));
            var still = PortTable.GetListeners().Where(l => app.AllPorts.Contains(l.Port)).Select(l => $":{l.Port} (pid {l.Pid})").Distinct();
            return Done(Fail(app, Join($"{app.Name} is still listening on {string.Join(", ", still)}.", notes)));
        }
        catch (OperationCanceledException)
        {
            return Done(OperationResult.Fail($"Stopping {app.Name} was cancelled."));
        }

        OperationResult Done(OperationResult r)
        {
            lock (_lock) _lifecycle.Remove(app.Id);
            return r;
        }
    }

    private async Task StopLaunchedAsync(AppEntry app, List<string> notes, CancellationToken ct)
    {
        LaunchedProcess? launched;
        lock (_lock) _launched.Remove(app.Id, out launched);
        if (launched is null) return;
        using (launched)
        {
            if (!launched.Identity.IsAlive()) return;
            var r = await ProcessGuard.TerminateTreeAsync(launched.Identity, app.Policy, TimeSpan.FromSeconds(app.GracefulTimeoutSec), ct);
            if (!r.Ok) notes.Add(r.Message);
        }
    }

    /// <summary>Terminates one specific process (Who's on port / Discovered → Kill), after the user confirmed it.</summary>
    public Task<KillResult> KillAsync(ProcessIdentity confirmed) =>
        ProcessGuard.TerminateTreeAsync(confirmed, StopPolicy.Graceful, TimeSpan.FromSeconds(5));

    // ================================================================ groups

    /// <summary>
    /// Starts members in order, waiting for each to become ready. With OnFailure = "stop" (default), a member
    /// that doesn't become ready stops the sequence so later members don't start against a missing dependency.
    /// </summary>
    public async Task<OperationResult> StartGroupAsync(GroupEntry group, Action<string>? progress = null, CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var app in Members(group))
        {
            if (string.IsNullOrWhiteSpace(app.Start)) continue;
            progress?.Invoke(app.Name);
            var r = await StartAndWaitAsync(app, ct);
            if (r.Ok) continue;
            failures.Add(r.Message);
            if (group.OnFailure != "continue")
                return OperationResult.Fail($"Start all “{group.Name}” stopped at {app.Name}: {r.Message}");
        }
        return failures.Count == 0
            ? OperationResult.Success($"“{group.Name}” is up.")
            : OperationResult.Fail($"“{group.Name}” started with problems: {string.Join(" ", failures)}");
    }

    /// <summary>Stops members one at a time in reverse start order (dependents before their dependencies).</summary>
    public async Task<OperationResult> StopGroupAsync(GroupEntry group, ConfirmStop? confirm, CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var app in Enumerable.Reverse(Members(group)))
        {
            bool running = app.AllPorts.Any(p => PortTable.GetListeners().Any(l => l.Port == p));
            bool launched;
            lock (_lock) launched = _launched.ContainsKey(app.Id);
            if (!running && !launched) continue;
            var r = await StopAsync(app, confirm, ct);
            if (!r.Ok) failures.Add(r.Message);
        }
        return failures.Count == 0
            ? OperationResult.Success($"“{group.Name}” stopped.")
            : OperationResult.Fail(string.Join(" ", failures));
    }

    private List<AppEntry> Members(GroupEntry group)
    {
        lock (Registry.SyncRoot)
            return group.Apps.Select(Registry.ById).Where(a => a is not null).Select(a => a!.Clone()).ToList();
    }

    // ================================================================ helpers

    private OperationResult Fail(AppEntry app, string message)
    {
        lock (_lock) _lastError[app.Id] = message;
        return OperationResult.Fail(message);
    }

    private static string Join(string head, List<string> notes) => notes.Count == 0 ? head : $"{head} {string.Join(" ", notes)}";

    private static async Task<bool> WaitForPortsFreeAsync(AppEntry app, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            if (!PortTable.GetListeners().Any(l => app.AllPorts.Contains(l.Port))) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(300, ct);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var l in _launched.Values) l.Dispose();
            _launched.Clear();
            foreach (var w in _watch.Values) foreach (var (_, h) in w) h.Dispose();
            _watch.Clear();
        }
    }
}
