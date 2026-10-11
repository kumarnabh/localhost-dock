using System.Diagnostics;
using LocalhostDock.Config;
using LocalhostDock.Net;

namespace LocalhostDock.Core;

public enum AppState { Up, Down, Starting, Stopping, NotConfigured }

public sealed record AppStatus(AppEntry App, AppState State, IReadOnlyList<Listener> Listeners);

/// <summary>A listening port that is not in the registry, with a best guess at what it is.</summary>
public sealed record DiscoveredPort(int Port, int Pid, string ProcessName, string? CommandLine, string? ExecutablePath, string Guess);

public sealed record Snapshot(IReadOnlyList<AppStatus> Apps, IReadOnlyList<DiscoveredPort> Discovered, IReadOnlyList<Listener> AllListeners);

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
        foreach (var app in Registry.Apps)
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
            statuses.Add(new AppStatus(app, state, mine));
        }

        return new Snapshot(statuses, Discover(listeners), listeners);
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
            throw new InvalidOperationException($"{app.Name} has no start command. Edit apps.json to add one.");

        Run(app.Start, app.Cwd, app.Window);
        SetPending(app, AppState.Starting, TimeSpan.FromSeconds(90));
    }

    public async Task StopAsync(AppEntry app)
    {
        SetPending(app, AppState.Stopping, TimeSpan.FromSeconds(30));

        if (!string.IsNullOrWhiteSpace(app.Stop))
        {
            using var p = Run(app.Stop, app.Cwd, "minimized");
            if (p is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
            }
            if (await WaitForPortsAsync(app, up: false, TimeSpan.FromSeconds(5))) return;
        }

        // No stop command, or it didn't free the ports: kill whatever owns them.
        await Task.Run(() =>
        {
            foreach (var l in PortTable.GetListeners().Where(l => app.AllPorts.Contains(l.Port)))
                ProcessInfo.KillTree(l.Pid);
        });
        await WaitForPortsAsync(app, up: false, TimeSpan.FromSeconds(10));
    }

    public async Task RestartAsync(AppEntry app)
    {
        await StopAsync(app);
        Start(app);
    }

    private async Task<bool> WaitForPortsAsync(AppEntry app, bool up, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var open = PortTable.GetListeners().Select(l => l.Port).ToHashSet();
            bool anyUp = app.AllPorts.Any(open.Contains);
            if (anyUp == up) return true;
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
    /// </summary>
    public static Process? Run(string command, string? cwd, string window)
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
