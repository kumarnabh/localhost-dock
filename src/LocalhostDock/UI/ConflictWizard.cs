using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Net;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

/// <summary>What the user chose in the port-conflict wizard.</summary>
internal enum ConflictChoice { None, StopHolder, StopOtherApp, MovePort }

/// <summary>
/// Explains who holds an app's port and offers the safe ways out: stop the holder (through the guarded path,
/// or via its own app entry if it's a registered app), or move this app to a suggested free port. Nothing
/// happens until a button is pressed; the caller performs the action.
/// </summary>
internal sealed class ConflictWizard : Form
{
    public ConflictChoice Choice { get; private set; }
    public int NewPort { get; private set; }
    public string? NewStart { get; private set; }
    public string? NewUrl { get; private set; }
    public AppEntry? OtherApp { get; }
    public ProcessIdentity? Holder { get; }

    public ConflictWizard(AppEntry app, int port, Registry registry, AppController controller)
    {
        Text = $"Port :{port} is taken";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(S(16), S(12), S(16), S(12));

        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, MaximumSize = new Size(S(600), 0) };

        // ---- who holds it
        var listener = PortTable.GetListeners().FirstOrDefault(l => l.Port == port);
        var details = listener is null ? null : ProcessInfo.Get(listener.Pid, fresh: true);
        Holder = details?.Identity;
        string? cwd = listener is null ? null : ProcessCwd.TryGet(listener.Pid);
        List<AppEntry> others;
        lock (registry.SyncRoot) others = registry.Apps.Where(a => a.Id != app.Id && a.AllPorts.Contains(port)).Select(a => a.Clone()).ToList();
        OtherApp = listener is null ? null : others.FirstOrDefault(o => controller.Classify(o, listener).Ownership is Ownership.Owned or Ownership.Matched);

        col.Controls.Add(Para($"{app.Name} wants :{port}, but something else is listening on it.", Theme.Bold));
        if (listener is null)
            col.Controls.Add(Para("Nothing is listening any more, so the port is free now. Close this and press Start again."));
        else
        {
            string who = OtherApp is not null ? $"It's {OtherApp.Name}, another app in your dock." : $"It's {details?.Name ?? "an unknown program"} (pid {listener.Pid}).";
            col.Controls.Add(Para(who));
            col.Controls.Add(Para(
                (cwd is not null ? $"Running in: {cwd}\n" : "") +
                $"Command: {Shorten(details?.CommandLine ?? details?.Restriction ?? "not readable")}", Small, Muted));
        }

        // ---- option 1: stop the holder
        if (listener is not null)
        {
            string? protectedWhy = Holder is null ? "Windows doesn't allow Localhost Dock to inspect it" : ProcessGuard.WhyProtected(Holder);
            col.Controls.Add(Header("Option 1: free the port"));
            if (OtherApp is not null)
            {
                col.Controls.Add(Para($"Stop {OtherApp.Name} (its own stop rules apply), then start {app.Name}.", Small, Muted));
                col.Controls.Add(Theme.Button($"Stop {OtherApp.Name}", (_, _) => Finish(ConflictChoice.StopOtherApp)));
            }
            else if (protectedWhy is null)
            {
                col.Controls.Add(Para($"Ask {details?.Name ?? "it"} to exit, forcing it after 5 s, then start {app.Name}. Only do this if you know what it is.", Small, Muted));
                col.Controls.Add(Theme.Button($"Stop {details?.Name ?? "it"} (pid {listener.Pid})", (_, _) => Finish(ConflictChoice.StopHolder), danger: true));
            }
            else col.Controls.Add(Para($"Can't stop it from here: {protectedWhy}.", Small, Muted));
        }

