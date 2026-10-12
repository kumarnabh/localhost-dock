using LocalhostDock.Config;
using LocalhostDock.Core;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

internal interface IFilterable
{
    bool Matches(string query);
}

/// <summary>Base row: rounded-ish surface with a hover highlight, dot, title, subtitle and a button strip.</summary>
internal abstract class HoverRow : Panel, IFilterable
{
    protected readonly Dot DotControl = new();
    protected readonly Label TitleLabel, SubLabel;
    protected readonly FlowLayoutPanel Strip;
    private bool _hover;

    protected HoverRow(params Button[] buttons)
    {
        Width = DockForm.RowWidth;
        Height = S(50);
        BackColor = Surface;
        Margin = new Padding(0, 0, 0, S(4));
        DoubleBuffered = true;

        Strip = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent,
        };
        Strip.Controls.AddRange(buttons);
        Strip.Location = new Point(Width - Strip.PreferredSize.Width - S(8), (Height - Strip.PreferredSize.Height) / 2);

        int textWidth = Strip.Left - S(32) - S(8);
        DotControl.Location = new Point(S(12), S(19));
        TitleLabel = new Label
        {
            Font = Bold, AutoSize = false, AutoEllipsis = true, Location = new Point(S(32), S(6)),
            Size = new Size(textWidth, S(20)), BackColor = Color.Transparent, UseMnemonic = false,
        };
        SubLabel = new Label
        {
            Font = Small, ForeColor = Muted, AutoSize = false, AutoEllipsis = true, Location = new Point(S(33), S(26)),
            Size = new Size(textWidth, S(17)), BackColor = Color.Transparent, UseMnemonic = false,
        };
        Controls.AddRange(new Control[] { Strip, DotControl, TitleLabel, SubLabel });
        WireHover(this);
    }

    public abstract bool Matches(string query);

    private void WireHover(Control c)
    {
        c.MouseEnter += (_, _) => SetHover(true);
        c.MouseLeave += (_, _) =>
        {
            // MouseLeave fires when moving onto a child; only un-hover when the cursor really left the row.
            if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) SetHover(false);
        };
        foreach (Control child in c.Controls) WireHover(child);
    }

    private void SetHover(bool on)
    {
        if (_hover == on) return;
        _hover = on;
        BackColor = on ? SurfaceHover : Surface;
        foreach (var b in Strip.Controls.OfType<Button>()) b.Invalidate();
    }

    protected void OnBodyClick(MouseEventHandler handler)
    {
        foreach (Control c in new Control[] { this, TitleLabel, SubLabel, DotControl }) c.MouseClick += handler;
    }

    protected void OnBodyDoubleClick(EventHandler handler)
    {
        foreach (Control c in new Control[] { this, TitleLabel, SubLabel, DotControl }) c.DoubleClick += handler;
    }
}

/// <summary>One registered app: status dot, name, ports/uptime/memory, and Open / Start / Stop / Restart / ⋯.</summary>
internal sealed class AppRow : HoverRow
{
    private readonly Button _open, _start, _stop, _restart;

    public AppEntry App { get; }

    public AppRow(AppEntry app, DockForm dock) : this(app, dock,
        IconButton(IconOpen, "Open in browser", (_, _) => dock.Act(app, "open")),
        IconButton(IconPlay, "Start", (_, _) => dock.Act(app, "start")),
        IconButton(IconStop, "Stop", (_, _) => dock.Act(app, "stop"), danger: true),
        IconButton(IconRestart, "Restart", (_, _) => dock.Act(app, "restart")),
        IconButton(IconMore, "More: logs, folder, VS Code, groups, edit…", (_, _) => { }))
    {
    }

    private AppRow(AppEntry app, DockForm dock, Button open, Button start, Button stop, Button restart, Button more)
        : base(open, start, stop, restart, more)
    {
        App = app;
        (_open, _start, _stop, _restart) = (open, start, stop, restart);
        TitleLabel.Text = app.Name;
        more.Click += (_, _) => dock.ShowAppMenu(app, more, new Point(0, more.Height));
        OnBodyClick((s, e) => { if (e.Button == MouseButtons.Right) dock.ShowAppMenu(app, (Control)s!, e.Location); });
        OnBodyDoubleClick((_, _) => dock.Act(app, App.EffectiveUrl is not null ? "open" : "start"));
        Tips.SetToolTip(TitleLabel, Describe(app));
    }

    public override bool Matches(string q) =>
        App.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        App.AllPorts.Any(p => p.ToString().StartsWith(q.TrimStart(':'))) ||
        (App.Cwd ?? App.Start ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);

