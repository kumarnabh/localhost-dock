using System.Collections.Concurrent;
using System.Diagnostics;
using LocalhostDock.Config;
using LocalhostDock.Health;
using LocalhostDock.Net;
using LocalhostDock.Workspace;
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
    /// <summary>Listening, but its HTTP health check is failing.</summary>
    Degraded,
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
    ExitInfo? LastExit, string? LastError, HealthSnapshot? Health = null, TimeSpan? LastStartDuration = null)
{
    /// <summary>Ownership of the process on the primary port (Unverified if several disagree).</summary>
    public Ownership? PrimaryOwnership =>
        Listeners.Where(l => l.Listener.Port == App.Port).Select(l => (Ownership?)l.Ownership).OrderBy(o => o).FirstOrDefault();

    public OwnedListener? ForeignHolder => Listeners.FirstOrDefault(l => l.Listener.Port == App.Port && l.Ownership == Ownership.Foreign);
}

public sealed record WorkspaceStatus(WorkspaceEntry Workspace, IReadOnlyList<AppStatus> Members)
{
    public int Up => Members.Count(m => m.State == AppState.Up);
    public bool Busy => Members.Any(m => m.State is AppState.Starting or AppState.Stopping);
}

/// <summary>A listening port that is not in the registry, with a best guess at what it is.</summary>
public sealed record DiscoveredPort(int Port, int Pid, string ProcessName, string? CommandLine, string? ExecutablePath, string Guess)
{
    public ProcessIdentity? Identity { get; init; }
}

public sealed record Snapshot(IReadOnlyList<AppStatus> Apps, IReadOnlyList<WorkspaceStatus> Workspaces,
    IReadOnlyList<DiscoveredPort> Discovered, IReadOnlyList<Listener> AllListeners);

public sealed record OperationResult(bool Ok, string Message)
{
    /// <summary>How long a start took to become ready (StartAndWait / workspaces).</summary>
    public TimeSpan? Duration { get; init; }

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
    private readonly Dictionary<string, (AppState State, DateTime Until, DateTime Started)> _lifecycle = new();
    private readonly Dictionary<string, TimeSpan> _startDurations = new();
    private readonly HashSet<string> _openOnReady = new();
    private readonly List<AppEntry> _readyToOpen = new();
    private readonly Dictionary<string, LaunchedProcess> _launched = new();
    private readonly Dictionary<string, DateTime> _userStopped = new();
    private readonly Dictionary<string, List<(ProcessIdentity Id, SafeProcessHandle Handle)>> _watch = new();
    private readonly Dictionary<string, ExitInfo> _lastExit = new();
    private readonly Dictionary<string, string> _lastError = new();
    private readonly HashSet<string> _verifiedStarts = new();
    private readonly HashSet<string> _startedHere = new();

    public Registry Registry { get; private set; }

    /// <summary>HTTP health checks for apps that configure one.</summary>
    public HealthMonitor Health { get; } = new();

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
        WorkspaceEntry[] workspaces;
        HashSet<int> ignored;
        lock (Registry.SyncRoot)
        {
            apps = Registry.Apps.Select(a => a.Clone()).ToArray();
            workspaces = Registry.Workspaces.Select(w => w.Clone()).ToArray();
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

            // Health: checked in the background while the app's own process is listening.
            bool hasHealth = HealthUrl.Resolve(app) is not null;
            bool starting;
            lock (_lock) starting = _lifecycle.TryGetValue(app.Id, out var l0) && l0.State == AppState.Starting;
            HealthSnapshot? health = null;
            if (appOnPrimary && hasHealth)
            {
                var before = Health.Get(app.Id);
                Health.MaybeCheck(app, urgent: starting && before?.Last?.Ok != true);
                health = Health.Get(app.Id) ?? before;
            }
            else if (!appOnPrimary) Health.Reset(app.Id);

            bool ready = appOnPrimary && (!hasHealth || health?.Last?.Ok == true);
            AppState state = app.Port is null ? AppState.NotConfigured
                : appOnPrimary ? (hasHealth && health is { ConsecutiveFailures: >= 2 } ? AppState.Degraded : AppState.Up)
                : primary.Count > 0 ? AppState.Conflict
                : AppState.Down;

            lock (_lock)
            {
                if (_lifecycle.TryGetValue(app.Id, out var lc))
                {
                    _launched.TryGetValue(app.Id, out var launched);
                    bool launcherFailed = lc.State == AppState.Starting && launched?.ExitCode is int code && code != 0 && !appOnPrimary;
                    bool settled = (lc.State == AppState.Starting && ready) ||
                                   (lc.State == AppState.Stopping && primary.Count == 0) ||
                                   launcherFailed || DateTime.UtcNow > lc.Until;
                    if (settled)
                    {
                        _lifecycle.Remove(app.Id);
                        if (lc.State == AppState.Starting && !ready)
                            _lastError[app.Id] = launcherFailed
                                ? $"The start command exited with code {launched!.ExitCode} before anything listened on :{app.Port}."
                                : appOnPrimary
                                    ? $"Listening on :{app.Port}, but the health check didn't pass within {app.StartTimeoutSec} s ({health?.Describe() ?? "no answer"})."
                                    : $"Nothing listened on :{app.Port} within {app.StartTimeoutSec} s.";
                        if (lc.State == AppState.Starting && ready)
                        {
                            _lastError.Remove(app.Id);
                            _startDurations[app.Id] = DateTime.UtcNow - lc.Started;
                            if (app.StartUnverified) _verifiedStarts.Add(app.Id);
                            if (_openOnReady.Remove(app.Id)) _readyToOpen.Add(app);
                        }
                    }
                    else state = lc.State;
                }
            }

            var appProcs = mine.Where(o => o.Ownership != Ownership.Foreign && o.Identity is not null).Select(o => o.Identity!).DistinctBy(i => i.Pid).ToList();
            DateTime? since = appProcs.Count > 0 && state is AppState.Up or AppState.Degraded ? appProcs.Min(i => i.CreatedUtc).ToLocalTime() : null;
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

            TrackExits(app, state is AppState.Degraded ? AppState.Up : state, appProcs);

            ExitInfo? lastExit;
            string? lastError;
            TimeSpan? duration;
            lock (_lock)
            {
                _lastExit.TryGetValue(app.Id, out lastExit);
                _lastError.TryGetValue(app.Id, out lastError);
                duration = _startDurations.TryGetValue(app.Id, out var d) ? d : null;
            }
            statuses.Add(new AppStatus(app, state, mine, since, memory, lastExit, lastError, health, duration));
        }

