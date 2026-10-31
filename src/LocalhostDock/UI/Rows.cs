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
    private bool _conflict;

    public AppEntry App { get; }

    public AppRow(AppEntry app, DockForm dock) : this(app, dock,
        IconButton(IconOpen, "Open in browser", (_, _) => dock.Act(app, "open")),
        IconButton(IconPlay, "Start", (_, _) => dock.Act(app, "start")),
        IconButton(IconStop, "Stop", (_, _) => dock.Act(app, "stop"), danger: true),
        IconButton(IconRestart, "Restart", (_, _) => dock.Act(app, "restart")),
        IconButton(IconMore, "More: logs, folder, VS Code, workspaces, edit…", (_, _) => { }))
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
        // Clicking a conflict explanation opens the wizard that resolves it.
        SubLabel.Cursor = Cursors.Hand;
        SubLabel.Click += (_, _) => { if (_conflict) dock.ResolveConflict(app); };
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
            AppState.Up => (s.UpSince is { } since ? $"up {Ago(DateTime.Now - since)}" : "up") +
                           (s.Health is { Last: not null } h ? $" · {h.Describe()}" : "") +
                           (s.PrimaryOwnership == Ownership.Unverified ? " (owner unverified)" : ""),
            AppState.Degraded => $"degraded · {s.Health?.Describe() ?? "health check failing"}",
            AppState.Down when s.LastError is { } err => "failed: " + err,
            AppState.Down when s.LastExit is { Expected: false } ex => ex.Code is int code ? $"crashed (exit code {code})" : "went down unexpectedly",
            AppState.Down => "down",
            AppState.Starting => s.Health is { Last: { Ok: false } } ? $"starting… waiting for health ({s.Health.Describe()})" : "starting…",
            AppState.Stopping => "stopping…",
            AppState.Conflict => $"port held by {s.ForeignHolder?.Identity?.ToString() ?? "another program"} · click to resolve",
            _ => "no port set; ⋯ → Edit to finish setup",
        };
        if (app.StartUnverified && s.State != AppState.Up) state += " · start command unverified";
        string mem = s.State is AppState.Up or AppState.Degraded && s.MemoryBytes > 0 ? $" · {Bytes(s.MemoryBytes)}" : "";
        string text = ports.Length > 0 ? $"{ports} · {state}{mem}" : state;
        if (SubLabel.Text != text)
        {
            SubLabel.Text = text;
            SubLabel.ForeColor = s.State is AppState.Conflict or AppState.Degraded || (s.State == AppState.Down && (s.LastError is not null || s.LastExit is { Expected: false }))
                ? Pending : Muted;
            Tips.SetToolTip(SubLabel, StatusExplanation(s) + "\n" + text);
        }

        _conflict = s.State == AppState.Conflict;
        bool up = s.State is AppState.Up or AppState.Degraded;
        bool busy = s.State is AppState.Starting or AppState.Stopping;
        bool hasStart = !string.IsNullOrWhiteSpace(app.Start);
        _open.Enabled = app.EffectiveUrl is not null;
        _start.Enabled = hasStart && !busy && s.State is AppState.Down or AppState.NotConfigured or AppState.Conflict;
        // Only offer Stop when something of *this app's* is running, or its port is in conflict (Stop then asks first).
        // A stranger on an extra port while the app is down is not a reason to light up Stop.
        _stop.Enabled = (up || s.State == AppState.Conflict || s.Listeners.Any(l => l.Ownership != Ownership.Foreign)) && s.State != AppState.Stopping;
        _restart.Enabled = hasStart && up && !busy;
        Theme.Tips.SetToolTip(_stop, s.State == AppState.Conflict
            ? "Stop: the process on this port isn't verified as this app, so you'll be asked first"
            : "Stop");
    }

    private static string StatusExplanation(AppStatus s) => s.State switch
    {
        AppState.Up when s.Health is { Last: { Ok: true } } && s.LastStartDuration is { } took => $"Running and healthy; it was ready {took.TotalSeconds:0.0} s after Start.",
        AppState.Degraded => $"Listening, but the health check ({s.App.Health}) is failing: {s.Health?.Describe()}. Availability over the last {s.Health?.Samples} checks: {s.Health?.Availability:P0}.",
        AppState.Up when s.PrimaryOwnership == Ownership.Owned => "Running: started by Localhost Dock." + (s.LastStartDuration is { } t ? $" Ready after {t.TotalSeconds:0.0} s." : ""),
        AppState.Up when s.PrimaryOwnership == Ownership.Matched => "Running: the process on the port belongs to this app's folder.",
        AppState.Up => "Something is listening on the port, but Localhost Dock can't verify it's this app. Stop will ask before closing it.",
        AppState.Conflict => $"Another program holds this app's port ({s.ForeignHolder?.Evidence}). Click to see who it is and to free the port or move this app.",
        AppState.Down when s.LastExit is { Expected: false } ex => $"It went down without you stopping it ({ex.Process}{(ex.Code is int c ? $", exit code {c}" : "")}).",
        AppState.Down => "Not running.",
        AppState.Starting => "Waiting for the app to listen on its port.",
        AppState.Stopping => "Asking the app to exit (force-stopping only if its stop policy allows).",
        _ => "No port configured, so status can't be known.",
    };

    private static string Describe(AppEntry a) =>
        string.Join("\n", new[]
        {
            a.EffectiveUrl, a.Start is null ? null : "Start: " + a.Start, a.Stop is null ? null : "Stop: " + a.Stop,
            a.Cwd is null ? null : "In: " + a.Cwd, "Double-click to open · right-click for more",
        }.Where(x => x is not null));
}

