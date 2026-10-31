using System.Diagnostics;
using LocalhostDock.Core;
using LocalhostDock.Integration;
using LocalhostDock.Workspace;
using Registry = LocalhostDock.Config.Registry;

namespace LocalhostDock.UI;

/// <summary>Tray icon and menu, poll loop, notifications, hotkey, and the popup dock.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "LocalhostDock";

    private readonly AppController _controller;
    private readonly NotifyIcon _tray;
    private readonly DockForm _dock;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Hotkey _hotkey;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<string, AppState> _previous = new(StringComparer.OrdinalIgnoreCase);
    private Snapshot? _last;
    private bool _polling;
    private Color _iconColor = Color.Empty;
    // The click action of the most recent toast, valid only while it can still be the one clicked.
    private (Action? Action, DateTime Until) _balloon;
    private FileSystemWatcher? _watcher;
    private string _sessionKey = "";
    private bool _restoreOffered;
    // The session as it was when the dock started (before anything overwrites it), and until when we hold off
    // writing a new one so an unanswered restore offer isn't lost.
    private readonly SessionFile? _previousSession = SessionStore.Load();
    private DateTime _holdSessionUntil = DateTime.UtcNow.AddMinutes(10);
    private readonly System.Windows.Forms.Timer _reloadDebounce = new() { Interval = 600 };

    public TrayApp(string[] args)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Registry registry;
        try { registry = Registry.Load(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read {Registry.FilePath}:\n\n{ex.Message}\n\nStarting with an empty registry.",
                "Localhost Dock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            registry = new Registry();
        }
        _controller = new AppController(registry);
        _dock = new DockForm(_controller, PollNow);
        _dock.Added += what => Notify("Added to your dock", $"{what}. Click the tray icon to start it.", null);
        _dock.OperationFinished += (who, message, ok) => Notify(who, message, _dock.ShowNearTray, ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        _dock.RegistryReloadRequested += ReloadRegistry;

        var menu = DarkMenu.Create();
        menu.Opening += (_, _) => BuildMenu(menu);
        _tray = new NotifyIcon
        {
            Text = "Localhost Dock",
            Icon = Theme.TrayIcon(Theme.Unknown),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _dock.Toggle(); };
        _tray.BalloonTipClicked += (_, _) => OnBalloonClicked();

        _hotkey = new Hotkey(() => { if (_dock.Visible) _dock.Hide(); else _dock.ShowNearTray(); });
        if (_hotkey.Set(_controller.Registry.Hotkey) is { } hotkeyError)
            Notify("Shortcut not available", hotkeyError, null, ToolTipIcon.Warning);

        _timer = new System.Windows.Forms.Timer { Interval = _controller.Registry.PollMs };
        _timer.Tick += (_, _) => PollNow();
        _timer.Start();
        PollNow();
        WatchRegistryFile();
        ShowLoadWarnings(registry);
        _ = HandleArgsWhenReady(args);
    }

    private void ShowLoadWarnings(Registry registry)
    {
        if (registry.LoadWarnings.Count == 0) return;
        Debug.WriteLine(string.Join(Environment.NewLine, registry.LoadWarnings));
        Notify("apps.json was repaired",
            registry.LoadWarnings.Count == 1 ? registry.LoadWarnings[0] : $"{registry.LoadWarnings[0]} (+{registry.LoadWarnings.Count - 1} more)",
            OpenRegistryFile, ToolTipIcon.Warning);
    }

    /// <summary>Reload automatically when apps.json is edited outside the dock (e.g. in Notepad).</summary>
    private void WatchRegistryFile()
    {
        try
        {
            _watcher = new FileSystemWatcher(Registry.Folder, "apps.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            _watcher.Changed += (_, _) => _ui.Post(_ => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }, null);
            _watcher.Renamed += (_, _) => _ui.Post(_ => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }, null);
            _watcher.EnableRaisingEvents = true;
            _reloadDebounce.Tick += (_, _) =>
            {
                _reloadDebounce.Stop();
                bool changed;
                lock (_controller.Registry.SyncRoot) changed = _controller.Registry.ChangedOnDisk();
                if (changed) ReloadRegistry();
            };
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    // ================================================================ args (startup + forwarded from a second launch)

    /// <summary>Handles "--show" / "--add &lt;path&gt;" (may be called from the pipe thread).</summary>
    public void HandleArgs(string[] args) => _ui.Post(_ => _ = HandleArgsWhenReady(args), null);

    private async Task HandleArgsWhenReady(string[] args)
    {
        while (_dock.IsEmpty) await Task.Delay(100);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--add", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _dock.AddFromPath(args[++i]);
                return;
            }
            if (args[i].Equals("--exit", StringComparison.OrdinalIgnoreCase))
            {
                ExitThread();
                return;
            }
            if (args[i].Equals("--show", StringComparison.OrdinalIgnoreCase))
            {
                _dock.ShowNearTray();
                return;
            }
        }
    }

    // ================================================================ polling

    private async void PollNow()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var snap = await Task.Run(_controller.Poll);
            _last = snap;
            UpdateTray(snap);
            NotifyTransitions(snap);
            RecordVerifiedStarts();
            foreach (var app in _controller.TakeReadyToOpen()) _controller.Open(app);
            if (!_restoreOffered) { _restoreOffered = true; OfferRestore(snap); }
            SaveSession(snap);
            _dock.Render(snap);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _polling = false;
        }
    }

    private void UpdateTray(Snapshot snap)
    {
        var tracked = snap.Apps.Where(a => a.State != AppState.NotConfigured).ToList();
        int up = tracked.Count(a => a.State == AppState.Up);
        Color c = tracked.Count == 0 || up == 0 ? Theme.Unknown : up == tracked.Count ? Theme.Up : Theme.Pending;

        string text = $"Localhost Dock: {up}/{tracked.Count} up";
        if (snap.Discovered.Count > 0) text += $", {snap.Discovered.Count} unknown";
        _tray.Text = text.Length > 63 ? text[..63] : text;

        if (c != _iconColor)
        {
            var old = _tray.Icon;
            _tray.Icon = Theme.TrayIcon(c);
            _iconColor = c;
            old?.Dispose();
        }
    }

    /// <summary>Toasts for crashes (up → down without you clicking Stop), failed starts, and successful starts.</summary>
    private void NotifyTransitions(Snapshot snap)
    {
        string mode = _controller.Registry.Notifications.ToLowerInvariant();
        foreach (var s in snap.Apps)
        {
            var app = s.App;
            bool known = _previous.TryGetValue(app.Id, out var before);
            _previous[app.Id] = s.State;
            if (!known || mode == "off") continue;

            // A crash: it was up, it's down, and the process exit wasn't something you asked for.
            if (before == AppState.Up && s.State == AppState.Down && !_controller.StoppedByUser(app.Id))
            {
                string how = s.LastExit is { Code: int code } ? $"{s.LastExit.Process} exited with code {code}." : $":{app.Port} stopped listening.";
                Notify($"{app.Name} went down", $"{how} You didn't stop it." + (app.Start is not null ? " Click to restart." : ""),
                    app.Start is not null ? () => _dock.Act(app, "start") : null, ToolTipIcon.Warning);
            }
            else if (before == AppState.Starting && s.State == AppState.Down)
            {
                Notify($"{app.Name} didn't start", (s.LastError ?? $"Nothing listened on :{app.Port}.") + " Click to view logs.",
                    () => LogViewer.ShowFor(app), ToolTipIcon.Warning);
            }
            else if (before == AppState.Up && s.State == AppState.Degraded)
            {
                Notify($"{app.Name} is unhealthy", $"Its health check is failing: {s.Health?.Describe()}. It's still running.", _dock.ShowNearTray, ToolTipIcon.Warning);
            }
            else if (mode == "all" && before == AppState.Degraded && s.State == AppState.Up)
            {
                Notify($"{app.Name} is healthy again", s.Health?.Describe() ?? "", null);
            }
            else if (before is AppState.Down or AppState.Up && s.State == AppState.Conflict)
            {
                Notify($"{app.Name}: port :{app.Port} taken", $"{s.ForeignHolder?.Identity?.ToString() ?? "Another program"} is listening on it. Click for details.",
                    _dock.ShowNearTray, ToolTipIcon.Warning);
            }
            else if (mode == "all" && before == AppState.Starting && s.State == AppState.Up && !app.OpenOnReady)
            {
                string took = s.LastStartDuration is { } d ? $" (ready in {d.TotalSeconds:0.0} s)" : "";
                Notify($"{app.Name} is up{took}", app.EffectiveUrl is { } url ? $"{url}  (click to open)" : $":{app.Port}",
                    app.EffectiveUrl is not null ? () => _controller.Open(app) : null);
            }
        }
        var ids = snap.Apps.Select(a => a.App.Id).ToHashSet();
        foreach (var gone in _previous.Keys.Where(k => !ids.Contains(k)).ToList()) _previous.Remove(gone);
    }

    private void RecordVerifiedStarts()
    {
        var verified = _controller.TakeVerifiedStarts();
        if (verified.Count == 0) return;
        _controller.Mutate(r => { foreach (var id in verified) if (r.ById(id) is { } a) a.StartUnverified = false; });
        TrySave();
    }

    private void Notify(string title, string text, Action? onClick, ToolTipIcon icon = ToolTipIcon.Info)
    {
        // Windows shows one balloon at a time but keeps older ones in the notification center, and the click
        // event doesn't say which one was clicked. If another toast is still "live", the click is ambiguous,
        // so neither gets a specific action: clicking just opens the dock.
        bool overlapping = DateTime.UtcNow < _balloon.Until;
        _balloon = (overlapping ? null : onClick, DateTime.UtcNow.AddSeconds(8));
        if (text.Length > 250) text = text[..247] + "…";
        _tray.ShowBalloonTip(5000, title, text, icon);
    }

    private void OnBalloonClicked()
    {
        var (action, until) = _balloon;
        _balloon = (null, DateTime.MinValue);
        // Clicked late (from the notification center) or ambiguous: the safe default is to show the dock.
        if (action is not null && DateTime.UtcNow <= until) action();
        else _dock.ShowNearTray();
    }

    private void TrySave()
    {
        try
        {
            lock (_controller.Registry.SyncRoot) _controller.Registry.Save();
        }
        catch (Config.RegistryConflictException) { ReloadRegistry(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    // ================================================================ tray menu

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(DarkMenu.Item("Open dock", _dock.ShowNearTray));

        // Quick control without opening the dock.
        if (_last is { } snap && snap.Apps.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var w in snap.Workspaces)
            {
                var wi = new ToolStripMenuItem($"▣  {w.Workspace.Name}  ({w.Up}/{w.Members.Count})");
                wi.DropDownItems.Add(DarkMenu.Item("Start workspace", () => _dock.ActWorkspace(w.Workspace, "start"), w.Up < w.Members.Count));
                wi.DropDownItems.Add(DarkMenu.Item("Stop workspace", () => _dock.ActWorkspace(w.Workspace, "stop"), w.Members.Any(m => m.State is AppState.Up or AppState.Degraded)));
                menu.Items.Add(wi);
            }
            foreach (var s in snap.Apps)
            {
                string mark = s.State switch { AppState.Up => "●", AppState.Down => "○", AppState.NotConfigured => "·", AppState.Degraded or AppState.Conflict => "⚠", _ => "◐" };
                var item = new ToolStripMenuItem($"{mark}  {s.App.Name}" + (s.App.Port is int p ? $"   :{p}" : ""));
                if (s.State == AppState.Up) item.ForeColor = Theme.Up;
                bool up = s.State == AppState.Up, hasStart = s.App.Start is not null;
                item.DropDownItems.Add(DarkMenu.Item("Open", () => _controller.Open(s.App), s.App.EffectiveUrl is not null));
                item.DropDownItems.Add(DarkMenu.Item("Start", () => _dock.Act(s.App, "start"), hasStart && !up));
                item.DropDownItems.Add(DarkMenu.Item("Stop", () => _dock.Act(s.App, "stop"), up));
                item.DropDownItems.Add(DarkMenu.Item("Restart", () => _dock.Act(s.App, "restart"), hasStart && up));
                item.DropDownItems.Add(DarkMenu.Item("View logs", () => LogViewer.ShowFor(s.App)));
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        if (_last is { } s2 && SessionStore.Candidates(_previousSession, s2.Apps).Count > 0)
            menu.Items.Add(DarkMenu.Item("Restore last session…", () => OfferRestore(_last!, ask: true)));
        menu.Items.Add(DarkMenu.Item("Add from a launcher or folder…", _dock.PickAndAdd));
        menu.Items.Add(DarkMenu.Item("Scan a folder for apps…", () => _dock.ScanFolder()));
        menu.Items.Add(DarkMenu.Item("Edit apps.json", OpenRegistryFile));
        menu.Items.Add(DarkMenu.Item("Reload apps.json", ReloadRegistry));

        var settings = new ToolStripMenuItem("Settings");
        var autostart = DarkMenu.Item("Start with Windows", ToggleAutostart);
        autostart.Checked = IsAutostart();
        var explorer = DarkMenu.Item("Explorer right-click: “Add to Localhost Dock”", ToggleExplorer);
        explorer.Checked = ExplorerMenu.IsInstalled();
        settings.DropDownItems.Add(autostart);
        settings.DropDownItems.Add(explorer);
        settings.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (mode, label) in new[] { ("all", "Notify: crashes and starts"), ("crashes", "Notify: crashes only"), ("off", "Notify: off") })
        {
            var n = DarkMenu.Item(label, () => SetNotifications(mode));
            n.Checked = _controller.Registry.Notifications.Equals(mode, StringComparison.OrdinalIgnoreCase);
            settings.DropDownItems.Add(n);
        }
        settings.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (mode, label) in new[] { ("ask", "After a restart: offer to restore running apps"), ("auto", "After a restart: restore running apps automatically"), ("off", "After a restart: don't restore") })
        {
            var r = DarkMenu.Item(label, () => { _controller.Mutate(reg => reg.RestoreSession = mode); TrySave(); });
            r.Checked = _controller.Registry.RestoreSession == mode;
            settings.DropDownItems.Add(r);
        }
        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(DarkMenu.Item($"Shortcut: {(_controller.Registry.Hotkey is { Length: > 0 } h ? h : "none")} (edit in apps.json)", () => { }, enabled: false));
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(DarkMenu.Item("Exit", ExitThread));
    }

    private void OpenRegistryFile()
    {
        // Don't save first: that could overwrite edits made outside the dock. The dock saves its own changes as
        // they happen, and reloads automatically when the file is changed here.
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Registry.FilePath}\"") { UseShellExecute = true });
    }

    private void ReloadRegistry()
    {
        try
        {
            _controller.Reload();
            ShowLoadWarnings(_controller.Registry);
            _timer.Interval = _controller.Registry.PollMs;
            if (_hotkey.Set(_controller.Registry.Hotkey) is { } err) Notify("Shortcut not available", err, null, ToolTipIcon.Warning);
            _dock.ForceRebuild();
            PollNow();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"apps.json has an error, keeping the previous registry:\n\n{ex.Message}",
                "Localhost Dock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetNotifications(string mode)
    {
        _controller.Mutate(r => r.Notifications = mode);
        TrySave();
    }

    private static bool IsAutostart()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private static void ToggleAutostart()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsAutostart()) key.DeleteValue(RunValue, false);
        else key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"", Microsoft.Win32.RegistryValueKind.String);
    }

    private static void ToggleExplorer()
    {
        if (ExplorerMenu.IsInstalled()) ExplorerMenu.Uninstall();
        else ExplorerMenu.Install();
    }

    // ================================================================ session

    /// <summary>Remembers which apps are running (only when that set changes), so they can be restored later.</summary>
    private void SaveSession(Snapshot snap, bool cleanExit = false)
    {
        // While a restore offer is pending, keep the previous session file as it is.
        if (!cleanExit && DateTime.UtcNow < _holdSessionUntil) return;
        var running = snap.Apps.Where(a => a.State is AppState.Up or AppState.Degraded).Select(a => a.App.Id).OrderBy(x => x).ToList();
        string key = string.Join(",", running) + cleanExit;
        if (key == _sessionKey) return;
        _sessionKey = key;
        SessionStore.Save(running, cleanExit);
    }

    /// <summary>
    /// After a reboot or crash: apps that were running and are now down. Never stops anything, never restarts an
    /// app that's already up, and (in auto mode) never runs an unverified start command.
    /// </summary>
    private void OfferRestore(Snapshot snap, bool ask = false)
    {
        string mode = _controller.Registry.RestoreSession;
        if (mode == "off" && !ask) return;
        var session = _previousSession;
        var candidates = SessionStore.Candidates(session, snap.Apps);
        if (session is null || candidates.Count == 0)
        {
            _holdSessionUntil = DateTime.MinValue; // nothing to offer: start recording right away
            return;
        }
        // Remember the session as it was before this restore offer replaces it.
        var savedAt = session.SavedUtc;

        if (mode == "auto" && !ask)
        {
            var safe = candidates.Where(a => !a.StartUnverified).ToList();
            _holdSessionUntil = DateTime.MinValue;
            if (safe.Count > 0) _ = RestoreAsync(safe);
            return;
        }
        if (!ask)
        {
            Notify("Restore your last session?",
                $"{string.Join(", ", candidates.Select(c => c.Name).Take(4))}{(candidates.Count > 4 ? "…" : "")} {(candidates.Count == 1 ? "was" : "were")} running before. Click to choose.",
                () => OfferRestore(_last ?? snap, ask: true));
            return;
        }
        using var dialog = new RestoreDialog(candidates, savedAt);
        var answer = dialog.ShowDialog();
        _holdSessionUntil = DateTime.MinValue; // answered either way
        if (answer == DialogResult.OK && dialog.Selected.Count > 0) _ = RestoreAsync(dialog.Selected);
    }

    private async Task RestoreAsync(List<Config.AppEntry> apps)
    {
        List<Config.WorkspaceEntry> workspaces;
        lock (_controller.Registry.SyncRoot) workspaces = _controller.Registry.Workspaces.Select(w => w.Clone()).ToList();
        // Start them as one temporary workspace, keeping the dependencies your workspaces declare between them.
        var plan = WorkspacePlan.Combine("Last session", apps.Select(a => a.Id).ToList(), workspaces);
        var result = await _controller.StartWorkspaceAsync(plan, _ => PollNow());
        Notify(result.Ok ? "Session restored" : "Session partly restored", result.Message, _dock.ShowNearTray, result.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        PollNow();
    }

    protected override void ExitThreadCore()
    {
        if (_last is { } final) SaveSession(final, cleanExit: true);
        _timer.Stop();
        _watcher?.Dispose();
        _reloadDebounce.Dispose();
        _hotkey.Dispose();
        _controller.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _dock.Dispose();
        base.ExitThreadCore();
    }
}
