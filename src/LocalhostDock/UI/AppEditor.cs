using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Net;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

/// <summary>
/// Add / edit dialog. The top "Detect from" field takes a launcher or project folder (typed, pasted, browsed
/// or dropped) and fills in everything else; every field stays editable.
/// </summary>
internal sealed class AppEditor : Form
{
    private readonly AppEntry _app;
    private readonly Registry _registry;
    private readonly TextBox _source, _name, _port, _extra, _url, _start, _stop, _cwd, _logFile;
    private readonly ComboBox _window, _candidates;
    private readonly CheckBox _capture;
    private readonly ComboBox _stopPolicy;
    private readonly NumericUpDown _grace;
    private readonly string? _originalStart;
    private int _detectGeneration;
    private readonly Label _detectNote, _portNote;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 350 };
    private List<Detection> _found = new();

    public AppEditor(AppEntry app, Registry registry, string title, string? detectFrom = null, string? note = null)
    {
        _app = app;
        _originalStart = app.Start;
        _registry = registry;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(S(16), S(12), S(16), S(12));
        AllowDrop = true;

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // ---- detect
        Caption(grid, "Detect from");
        _source = Box(detectFrom ?? "", "Launcher (.cmd/.bat/.ps1) or project folder — paste, browse or drop");
        grid.Controls.Add(_source);
        grid.Controls.Add(Pair(SmallButton("File…", (_, _) => BrowseFile(_source)), SmallButton("Folder…", (_, _) => BrowseFolder(_source))));

        grid.Controls.Add(Filler());
        _candidates = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(400), Visible = false, Margin = new Padding(S(3), 0, S(3), S(2)) };
        _candidates.SelectedIndexChanged += (_, _) => { if (_candidates.SelectedIndex >= 0) Fill(_found[_candidates.SelectedIndex]); };
        // A hidden control would be skipped by the table's auto-flow and shift every cell after it, so wrap it.
        var candidateHolder = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        candidateHolder.Controls.Add(_candidates);
        grid.Controls.Add(candidateHolder);
        grid.Controls.Add(Filler());

        grid.Controls.Add(Filler());
        _detectNote = new Label
        {
            Text = note ?? "Fields fill in automatically. You can still change any of them.",
            ForeColor = Muted, Font = Small, AutoSize = true, MaximumSize = new Size(S(400), 0), Margin = new Padding(S(3), 0, 0, S(10)),
        };
        grid.Controls.Add(_detectNote);
        grid.Controls.Add(Filler());

        // ---- fields
        _name = Field(grid, "Name", app.Name);
        Caption(grid, "Port");
        _port = Box(app.Port?.ToString() ?? "", "e.g. 3000");
        _port.Width = S(80);
        _extra = Box(string.Join(", ", app.ExtraPorts), "extra ports, comma-separated");
        _extra.Width = S(170);
        _portNote = new Label { AutoSize = true, Font = Small, ForeColor = Muted, Margin = new Padding(S(6), S(6), 0, 0) };
        var portRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        portRow.Controls.AddRange(new Control[] { _port, _extra, _portNote });
        grid.Controls.Add(portRow);
        grid.Controls.Add(Filler());

        _url = Field(grid, "URL", app.Url ?? "", "blank = http://localhost:{port}");
        _start = Field(grid, "Start", app.Start ?? "", "launcher path, or a command like npm run dev", browse: true);
        _stop = Field(grid, "Stop", app.Stop ?? "", "optional; blank = kill whatever holds the ports", browse: true);
        _cwd = Field(grid, "Working dir", app.Cwd ?? "", "blank = the launcher's folder", folder: true);

        Caption(grid, "Window");
        _window = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(120) };
        _window.Items.AddRange(new object[] { "minimized", "normal", "hidden" });
        _window.SelectedItem = _window.Items.Contains(app.Window) ? app.Window : "minimized";
        _capture = new CheckBox { Text = "Capture output to a log (runs hidden)", Checked = app.CaptureLogs, AutoSize = true, Margin = new Padding(S(12), S(5), 0, 0) };
        _capture.CheckedChanged += (_, _) => _window.Enabled = !_capture.Checked;
        _window.Enabled = !app.CaptureLogs;
        var winRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        winRow.Controls.AddRange(new Control[] { _window, _capture });
        grid.Controls.Add(winRow);
        grid.Controls.Add(Filler());

        _logFile = Field(grid, "Log file", app.LogFile ?? "", "optional: a log your app already writes", browse: true, filter: "Logs|*.log;*.txt|All files|*.*");

        Caption(grid, "Stop policy");
        _stopPolicy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(300) };
        _stopPolicy.Items.AddRange(new object[]
        {
            "graceful: ask it to exit, force after the timeout",
            "graceful-only: never force-kill",
            "force: stop immediately",
        });
        _stopPolicy.SelectedIndex = app.StopPolicy switch { "graceful-only" => 1, "force" => 2, _ => 0 };
        _grace = new NumericUpDown { Minimum = 1, Maximum = 300, Value = Math.Clamp(app.GracefulTimeoutSec, 1, 300), Width = S(60) };
        var policyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        policyRow.Controls.AddRange(new Control[] { _stopPolicy, new Label { Text = "timeout (s)", AutoSize = true, ForeColor = Muted, Margin = new Padding(S(10), S(6), S(4), 0) }, _grace });
        grid.Controls.Add(policyRow);
        grid.Controls.Add(Filler());

        // ---- buttons
        var save = Theme.Button("Save", (_, _) => Save(), primary: true);
        var cancel = Theme.Button("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; });
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Padding = new Padding(0, S(12), 0, 0) };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;

        // Auto-sized outer column (Dock-based layouts collapse inside an AutoSize form).
        var outer = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(Padding.Left, Padding.Top) };
        outer.Controls.Add(grid);
        outer.Controls.Add(buttons);
        Controls.Add(outer);
        Theme.Dark(this);

        _source.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunDetection(); };
        _port.TextChanged += (_, _) => UpdatePortNote();
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) _source.Text = f[0]; };
        Shown += (_, _) =>
        {
            UpdatePortNote();
            if (detectFrom is not null && string.IsNullOrEmpty(app.Start)) RunDetection();
            (string.IsNullOrEmpty(_source.Text) && string.IsNullOrEmpty(app.Name) ? _source : _name).Focus();
        };
    }

    // ---------------------------------------------------------------- detection

    private async void RunDetection()
    {
        string path = _source.Text.Trim().Trim('"');
        if (path.Length == 0) return;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            _detectNote.Text = "That path doesn't exist.";
            return;
        }

        // Detection reads files (and follows script references), so keep it off the UI thread.
        int generation = ++_detectGeneration;
        UseWaitCursor = true;
        _detectNote.Text = "Detecting…";
        List<Detection> found;
        try { found = await Task.Run(() => AppDetector.Detect(path)); }
        catch (Exception ex) { found = new(); _detectNote.Text = $"Detection failed: {ex.Message}"; }
        finally { UseWaitCursor = false; }
        if (generation != _detectGeneration || IsDisposed) return; // a newer path was typed meanwhile
        _found = found;

        _candidates.Items.Clear();
        _candidates.Visible = _found.Count > 1;
        if (_found.Count == 0)
        {
            _detectNote.Text = "Couldn't find a launcher or dev script there. Fill in Start and Port by hand.";
            if (Directory.Exists(path) && _cwd.Text.Length == 0) _cwd.Text = path;
            return;
        }
        foreach (var d in _found)
            _candidates.Items.Add($"{d.App.Name}  {(d.App.Port is int p ? ":" + p : "")}  —  {d.App.Start}");
        _candidates.SelectedIndex = 0; // fills
        if (_found.Count == 1) Fill(_found[0]);
    }

    private void Fill(Detection d)
    {
        var a = d.App;
        _name.Text = _registry.UniqueName(a.Name, _app);
        _app.StartUnverified = a.StartUnverified;
        _port.Text = a.Port?.ToString() ?? "";
        _extra.Text = string.Join(", ", a.ExtraPorts);
        _url.Text = a.Url ?? "";
        _start.Text = a.Start ?? "";
        _stop.Text = a.Stop ?? "";
        _cwd.Text = a.Cwd ?? "";
        _detectNote.Text = (_found.Count > 1 ? $"Found {_found.Count} apps here. Pick one above. " : "") + d.Note;
        UpdatePortNote();
    }

    private void UpdatePortNote()
    {
        if (!int.TryParse(_port.Text.Trim().TrimStart(':'), out int port) || port is < 1 or > 65535)
        {
            _portNote.Text = "";
            return;
        }
        var other = _registry.Apps.FirstOrDefault(a => a.Id != _app.Id && a.AllPorts.Contains(port));
        var holder = PortTable.GetListeners().FirstOrDefault(l => l.Port == port);
        if (other is not null)
            (_portNote.Text, _portNote.ForeColor) = ($"⚠ also used by {other.Name}", Pending);
        else if (holder is not null)
            (_portNote.Text, _portNote.ForeColor) = ($"● in use by {ProcessInfo.Get(holder.Pid)?.Name ?? "pid " + holder.Pid}", Up);
        else if (ExcludedPorts.Find(port) is { } r)
            (_portNote.Text, _portNote.ForeColor) = ($"⚠ reserved by Windows ({r.Start}–{r.End})", Down);
        else
            (_portNote.Text, _portNote.ForeColor) = ("free", Muted);
    }

    // ---------------------------------------------------------------- save

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { Warn("Give the app a name."); return; }
        string name = _name.Text.Trim();
        if (_registry.Apps.Any(a => a.Id != _app.Id && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { Warn($"There's already an app called “{name}”."); return; }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(_port.Text))
        {
            if (!int.TryParse(_port.Text.Trim().TrimStart(':'), out int p) || p is < 1 or > 65535) { Warn("Port must be 1–65535."); return; }
            port = p;
        }

        var extra = new List<int>();
        foreach (var part in _extra.Text.Split(',', ' ', ';').Where(s => s.Length > 0))
        {
            if (!int.TryParse(part.TrimStart(':'), out int p) || p is < 1 or > 65535) { Warn($"“{part}” isn't a valid port."); return; }
            extra.Add(p);
        }

        if (string.IsNullOrWhiteSpace(_start.Text) && port is null)
        {
            if (MessageBox.Show(this, "Without a start command or port, this app can't do much yet. Save anyway?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }

        _app.Name = name;
        _app.Port = port;
        _app.ExtraPorts = extra;
        _app.Url = NullIfBlank(_url.Text);
        _app.Start = NullIfBlank(_start.Text);
        _app.Stop = NullIfBlank(_stop.Text);
        _app.Cwd = NullIfBlank(_cwd.Text);
        _app.Window = (string)_window.SelectedItem!;
        _app.CaptureLogs = _capture.Checked;
        _app.LogFile = NullIfBlank(_logFile.Text);
        _app.StopPolicy = _stopPolicy.SelectedIndex switch { 1 => "graceful-only", 2 => "force", _ => "graceful" };
        _app.GracefulTimeoutSec = (int)_grace.Value;
        // Editing the start command by hand counts as reviewing it.
        if (!string.Equals(_app.Start, _originalStart, StringComparison.Ordinal)) _app.StartUnverified = false;
        DialogResult = DialogResult.OK;
    }

    // ---------------------------------------------------------------- layout helpers

    private static void Caption(TableLayoutPanel grid, string text) =>
        grid.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Muted, Margin = new Padding(0, S(6), S(10), 0) });

    private static Label Filler() => new() { AutoSize = true, Margin = Padding.Empty };

    private static TextBox Box(string value, string hint) =>
        new() { Text = value, Width = S(394), PlaceholderText = hint, Margin = new Padding(S(3), S(3), S(3), S(3)) };

    private TextBox Field(TableLayoutPanel grid, string label, string value, string? hint = null,
        bool browse = false, bool folder = false, string filter = "Launchers|*.cmd;*.bat;*.ps1;*.exe|All files|*.*")
    {
        Caption(grid, label);
        var box = Box(value, hint ?? "");
        grid.Controls.Add(box);
        if (browse) grid.Controls.Add(SmallButton("…", (_, _) => BrowseFile(box, filter)));
        else if (folder) grid.Controls.Add(SmallButton("…", (_, _) => BrowseFolder(box)));
        else grid.Controls.Add(Filler());
        return box;
    }

    private static Button SmallButton(string text, EventHandler click)
    {
        var b = Theme.Button(text, click);
        b.Margin = new Padding(S(2), S(2), 0, 0);
        return b;
    }

    private static FlowLayoutPanel Pair(params Control[] c)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        p.Controls.AddRange(c);
        return p;
    }

    private void BrowseFile(TextBox box, string filter = "Launchers and scripts|*.cmd;*.bat;*.ps1;*.exe;*.py;*.js|All files|*.*")
    {
        using var dlg = new OpenFileDialog { Filter = filter };
        string current = box.Text.Trim().Trim('"');
        if (File.Exists(current)) dlg.InitialDirectory = Path.GetDirectoryName(current);
        if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
    }

    private void BrowseFolder(TextBox box)
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(box.Text) ? box.Text : "" };
        if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
    }

    private void Warn(string msg) => MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounce.Dispose();
        base.Dispose(disposing);
    }
}