        // ---- option 2: move this app
        List<int> registered;
        lock (registry.SyncRoot) registered = registry.Apps.Where(a => a.Id != app.Id).SelectMany(a => a.AllPorts).ToList();
        if (PortAdvisor.SuggestFree(port, registered) is int free)
        {
            NewPort = free;
            NewStart = PortAdvisor.RewriteStartPort(app.Start, port, free);
            NewUrl = app.Url?.Contains($":{port}") == true ? app.Url.Replace($":{port}", $":{free}") : null;
            col.Controls.Add(Header("Option 2: use another port"));
            string startNote = NewStart is not null
                ? $"Its start command becomes:\n{Shorten(NewStart)}"
                : app.Start is null ? "" : "Its start command doesn't set the port itself (it's set inside your launcher or the app's config), so change it there to match. Localhost Dock never edits your scripts.";
            col.Controls.Add(Para($"Move {app.Name} to :{free} (free, not reserved by Windows, not used by your other apps). {startNote}", Small, Muted));
            col.Controls.Add(Theme.Button($"Move {app.Name} to :{free}", (_, _) => Finish(ConflictChoice.MovePort), primary: true));
        }

        var cancel = Theme.Button("Close", (_, _) => Finish(ConflictChoice.None));
        cancel.Margin = new Padding(0, S(14), 0, 0);
        col.Controls.Add(cancel);
        CancelButton = cancel;

        Controls.Add(col);
        Theme.Dark(this);

        // This dialog can appear right where you were about to click something else, and one of its buttons stops
        // a process. Ignore clicks for a moment after it appears so a stray click can't choose for you.
        var actions = col.Controls.OfType<Button>().Where(b => b != cancel).ToList();
        foreach (var b in actions) b.Enabled = false;
        var arm = new System.Windows.Forms.Timer { Interval = 600 };
        arm.Tick += (_, _) => { arm.Dispose(); foreach (var b in actions) b.Enabled = true; };
        Shown += (_, _) => arm.Start();
    }

    private void Finish(ConflictChoice choice)
    {
        Choice = choice;
        DialogResult = choice == ConflictChoice.None ? DialogResult.Cancel : DialogResult.OK;
    }

    private static Label Header(string text) => new()
    {
        Text = text, Font = Theme.Bold, AutoSize = true, Margin = new Padding(0, S(14), 0, S(2)),
    };

    private static Label Para(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text, Font = font ?? Body, ForeColor = color ?? Theme.Text, AutoSize = true, MaximumSize = new Size(S(580), 0),
        Margin = new Padding(0, S(2), 0, S(4)), UseMnemonic = false,
    };

    private static string Shorten(string s) => s.Length > 220 ? s[..220] + "…" : s;
}

/// <summary>"Restore your last session?": pick which of the previously running apps to start again.</summary>
internal sealed class RestoreDialog : Form
{
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly List<AppEntry> _apps;

    public List<AppEntry> Selected => _list.CheckedIndices.Cast<int>().Select(i => _apps[i]).ToList();

    public RestoreDialog(List<AppEntry> apps, DateTime savedUtc)
    {
        _apps = apps;
        Text = "Restore your last session";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(S(420), S(320));
        Padding = new Padding(S(14));

        var intro = new Label
        {
            Dock = DockStyle.Top, Height = S(54), ForeColor = Muted,
            Text = $"These were running when Localhost Dock last saw them ({savedUtc.ToLocalTime():g}) and are stopped now. " +
                   "Start them again? Nothing that's running will be stopped.",
        };
        foreach (var a in apps) _list.Items.Add(a.Name + (a.StartUnverified ? "  (start command unverified)" : ""), !a.StartUnverified);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, S(8), 0, 0) };
        var start = Theme.Button("Start selected", (_, _) => DialogResult = DialogResult.OK, primary: true);
        buttons.Controls.Add(start);
        buttons.Controls.Add(Theme.Button("Not now", (_, _) => DialogResult = DialogResult.Cancel));
        AcceptButton = start;

        Controls.Add(_list);
        Controls.Add(intro);
        Controls.Add(buttons);
        Theme.Dark(this);
    }
}