    public void Update(AppStatus s)
    {
        var app = s.App;
        DotControl.Color = For(s.State);

        string ports = app.Port is null ? "" : string.Join(" ", app.AllPorts.Select(p => ":" + p));
        string state = s.State switch
        {
            AppState.Up => s.UpSince is { } since ? $"up {Ago(DateTime.Now - since)}" : "up",
            AppState.Down => "down",
            AppState.Starting => "starting…",
            AppState.Stopping => "stopping…",
            _ => "no port set; ⋯ → Edit to finish setup",
        };
        string mem = s.State == AppState.Up && s.MemoryBytes > 0 ? $" · {Bytes(s.MemoryBytes)}" : "";
        string text = ports.Length > 0 ? $"{ports} · {state}{mem}" : state;
        if (SubLabel.Text != text) SubLabel.Text = text;

        bool up = s.State == AppState.Up;
        bool busy = s.State is AppState.Starting or AppState.Stopping;
        bool hasStart = !string.IsNullOrWhiteSpace(app.Start);
        _open.Enabled = app.EffectiveUrl is not null;
        _start.Enabled = hasStart && !up && !busy;
        _stop.Enabled = (up || s.Listeners.Count > 0) && s.State != AppState.Stopping;
        _restart.Enabled = hasStart && up && !busy;
    }

    private static string Describe(AppEntry a) =>
        string.Join("\n", new[]
        {
            a.EffectiveUrl, a.Start is null ? null : "Start: " + a.Start, a.Stop is null ? null : "Stop: " + a.Stop,
            a.Cwd is null ? null : "In: " + a.Cwd, "Double-click to open · right-click for more",
        }.Where(x => x is not null));
}

/// <summary>A start group: combined status and Start all / Stop all.</summary>
internal sealed class GroupRow : HoverRow
{
    private readonly Button _start, _stop;
    private readonly GroupEntry _group;

    public GroupRow(GroupEntry group, DockForm dock) : this(group, dock,
        IconButton(IconPlay, "Start all (in order)", (_, _) => dock.ActGroup(group, "start")),
        IconButton(IconStop, "Stop all", (_, _) => dock.ActGroup(group, "stop"), danger: true),
        IconButton(IconMore, "More", (_, _) => { }))
    {
    }

    private GroupRow(GroupEntry group, DockForm dock, Button start, Button stop, Button more) : base(start, stop, more)
    {
        _group = group;
        (_start, _stop) = (start, stop);
        TitleLabel.Text = group.Name;
        more.Click += (_, _) => dock.ShowGroupMenu(group, more, new Point(0, more.Height));
        OnBodyClick((s, e) => { if (e.Button == MouseButtons.Right) dock.ShowGroupMenu(group, (Control)s!, e.Location); });
    }

    public override bool Matches(string q) =>
        _group.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || _group.Apps.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase));

    public void Update(GroupStatus g)
    {
        int total = g.Members.Count, up = g.Up;
        DotControl.Color = g.Busy ? Pending : up == 0 ? Down : Up;
        DotControl.Ring = up > 0 && up < total && !g.Busy;
        string text = $"{up} of {total} up · {string.Join(", ", g.Group.Apps)}";
        if (SubLabel.Text != text) SubLabel.Text = text;
        _start.Enabled = up < total && !g.Busy;
        _stop.Enabled = up > 0 && !g.Busy;
    }
}

/// <summary>A listening port that isn't in the registry.</summary>
internal sealed class DiscoveredRow : HoverRow
{
    private readonly DiscoveredPort _d;

    public DiscoveredRow(DiscoveredPort d, DockForm dock) : base(
        Theme.Button("Add", (_, _) => dock.AddDiscovered(d), primary: true),
        Theme.Button("Ignore", (_, _) => dock.Ignore(d.Port)),
        IconButton(IconKill, "Kill (asks first)", (_, _) => dock.ConfirmKill(d.Port, d.Pid), danger: true))
    {
        _d = d;
        DotControl.Color = Accent;
        TitleLabel.Text = d.Guess;
        SubLabel.Text = $"{d.ProcessName} · pid {d.Pid}" + (d.CommandLine is { } c ? " · " + Shorten(c) : "");
        string tip = d.CommandLine ?? d.ExecutablePath ?? d.ProcessName;
        Tips.SetToolTip(TitleLabel, tip);
        Tips.SetToolTip(SubLabel, tip);
        foreach (var b in Strip.Controls.OfType<Button>()) b.Margin = new Padding(S(3), 0, 0, 0);
    }

    public override bool Matches(string q) =>
        _d.Guess.Contains(q, StringComparison.OrdinalIgnoreCase) || _d.Port.ToString().StartsWith(q.TrimStart(':'));

    private static string Shorten(string cmd)
    {
        // Show the most telling bit: the script/project path rather than the interpreter.
        var parts = cmd.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string tail = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : cmd;
        return tail.Length > 70 ? "…" + tail[^70..] : tail;
    }
}
