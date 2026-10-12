using System.Diagnostics;
using LocalhostDock.Core;
using LocalhostDock.Integration;
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
    private Action? _balloonAction;

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
        _tray.BalloonTipClicked += (_, _) => { _balloonAction?.Invoke(); _balloonAction = null; };

        _hotkey = new Hotkey(() => { if (_dock.Visible) _dock.Hide(); else _dock.ShowNearTray(); });
        if (_hotkey.Set(_controller.Registry.Hotkey) is { } hotkeyError)
            Notify("Shortcut not available", hotkeyError, null, ToolTipIcon.Warning);

        _timer = new System.Windows.Forms.Timer { Interval = _controller.Registry.PollMs };
        _timer.Tick += (_, _) => PollNow();
        _timer.Start();
        PollNow();
        _ = HandleArgsWhenReady(args);
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
            bool known = _previous.TryGetValue(app.Name, out var before);
            _previous[app.Name] = s.State;
            if (!known || mode == "off") continue;

            if (before == AppState.Up && s.State == AppState.Down && !_controller.StoppedByUser(app.Name))
            {
                Notify($"{app.Name} went down",
                    $":{app.Port} stopped listening and you didn't stop it." + (app.Start is not null ? " Click to restart." : ""),
                    app.Start is not null ? () => _dock.Act(app, "start") : null, ToolTipIcon.Warning);
            }
            else if (before == AppState.Starting && s.State == AppState.Down)
            {
                Notify($"{app.Name} didn't start", $"Nothing listened on :{app.Port} within 90 seconds. Click to view logs.",
                    () => LogViewer.ShowFor(app), ToolTipIcon.Warning);
            }
            else if (mode == "all" && before == AppState.Starting && s.State == AppState.Up)
            {
                Notify($"{app.Name} is up", app.EffectiveUrl is { } url ? $"{url}  (click to open)" : $":{app.Port}",
                    app.EffectiveUrl is not null ? () => _controller.Open(app) : null);
            }
        }
        foreach (var gone in _previous.Keys.Where(k => _controller.Registry.Find(k) is null).ToList()) _previous.Remove(gone);
    }

    private void Notify(string title, string text, Action? onClick, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _balloonAction = onClick;
        _tray.ShowBalloonTip(5000, title, text, icon);
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
            foreach (var g in snap.Groups)
            {
                var gi = new ToolStripMenuItem($"▣  {g.Group.Name}  ({g.Up}/{g.Members.Count})");
                gi.DropDownItems.Add(DarkMenu.Item("Start all", () => _dock.ActGroup(g.Group, "start"), g.Up < g.Members.Count));
                gi.DropDownItems.Add(DarkMenu.Item("Stop all", () => _dock.ActGroup(g.Group, "stop"), g.Up > 0));
                menu.Items.Add(gi);
            }
            foreach (var s in snap.Apps)
            {
                string mark = s.State switch { AppState.Up => "●", AppState.Down => "○", AppState.NotConfigured => "·", _ => "◐" };
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
        settings.DropDownItems.Add(DarkMenu.Item($"Shortcut: {(_controller.Registry.Hotkey is { Length: > 0 } h ? h : "none")} (edit in apps.json)", () => { }, enabled: false));
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(DarkMenu.Item("Exit", ExitThread));
    }

    private void OpenRegistryFile()
    {
        _controller.Registry.Save();
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Registry.FilePath}\"") { UseShellExecute = true });
    }

    private void ReloadRegistry()
    {
        try
        {
            _controller.Reload();
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
        _controller.Registry.Notifications = mode;
        _controller.Registry.Save();
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

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _hotkey.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _dock.Dispose();
        base.ExitThreadCore();
    }
}
