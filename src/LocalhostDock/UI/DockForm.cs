using System.Runtime.InteropServices;
using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Net;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

/// <summary>The popup panel: registered apps, discovered ports, and the port lookup.</summary>
internal sealed class DockForm : Form
{
    private static readonly int PanelWidth = S(470);
    private static readonly int RowWidth = PanelWidth - S(28);

    private readonly AppController _controller;
    private readonly Action _requestPoll;
    private readonly Label _summary;
    private readonly FlowLayoutPanel _list;
    private readonly TextBox _portBox;
    private readonly FlowLayoutPanel _lookupResult;

    private readonly Dictionary<string, AppRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private string _signature = "";
    private Snapshot? _last;
    private DateTime _hiddenAt;
    private int _modal;

    public DockForm(AppController controller, Action requestPoll)
    {
        _controller = controller;
        _requestPoll = requestPoll;

        Text = "Localhost Dock";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Width = PanelWidth;
        Height = S(420);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = S(48), BackColor = Theme.Background };
        var title = new Label { Text = "Localhost Dock", Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, Location = new Point(S(12), S(6)) };
        _summary = new Label { Text = "", Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(S(13), S(27)) };
        var headerButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Right,
            Width = S(220),
            Padding = new Padding(0, S(12), S(8), 0),
            BackColor = Theme.Background,
        };
        headerButtons.Controls.Add(Theme.Button("Refresh", (_, _) => _requestPoll()));
        headerButtons.Controls.Add(Theme.Button("+ Add app", (_, _) => AddApp(null)));
        header.Controls.AddRange(new Control[] { headerButtons, title, _summary });

        // Port lookup (bottom)
        var lookup = new Panel { Dock = DockStyle.Bottom, Height = S(116), BackColor = Theme.Surface };
        var lookupTitle = SectionLabel("WHO'S ON THIS PORT?");
        lookupTitle.Location = new Point(S(12), S(8));
        _portBox = new TextBox
        {
            Location = new Point(S(12), S(30)), Width = S(90), BackColor = Theme.Background, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "e.g. 5173",
        };
        _portBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LookupPort(); } };
        var lookupBtn = Theme.Button("Look up", (_, _) => LookupPort());
        lookupBtn.Location = new Point(S(108), S(29));
        _lookupResult = new FlowLayoutPanel
        {
            Location = new Point(S(12), S(60)), Size = new Size(RowWidth, S(52)), FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, BackColor = Theme.Surface,
        };
        lookup.Controls.AddRange(new Control[] { lookupTitle, _portBox, lookupBtn, _lookupResult });

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(S(8), 0, 0, S(8)),
            BackColor = Theme.Background,
        };

        Controls.Add(_list);
        Controls.Add(lookup);
        Controls.Add(header);

        Deactivate += (_, _) =>
        {
            if (_modal > 0) return;
            Hide();
            _hiddenAt = DateTime.UtcNow;
        };
    }

    // ---------------------------------------------------------------- show/hide

    public void Toggle()
    {
        if (Visible) { Hide(); return; }
        // A click on the tray icon first deactivates (hides) us; don't immediately re-open.
        if (DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(300)) return;
        ShowNearTray();
    }

    public void ShowNearTray()
    {
        _area = Screen.FromPoint(Cursor.Position).WorkingArea;
        FitToContent();
        Show();
        Activate();
        _requestPoll();
    }

    private Rectangle _area;

    /// <summary>Sizes the panel to its rows (up to 85% of the screen) and pins it to the tray corner.</summary>
    private void FitToContent()
    {
        if (_area.IsEmpty) _area = Screen.PrimaryScreen!.WorkingArea;
        int contentHeight = S(48) + S(116) + _list.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical) + S(24);
        Height = Math.Clamp(contentHeight, S(260), (int)(_area.Height * 0.85));
        Location = new Point(_area.Right - Width - S(12), _area.Bottom - Height - S(12));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounded corners; harmless no-op elsewhere.
        int pref = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
        // Dark scrollbars on the app list.
        SetWindowTheme(_list.Handle, "DarkMode_Explorer", null);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string appName, string? idList);

    // ---------------------------------------------------------------- rendering

    public void ForceRebuild() => _signature = "";

    public bool IsEmpty => _last is null;

    public void Render(Snapshot snap)
    {
        _last = snap;
        var tracked = snap.Apps.Where(a => a.State != AppState.NotConfigured).ToList();
        _summary.Text = $"{tracked.Count(a => a.State == AppState.Up)} of {tracked.Count} up" +
                        (snap.Discovered.Count > 0 ? $"  ·  {snap.Discovered.Count} unknown port(s)" : "");

        string sig = string.Join("|", snap.Apps.Select(a => a.App.Name)) + "#" +
                     string.Join("|", snap.Discovered.Select(d => $"{d.Port}/{d.Pid}"));
        if (sig != _signature)
        {
            Rebuild(snap);
            _signature = sig;
        }
        foreach (var s in snap.Apps)
            if (_rows.TryGetValue(s.App.Name, out var row)) row.Update(s);
    }

    private void Rebuild(Snapshot snap)
    {
        _list.SuspendLayout();
        var old = _list.Controls.Cast<Control>().ToList();
        _list.Controls.Clear();
        foreach (var c in old) c.Dispose();
        _rows.Clear();

        _list.Controls.Add(SectionLabel("APPS"));
        if (snap.Apps.Count == 0)
            _list.Controls.Add(Hint("No apps yet. Click “+ Add app” or add one from the discovered ports below."));
        foreach (var s in snap.Apps)
        {
            var row = new AppRow(s.App, this);
            _rows[s.App.Name] = row;
            _list.Controls.Add(row);
        }

        if (snap.Discovered.Count > 0)
        {
            _list.Controls.Add(SectionLabel("DISCOVERED — NOT IN YOUR REGISTRY"));
            foreach (var d in snap.Discovered) _list.Controls.Add(DiscoveredRow(d));
        }

        _list.ResumeLayout();
        if (Visible) FitToContent();
    }

    private static Label SectionLabel(string text) => new()
    {
        Text = text, Font = Theme.Header, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(S(4), S(10), 0, S(4)),
    };

    private static Label Hint(string text) => new()
    {
        Text = text, ForeColor = Theme.Muted, Font = Theme.Small, MaximumSize = new Size(RowWidth, 0), AutoSize = true,
        Margin = new Padding(S(4), S(2), 0, S(6)),
    };

    private Control DiscoveredRow(DiscoveredPort d)
    {
        var row = RowShell();
        var dot = new Dot { Color = Theme.Accent, Location = new Point(S(10), S(16)) };
        var buttons = ButtonStrip(
            Theme.Button("Add", (_, _) => AddApp(d)),
            Theme.Button("Ignore", (_, _) => Ignore(d.Port)),
            Theme.Button("Kill", (_, _) => ConfirmKill(d.Port, d.Pid), danger: true));
        var (name, sub) = RowLabels(buttons);
        name.Text = d.Guess;
        sub.Text = $"{d.ProcessName} · pid {d.Pid}";
        var tip = new ToolTip();
        tip.SetToolTip(sub, d.CommandLine ?? d.ExecutablePath ?? d.ProcessName);
        tip.SetToolTip(name, d.CommandLine ?? d.ExecutablePath ?? d.ProcessName);

        row.Controls.AddRange(new Control[] { buttons, dot, name, sub });
        return row;
    }

    /// <summary>Name + subtitle labels sized to the space left of a row's buttons.</summary>
    internal static (Label Name, Label Sub) RowLabels(Control buttons)
    {
        int width = RowWidth - S(28) - buttons.PreferredSize.Width - S(14);
        var name = new Label
        {
            Font = Theme.Bold, AutoSize = false, AutoEllipsis = true, Location = new Point(S(28), S(4)),
            Size = new Size(width, S(20)),
        };
        var sub = new Label
        {
            Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true,
            Location = new Point(S(29), S(23)), Size = new Size(width, S(17)),
        };
        return (name, sub);
    }

    internal static Panel RowShell() => new()
    {
        Width = RowWidth, Height = S(44), BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, S(4)),
    };

    internal static FlowLayoutPanel ButtonStrip(params Button[] buttons)
    {
        var strip = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Anchor = AnchorStyles.Right,
        };
        strip.Controls.AddRange(buttons);
        strip.Layout += (_, _) =>
        {
            if (strip.Parent is { } p) strip.Location = new Point(p.Width - strip.Width - S(6), (p.Height - strip.Height) / 2);
        };
        return strip;
    }

    // ---------------------------------------------------------------- actions

    internal async void Act(AppEntry app, string action)
    {
        try
        {
            switch (action)
            {
                case "open": _controller.Open(app); break;
                case "start": _controller.Start(app); break;
                case "stop": _requestPoll(); await _controller.StopAsync(app); break;
                case "restart": _requestPoll(); await _controller.RestartAsync(app); break;
            }
        }
        catch (Exception ex)
        {
            ShowModal(() => MessageBox.Show(this, ex.Message, $"{app.Name} — {action}", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
        _requestPoll();
    }

    internal void EditApp(AppEntry app)
    {
        var copy = Clone(app);
        if (ShowModal(() => new AppEditor(copy, "Edit app").ShowDialog(this)) != DialogResult.OK) return;
        int i = _controller.Registry.Apps.IndexOf(app);
        if (i >= 0) _controller.Registry.Apps[i] = copy;
        SaveAndRefresh();
    }

    internal void RemoveApp(AppEntry app)
    {
        var answer = ShowModal(() => MessageBox.Show(this,
            $"Remove {app.Name} from the registry?\n\nThis doesn't stop it or touch its files.", "Remove app",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2));
        if (answer != DialogResult.Yes) return;
        _controller.Registry.Apps.Remove(app);
        SaveAndRefresh();
    }

    private void AddApp(DiscoveredPort? from)
    {
        var app = new AppEntry();
        if (from is not null)
        {
            app.Name = from.Guess.Split(" :")[0];
            app.Port = from.Port;
            app.Start = from.CommandLine;
            if (from.ExecutablePath is null && from.CommandLine is null) app.Start = null;
        }
        if (ShowModal(() => new AppEditor(app, from is null ? "Add app" : $"Add {from.Guess}").ShowDialog(this)) != DialogResult.OK)
            return;
        if (_controller.Registry.Apps.Any(a => a.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)))
            app.Name += $" ({app.Port})";
        _controller.Registry.Apps.Add(app);
        SaveAndRefresh();
    }

    private void Ignore(int port)
    {
        if (!_controller.Registry.IgnoredPorts.Contains(port)) _controller.Registry.IgnoredPorts.Add(port);
        SaveAndRefresh();
    }

    private void SaveAndRefresh()
    {
        try { _controller.Registry.Save(); }
        catch (Exception ex)
        {
            ShowModal(() => MessageBox.Show(this, ex.Message, "Could not save apps.json", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
        ForceRebuild();
        _requestPoll();
    }

    private void LookupPort()
    {
        _lookupResult.Controls.Clear();
        if (!int.TryParse(_portBox.Text.Trim().TrimStart(':'), out int port) || port is < 1 or > 65535)
        {
            _lookupResult.Controls.Add(Hint("Enter a port number between 1 and 65535."));
            return;
        }

        var holders = PortTable.GetListeners().Where(l => l.Port == port).GroupBy(l => l.Pid).ToList();
        if (holders.Count == 0)
        {
            _lookupResult.Controls.Add(Hint(ExcludedPorts.Find(port) is { } r
                ? $"Nothing is listening, but Windows reserves :{r.Start}–{r.End} (Hyper-V/WSL/Docker), so nothing can bind :{port} either. Pick another port."
                : $":{port} is free — nothing is listening."));
            return;
        }

        foreach (var g in holders)
        {
            var d = ProcessInfo.Get(g.Key, fresh: true);
            string addrs = string.Join(", ", g.Select(l => l.Address).Distinct());
            var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.Surface };
            var lbl = new Label
            {
                Text = $"{d?.Name ?? "?"} · pid {g.Key} · {addrs}", AutoSize = true, ForeColor = Theme.Text,
                Margin = new Padding(0, S(5), S(6), 0),
            };
            new ToolTip().SetToolTip(lbl, d?.CommandLine ?? d?.ExecutablePath ?? "");
            line.Controls.Add(lbl);
            if (d is not null && !d.IsCritical)
                line.Controls.Add(Theme.Button("Kill", (_, _) => ConfirmKill(port, g.Key), danger: true));
            _lookupResult.Controls.Add(line);
        }
    }

    private void ConfirmKill(int port, int pid)
    {
        var d = ProcessInfo.Get(pid, fresh: true);
        if (d is null)
        {
            _requestPoll();
            return;
        }
        if (d.IsCritical)
        {
            ShowModal(() => MessageBox.Show(this, $"{d.Name} is a system process; Localhost Dock won't kill it.",
                "Kill", MessageBoxButtons.OK, MessageBoxIcon.Information));
            return;
        }

        string detail = d.CommandLine ?? d.ExecutablePath ?? "";
        if (detail.Length > 400) detail = detail[..400] + "…";
        var answer = ShowModal(() => MessageBox.Show(this,
            $"Kill {d.Name} (pid {pid}) and its child processes?\n\nIt is listening on :{port}.\n\n{detail}",
            "Confirm kill", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2));
        if (answer != DialogResult.Yes) return;

        string? error = ProcessInfo.KillTree(pid);
        if (error is not null)
            ShowModal(() => MessageBox.Show(this, error, "Kill failed", MessageBoxButtons.OK, MessageBoxIcon.Error));
        if (_lookupResult.Controls.Count > 0) LookupPort();
        _requestPoll();
    }

    private T ShowModal<T>(Func<T> show)
    {
        _modal++;
        try { return show(); }
        finally
        {
            _modal--;
            if (Visible) Activate();
        }
    }

    private static AppEntry Clone(AppEntry a) => new()
    {
        Name = a.Name, Port = a.Port, ExtraPorts = a.ExtraPorts.ToList(), Url = a.Url, Start = a.Start, Stop = a.Stop,
        Cwd = a.Cwd, Window = a.Window,
    };
}

/// <summary>One registered app: status dot, name, ports, and Open / Start / Stop / Restart.</summary>
internal sealed class AppRow : Panel
{
    private readonly Dot _dot = new() { Location = new Point(Theme.S(10), Theme.S(16)) };
    private readonly Label _sub;
    private readonly Button _open, _start, _stop, _restart;

    public AppRow(AppEntry app, DockForm dock)
    {
        Width = DockForm.RowShell().Width;
        Height = Theme.S(44);
        BackColor = Theme.Surface;
        Margin = new Padding(0, 0, 0, Theme.S(4));

        _open = Theme.Button("Open", (_, _) => dock.Act(app, "open"));
        _start = Theme.Button("Start", (_, _) => dock.Act(app, "start"));
        _stop = Theme.Button("Stop", (_, _) => dock.Act(app, "stop"), danger: true);
        _restart = Theme.Button("Restart", (_, _) => dock.Act(app, "restart"));
        var strip = DockForm.ButtonStrip(_open, _start, _stop, _restart);

        (var name, _sub) = DockForm.RowLabels(strip);
        name.Text = app.Name;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Edit…", null, (_, _) => dock.EditApp(app));
        menu.Items.Add("Remove", null, (_, _) => dock.RemoveApp(app));
        ContextMenuStrip = menu;
        name.ContextMenuStrip = menu;
        _sub.ContextMenuStrip = menu;

        Controls.AddRange(new Control[] { strip, _dot, name, _sub });
    }

    public void Update(AppStatus s)
    {
        var app = s.App;
        _dot.Color = Theme.For(s.State);

        string ports = app.Port is null ? "no port set" : string.Join(" ", app.AllPorts.Select(p => ":" + p));
        string state = s.State switch
        {
            AppState.Up => "up",
            AppState.Down => "down",
            AppState.Starting => "starting…",
            AppState.Stopping => "stopping…",
            _ => "not configured",
        };
        string pid = s.Listeners.Count > 0 ? $" · pid {string.Join(",", s.Listeners.Select(l => l.Pid).Distinct())}" : "";
        _sub.Text = $"{ports} · {state}{pid}";

        bool up = s.State == AppState.Up;
        bool busy = s.State is AppState.Starting or AppState.Stopping;
        bool hasStart = !string.IsNullOrWhiteSpace(app.Start);
        _open.Enabled = app.EffectiveUrl is not null;
        _start.Enabled = hasStart && !up && !busy;
        _stop.Enabled = (up || s.Listeners.Count > 0) && s.State != AppState.Stopping;
        _restart.Enabled = hasStart && up && !busy;
    }
}
