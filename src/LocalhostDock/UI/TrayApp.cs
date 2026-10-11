using System.Diagnostics;
using LocalhostDock.Config;
using LocalhostDock.Core;
using Registry = LocalhostDock.Config.Registry;

namespace LocalhostDock.UI;

/// <summary>Tray icon, poll loop and the popup dock.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "LocalhostDock";

    private readonly AppController _controller;
    private readonly NotifyIcon _tray;
    private readonly DockForm _dock;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _autostart;
    private bool _polling;
    private Color _iconColor = Color.Empty;

    public TrayApp(bool showOnStart = false)
    {
        Registry registry;
        try { registry = Registry.Load(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read {Registry.FilePath}:\n\n{ex.Message}\n\nStarting with an empty registry.",
                "Localhost Dock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            registry = new Registry();
        }
        _controller = new AppController(registry);
        _dock = new DockForm(_controller, () => PollNow());

        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Checked = IsAutostart() };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dock", null, (_, _) => _dock.ShowNearTray());
        menu.Items.Add("Edit apps.json", null, (_, _) => OpenRegistryFile());
        menu.Items.Add("Reload apps.json", null, (_, _) => ReloadRegistry());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Text = "Localhost Dock",
            Icon = Theme.TrayIcon(Theme.Unknown),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _dock.Toggle(); };

        _timer = new System.Windows.Forms.Timer { Interval = _controller.Registry.PollMs };
        _timer.Tick += (_, _) => PollNow();
        _timer.Start();
        PollNow();
        if (showOnStart) _ = ShowWhenReady();
    }

    private async Task ShowWhenReady()
    {
        while (_dock.IsEmpty) await Task.Delay(100);
        _dock.ShowNearTray();
    }

    private async void PollNow()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var snap = await Task.Run(_controller.Poll);
            UpdateTray(snap);
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

        string text = $"Localhost Dock — {up}/{tracked.Count} up";
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
            _dock.ForceRebuild();
            PollNow();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"apps.json has an error, keeping the previous registry:\n\n{ex.Message}",
                "Localhost Dock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool IsAutostart()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private void ToggleAutostart()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsAutostart()) key.DeleteValue(RunValue, false);
        else key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"", Microsoft.Win32.RegistryValueKind.String);
        _autostart.Checked = IsAutostart();
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _dock.Dispose();
        base.ExitThreadCore();
    }
}