/// <summary>A workspace: readiness of its members and one-click Start all / Stop all (dependency-aware).</summary>
internal sealed class WorkspaceRow : HoverRow
{
    private readonly Button _start, _stop;
    private readonly WorkspaceEntry _ws;
    private List<string> _memberNames = new();

    public WorkspaceRow(WorkspaceEntry ws, DockForm dock) : this(ws, dock,
        IconButton(IconPlay, "Start workspace (dependencies first)", (_, _) => dock.ActWorkspace(ws, "start")),
        IconButton(IconStop, "Stop workspace (dependents first)", (_, _) => dock.ActWorkspace(ws, "stop"), danger: true),
        IconButton(IconMore, "More", (_, _) => { }))
    {
    }

    private WorkspaceRow(WorkspaceEntry ws, DockForm dock, Button start, Button stop, Button more) : base(start, stop, more)
    {
        _ws = ws;
        (_start, _stop) = (start, stop);
        TitleLabel.Text = ws.Name;
        more.Click += (_, _) => dock.ShowWorkspaceMenu(ws, more, new Point(0, more.Height));
        OnBodyClick((s, e) => { if (e.Button == MouseButtons.Right) dock.ShowWorkspaceMenu(ws, (Control)s!, e.Location); });
        OnBodyDoubleClick((_, _) => dock.ActWorkspace(ws, "start"));
    }

    public override bool Matches(string q) =>
        _ws.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || _memberNames.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase));

    public void Update(WorkspaceStatus w)
    {
        int total = w.Members.Count, ready = w.Up;
        bool trouble = w.Members.Any(m => m.State is AppState.Conflict or AppState.Degraded || m.LastError is not null);
        DotControl.Color = w.Busy ? Pending : ready == 0 ? (trouble ? Pending : Down) : trouble ? Pending : Up;
        DotControl.Ring = ready > 0 && ready < total && !w.Busy;
        _memberNames = w.Members.Select(m => m.App.Name).ToList();
        // e.g. "2 of 3 ready · API ✓ · Web ✓ · Worker ✗"
        string marks = string.Join(" · ", w.Members.Select(m => m.App.Name + (m.State switch
        {
            AppState.Up => " ✓",
            AppState.Starting or AppState.Stopping => " …",
            AppState.Degraded => " ⚠",
            AppState.Conflict => " ⚠",
            _ => " ✗",
        })));
        string text = $"{ready} of {total} ready · {marks}";
        if (SubLabel.Text != text)
        {
            SubLabel.Text = text;
            Tips.SetToolTip(SubLabel, "Start order: " + StartOrder(w) + (w.Workspace.OnFailure == "continue" ? "\nKeeps starting what doesn't depend on a failed app." : "\nStops starting more as soon as one fails."));
        }
        _start.Enabled = ready < total && !w.Busy;
        _stop.Enabled = w.Members.Any(m => m.State is AppState.Up or AppState.Degraded) && !w.Busy;
    }

    private static string StartOrder(WorkspaceStatus w)
    {
        try
        {
            var names = w.Members.ToDictionary(m => m.App.Id, m => m.App.Name);
            return string.Join(" → ", LocalhostDock.Workspace.WorkspacePlan.Levels(w.Workspace)
                .Select(level => string.Join(" + ", level.Select(id => names.GetValueOrDefault(id, id)))));
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }
}
/// <summary>A listening port that isn't in the registry.</summary>
internal sealed class DiscoveredRow : HoverRow
{
    private readonly DiscoveredPort _d;

    public DiscoveredRow(DiscoveredPort d, DockForm dock) : base(
        Theme.Button("Add", (_, _) => dock.AddDiscovered(d), primary: true),
        Theme.Button("Ignore", (_, _) => dock.Ignore(d.Port)),
        IconButton(IconKill, "Kill (asks first)", (_, _) => dock.ConfirmKill(d.Port, d.Identity), danger: true))
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