        var workspaceStatuses = workspaces
            .Select(w => new WorkspaceStatus(w, w.Apps.Select(id => statuses.FirstOrDefault(s => s.App.Id == id)).Where(s => s is not null).Select(s => s!).ToList()))
            .ToList();

        return new Snapshot(statuses, workspaceStatuses, Discover(listeners, apps, ignored), listeners);
    }

    /// <summary>Apps that became ready after a dock Start and want their URL opened (the UI opens them).</summary>
    public List<AppEntry> TakeReadyToOpen()
    {
        lock (_lock)
        {
            var list = _readyToOpen.ToList();
            _readyToOpen.Clear();
            return list;
        }
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
        var sw = Stopwatch.StartNew();
        var started = await StartAsync(app);
        // "Already running" still has to prove it's ready (e.g. healthy) before dependents may start.
        if (!started.Ok && !started.Message.Contains("already running")) return started;
        if (app.Port is not int port) return started;

        bool hasHealth = HealthUrl.Resolve(app) is not null;
        HealthSample? lastHealth = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(app.StartTimeoutSec);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            var mine = PortTable.GetListeners().Where(l => l.Port == port).Select(l => Classify(app, l)).ToList();
            if (mine.Any(o => o.Ownership != Ownership.Foreign))
            {
                if (!hasHealth) return Ready("up");
                lastHealth = await Health.CheckNowAsync(app, ct);
                if (lastHealth.Ok) return Ready($"healthy ({lastHealth.LatencyMs:0} ms)");
            }

            LaunchedProcess? launched;
            lock (_lock) _launched.TryGetValue(app.Id, out launched);
            if (launched?.ExitCode is int code && code != 0 && !mine.Any(o => o.Ownership != Ownership.Foreign))
                return Fail(app, $"{app.Name}'s start command exited with code {code} before anything listened on :{port}.");
            await Task.Delay(hasHealth ? 1000 : 400, ct);
        }
        return lastHealth is not null
            ? Fail(app, $"{app.Name} is listening on :{port}, but its health check didn't pass within {app.StartTimeoutSec} s ({(lastHealth.Status is int s ? "HTTP " + s : lastHealth.Error)}).")
            : Fail(app, $"{app.Name} didn't start listening on :{port} within {app.StartTimeoutSec} s.");

        OperationResult Ready(string how)
        {
            lock (_lock) _startDurations[app.Id] = sw.Elapsed;
            return new OperationResult(true, $"{app.Name} is ready on :{port}, {how}, after {sw.Elapsed.TotalSeconds:0.0} s.") { Duration = sw.Elapsed };
        }
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
            _lifecycle[app.Id] = (AppState.Starting, DateTime.UtcNow.AddSeconds(app.StartTimeoutSec), DateTime.UtcNow);
            if (app.OpenOnReady) _openOnReady.Add(app.Id);
            Health.Reset(app.Id);
        }
        return OperationResult.Success($"Starting {app.Name}…");
    }

    private async Task<OperationResult> StopCoreAsync(AppEntry app, ConfirmStop? confirm, CancellationToken ct)
    {
        lock (_lock)
        {
            _userStopped[app.Id] = DateTime.UtcNow;
            _lifecycle[app.Id] = (AppState.Stopping, DateTime.UtcNow.AddSeconds(app.GracefulTimeoutSec + 40), DateTime.UtcNow);
            _openOnReady.Remove(app.Id);
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

    // ================================================================ workspaces

    /// <summary>
    /// Starts a workspace dependencies-first. A member starts as soon as everything it depends on is ready
    /// (listening and, if it has a health check, healthy), with up to MaxParallel starting at once. When a member
    /// fails, its dependents never start; with OnFailure = "stop" (default) nothing new starts at all.
    /// Each member's time-to-ready is measured, and the slowest is reported.
    /// </summary>
    public async Task<OperationResult> StartWorkspaceAsync(WorkspaceEntry ws, Action<string>? progress = null, CancellationToken ct = default)
    {
        try { WorkspacePlan.Levels(ws); }
        catch (InvalidOperationException ex) { return OperationResult.Fail(ex.Message); }

        var members = Members(ws).ToDictionary(a => a.Id);
        var pending = ws.Apps.Where(members.ContainsKey).ToList();
        var ready = new HashSet<string>();
        var failed = new Dictionary<string, string>();
        var skipped = new List<string>();
        var durations = new Dictionary<string, TimeSpan>();
        var running = new Dictionary<Task<OperationResult>, string>();
        string? haltedAt = null;

        while (true)
        {
            if (haltedAt is null)
            {
                foreach (var id in pending.ToList())
                {
                    if (running.Count >= ws.MaxParallel) break;
                    var deps = ws.DependenciesOf(id).Where(members.ContainsKey).ToList();
                    if (deps.Any(d => failed.ContainsKey(d) || skipped.Contains(d)))
                    {
                        pending.Remove(id);
                        skipped.Add(id); // its dependency failed: never start against a missing dependency
                        continue;
                    }
                    if (!deps.All(ready.Contains)) continue;

                    pending.Remove(id);
                    var app = members[id];
                    if (string.IsNullOrWhiteSpace(app.Start))
                    {
                        if (IsListeningAsApp(app)) ready.Add(id);
                        else failed[id] = $"{app.Name} has no start command and isn't running.";
                        continue;
                    }
                    progress?.Invoke(app.Name);
                    running[StartAndWaitAsync(app, ct)] = id;
                }
            }
            if (running.Count == 0) break;

            var done = await Task.WhenAny(running.Keys);
            string doneId = running[done];
            running.Remove(done);
            var r = await done;
            if (r.Ok)
            {
                ready.Add(doneId);
                if (r.Duration is { } d) durations[doneId] = d;
            }
            else
            {
                failed[doneId] = r.Message;
                if (ws.OnFailure != "continue") haltedAt ??= doneId;
            }
        }
        skipped.AddRange(pending);

        string Name(string id) => members.TryGetValue(id, out var a) ? a.Name : id;
        string slowest = durations.Count > 1
            ? $" Slowest: {Name(durations.MaxBy(kv => kv.Value).Key)} ({durations.Values.Max().TotalSeconds:0.0} s)."
            : "";
        if (failed.Count == 0 && skipped.Count == 0)
            return OperationResult.Success($"“{ws.Name}” is ready ({ready.Count} app{(ready.Count == 1 ? "" : "s")}).{slowest}");

        string head = haltedAt is not null
            ? $"Start “{ws.Name}” stopped at {Name(haltedAt)}: {failed[haltedAt]}"
            : $"“{ws.Name}” started with problems: {string.Join(" ", failed.Select(f => f.Value))}";
        string notStarted = skipped.Count > 0 ? $" Not started (a dependency failed): {string.Join(", ", skipped.Select(Name))}." : "";
        return OperationResult.Fail(head + notStarted);
    }

    /// <summary>Stops members one at a time, dependents before what they depend on.</summary>
    public async Task<OperationResult> StopWorkspaceAsync(WorkspaceEntry ws, ConfirmStop? confirm, CancellationToken ct = default)
    {
        List<string> order;
        try { order = WorkspacePlan.StopOrder(ws); }
        catch (InvalidOperationException) { order = ws.Apps.AsEnumerable().Reverse().ToList(); }

        var members = Members(ws).ToDictionary(a => a.Id);
        var failures = new List<string>();
        foreach (var id in order)
        {
            if (!members.TryGetValue(id, out var app)) continue;
            bool running = app.AllPorts.Any(p => PortTable.GetListeners().Any(l => l.Port == p));
            bool launched;
            lock (_lock) launched = _launched.ContainsKey(app.Id);
            if (!running && !launched) continue;
            var r = await StopAsync(app, confirm, ct);
            if (!r.Ok) failures.Add(r.Message);
        }
        return failures.Count == 0
            ? OperationResult.Success($"“{ws.Name}” stopped.")
            : OperationResult.Fail(string.Join(" ", failures));
    }

    private bool IsListeningAsApp(AppEntry app) =>
        app.Port is int p && PortTable.GetListeners().Where(l => l.Port == p).Select(l => Classify(app, l)).Any(o => o.Ownership != Ownership.Foreign);

    private List<AppEntry> Members(WorkspaceEntry ws)
    {
        lock (Registry.SyncRoot)
            return ws.Apps.Select(Registry.ById).Where(a => a is not null).Select(a => a!.Clone()).ToList();
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
        Health.Dispose();
    }
}
