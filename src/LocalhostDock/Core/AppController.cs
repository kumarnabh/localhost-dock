using System.Diagnostics;
using LocalhostDock.Config;
using LocalhostDock.Net;

namespace LocalhostDock.Core;

public enum AppState { Up, Down, Starting, Stopping, NotConfigured }

public sealed record AppStatus(AppEntry App, AppState State, IReadOnlyList<Listener> Listeners, DateTime? UpSince, long MemoryBytes);

public sealed record GroupStatus(GroupEntry Group, IReadOnlyList<AppStatus> Members)
{
    public int Up => Members.Count(m => m.State == AppState.Up);
    public bool Busy => Members.Any(m => m.State is AppState.Starting or AppState.Stopping);
}

/// <summary>A listening port that is not in the registry, with a best guess at what it is.</summary>
public sealed record DiscoveredPort(int Port, int Pid, string ProcessName, string? CommandLine, string? ExecutablePath, string Guess);

public sealed record Snapshot(IReadOnlyList<AppStatus> Apps, IReadOnlyList<GroupStatus> Groups,
    IReadOnlyList<DiscoveredPort> Discovered, IReadOnlyList<Listener> AllListeners);

/// <summary>Owns the registry, polls port state, and starts/stops apps.</summary>
public sealed class AppController
{
    private static readonly HashSet<string> DevRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "python.exe", "pythonw.exe", "py.exe", "uvicorn.exe", "deno.exe", "bun.exe", "java.exe",
        "dotnet.exe", "ruby.exe", "php.exe", "go.exe", "cargo.exe", "esbuild.exe", "caddy.exe", "nginx.exe",
    };

    // Transitional state we set ourselves after clicking Start / Stop, keyed by app name.
    private readonly Dictionary<string, (AppState State, DateTime Until)> _pending = new(StringComparer.OrdinalIgnoreCase);
    // Launchers we started, so Stop can also clean up their console/cmd trees.
    private readonly Dictionary<string, Process> _launched = new(StringComparer.OrdinalIgnoreCase);
    // When the user last asked an app to stop (so going down isn't reported as a crash).
    private readonly Dictionary<string, DateTime> _userStopped = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public Registry Registry { get; private set; }

    public AppController(Registry registry) => Registry = registry;

    public void Reload() => Registry = Registry.Load();

    // ---------------------------------------------------------------- status

    public Snapshot Poll()
    {
        var listeners = PortTable.GetListeners();
        var byPort = listeners.ToLookup(l => l.Port);

        var statuses = new List<AppStatus>();
        foreach (var app in Registry.Apps.ToList())
        {
            var mine = app.AllPorts.SelectMany(p => byPort[p]).ToList();
            bool primaryUp = app.Port is int p && byPort[p].Any();
            AppState state = app.Port is null ? AppState.NotConfigured : primaryUp ? AppState.Up : AppState.Down;

            lock (_lock)
            {
                if (_pending.TryGetValue(app.Name, out var pend))
                {
                    bool settled = (pend.State == AppState.Starting && primaryUp) ||
                                   (pend.State == AppState.Stopping && !primaryUp) ||
                                   DateTime.UtcNow > pend.Until;
                    if (settled) _pending.Remove(app.Name);
                    else state = pend.State;
                }
            }

            DateTime? since = null;
            long memory = 0;
            foreach (int pid in mine.Select(l => l.Pid).Distinct())
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    memory += proc.WorkingSet64;
                    var started = proc.StartTime;
                    if (since is null || started < since) since = started;
                }
                catch { /* exited or access denied */ }
            }
            statuses.Add(new AppStatus(app, state, mine, primaryUp ? since : null, memory));
        }

        var groups = Registry.Groups
            .Select(g => new GroupStatus(g, g.Apps
                .Select(n => statuses.FirstOrDefault(s => s.App.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList()))
            .ToList();

        return new Snapshot(statuses, groups, Discover(listeners), listeners);
    }

    /// <summary>True if the user clicked Stop/Restart on this app in the last minute.</summary>
    public bool StoppedByUser(string name)
    {
        lock (_lock) return _userStopped.TryGetValue(name, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(1);
    }

    private List<DiscoveredPort> Discover(List<Listener> listeners)
    {
        var known = Registry.Apps.SelectMany(a => a.AllPorts).ToHashSet();
        var ignored = Registry.IgnoredPorts.ToHashSet();
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

            found.Add(new DiscoveredPort(l.Port, l.Pid, d.Name, d.CommandLine, d.ExecutablePath, Guess(d, l.Port)));
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

    // ---------------------------------------------------------------- actions

    public void Open(AppEntry app)
    {
        if (app.EffectiveUrl is { } url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void Start(AppEntry app)
    {
        if (string.IsNullOrWhiteSpace(app.Start))
            throw new InvalidOperationException($"{app.Name} has no start command. Right-click it and choose Edit… to add one.");

        string? log = null;
        if (app.CaptureLogs)
        {
            log = Registry.CapturedLogPath(app.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            try
            {
                if (File.Exists(log) && new FileInfo(log).Length > 5 * 1024 * 1024) File.Delete(log);
                File.AppendAllText(log, $"{Environment.NewLine}──── {DateTime.Now:yyyy-MM-dd HH:mm:ss}  starting {app.Name} ────{Environment.NewLine}");
            }
            catch { /* the app may still hold it; append below will tell */ }
        }

        var p = Run(app.Start, app.Cwd, app.Window, log);
        lock (_lock)
        {
            if (p is not null) _launched[app.Name] = p;
            _userStopped.Remove(app.Name);
        }
        SetPending(app, AppState.Starting, TimeSpan.FromSeconds(90));
    }

    public async Task StopAsync(AppEntry app)
    {
        lock (_lock) _userStopped[app.Name] = DateTime.UtcNow;
        SetPending(app, AppState.Stopping, TimeSpan.FromSeconds(30));

        if (!string.IsNullOrWhiteSpace(app.Stop))
        {
            using var p = Run(app.Stop, app.Cwd, "minimized", null);
            if (p is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
            }
            if (await WaitForPortsAsync(app, up: false, TimeSpan.FromSeconds(5)))
            {
                KillLaunched(app);
                return;
            }
        }

        // No stop command, or it didn't free the ports: kill whatever owns them, plus the launcher we started.
        await Task.Run(() =>
        {
            foreach (var l in PortTable.GetListeners().Where(l => app.AllPorts.Contains(l.Port)))
                ProcessInfo.KillTree(l.Pid);
            KillLaunched(app);
        });
        await WaitForPortsAsync(app, up: false, TimeSpan.FromSeconds(10));
    }

    public async Task RestartAsync(AppEntry app)
    {
        await StopAsync(app);
        Start(app);
    }

    /// <summary>Starts a group's apps in order, waiting (up to 45 s each) for each port before the next.</summary>
    public async Task StartGroupAsync(GroupEntry group, Action<string>? progress = null)
    {
        foreach (var app in Members(group))
        {
            if (string.IsNullOrWhiteSpace(app.Start) || IsUp(app)) continue;
            progress?.Invoke(app.Name);
            Start(app);
            if (app.Port is not null) await WaitForPortsAsync(app, up: true, TimeSpan.FromSeconds(45));
        }
    }

    public async Task StopGroupAsync(GroupEntry group)
    {
        // Reverse order: front ends before the APIs they depend on.
        var tasks = Members(group).Reverse().Where(a => IsUp(a) || a.AllPorts.Any(IsListening)).Select(StopAsync);
        await Task.WhenAll(tasks);
    }

    private IEnumerable<AppEntry> Members(GroupEntry group) =>
        group.Apps.Select(Registry.Find).Where(a => a is not null).Select(a => a!);

    private static bool IsListening(int port) => PortTable.GetListeners().Any(l => l.Port == port);

    private static bool IsUp(AppEntry app) => app.Port is int p && IsListening(p);

    private void KillLaunched(AppEntry app)
    {
        Process? p;
        lock (_lock) { _launched.Remove(app.Name, out p); }
        if (p is null) return;
        try
        {
            if (!p.HasExited) ProcessInfo.KillTree(p.Id);
        }
        catch { /* already gone */ }
        finally { p.Dispose(); }
    }

    private async Task<bool> WaitForPortsAsync(AppEntry app, bool up, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var open = PortTable.GetListeners().Select(l => l.Port).ToHashSet();
            bool ok = up ? app.Port is int p && open.Contains(p) : !app.AllPorts.Any(open.Contains);
            if (ok) return true;
            await Task.Delay(400);
        }
        return false;
    }

    private void SetPending(AppEntry app, AppState state, TimeSpan for_)
    {
        lock (_lock) _pending[app.Name] = (state, DateTime.UtcNow + for_);
    }

    /// <summary>
    /// Runs a command in its own console window. A bare path to an existing .cmd/.bat/.ps1/.exe is launched as
    /// that file (so your existing launchers keep working unchanged); anything else goes through cmd.exe /c.
    /// With <paramref name="logFile"/>, output is appended to that file and the window is hidden.
    /// </summary>
    public static Process? Run(string command, string? cwd, string window, string? logFile)
    {
        string trimmed = command.Trim();
        string unquoted = trimmed.Trim('"');
        string fileName, args;
        string? defaultCwd = null;

        if (File.Exists(unquoted))
        {
            defaultCwd = Path.GetDirectoryName(unquoted);
            switch (Path.GetExtension(unquoted).ToLowerInvariant())
            {
                case ".ps1":
                    fileName = "powershell.exe";
                    args = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{unquoted}\"";
                    break;
                case ".exe":
                    fileName = unquoted;
                    args = "";
                    break;
                default:
                    fileName = "cmd.exe";
                    args = $"/c \"\"{unquoted}\"\"";
                    break;
            }
        }
        else
        {
            fileName = "cmd.exe";
            args = $"/c \"{trimmed}\"";
        }

        if (logFile is not null)
        {
            // Wrap whatever we built in cmd so stdout+stderr land in the log file.
            string inner = fileName == "cmd.exe" ? args[3..] : $"\"{fileName}\" {args}";
            if (fileName == "cmd.exe" && inner.StartsWith('"') && inner.EndsWith('"')) inner = inner[1..^1];
            fileName = "cmd.exe";
            args = $"/c \"{inner} >> \"{logFile}\" 2>&1\"";
            window = "hidden";
        }

        var psi = new ProcessStartInfo(fileName, args)
        {
            UseShellExecute = true,
            WorkingDirectory = !string.IsNullOrWhiteSpace(cwd) ? cwd : defaultCwd ?? Environment.CurrentDirectory,
            WindowStyle = window.ToLowerInvariant() switch
            {
                "hidden" => ProcessWindowStyle.Hidden,
                "normal" => ProcessWindowStyle.Normal,
                _ => ProcessWindowStyle.Minimized,
            },
        };
        return Process.Start(psi);
    }
}
