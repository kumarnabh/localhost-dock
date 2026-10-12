using System.Diagnostics;
using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Net;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

/// <summary>The popup panel: search, groups, apps, discovered ports, and the port lookup.</summary>
internal sealed class DockForm : Form
{
    public static readonly int PanelWidth = S(500);
    public static readonly int RowWidth = PanelWidth - S(30);

    private readonly AppController _controller;
    private readonly Action _requestPoll;
    private readonly Label _summary;
    private readonly TextBox _search;
    private readonly FlowLayoutPanel _list;
    private readonly Panel _footer, _dropOverlay;
    private readonly TextBox _portBox;
    private readonly FlowLayoutPanel _lookupResult;
    private readonly Button _addButton;

    private readonly Dictionary<string, AppRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GroupRow> _groupRows = new(StringComparer.OrdinalIgnoreCase);
    private string _signature = "";
    private Snapshot? _last;
    private DateTime _hiddenAt;
    private int _modal;
    private Rectangle _area;

    public DockForm(AppController controller, Action requestPoll)
    {
        _controller = controller;
        _requestPoll = requestPoll;

        Text = "Localhost Dock";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        ForeColor = Theme.Text;
        Font = Body;
        Width = PanelWidth;
        Height = S(480);
        KeyPreview = true;
        AllowDrop = true;

        // ---- header
        var header = new Panel { Dock = DockStyle.Top, Height = S(54), BackColor = Background };
        var title = new Label { Text = "Localhost Dock", Font = Theme.Title, AutoSize = true, Location = new Point(S(14), S(8)) };
        _summary = new Label { Font = Small, ForeColor = Muted, AutoSize = true, Location = new Point(S(15), S(31)) };
        var headerButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, AutoSize = true, WrapContents = false,
            Padding = new Padding(0, S(13), S(10), 0), BackColor = Background,
        };
        headerButtons.Controls.Add(IconButton(IconRefresh, "Refresh", (_, _) => _requestPoll()));
        _addButton = Theme.Button("+  Add", (_, _) => ShowAddMenu(), primary: true);
        headerButtons.Controls.Add(_addButton);
        header.Controls.AddRange(new Control[] { headerButtons, title, _summary });

        // ---- search
        var searchBar = new Panel { Dock = DockStyle.Top, Height = S(38), Padding = new Padding(S(14), S(2), S(14), S(8)), BackColor = Background };
        _search = new TextBox
        {
            Dock = DockStyle.Fill, BackColor = Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Search apps…   Enter opens the first match",
        };
        _search.KeyDown += SearchKeyDown;
        // WinForms hides PlaceholderText while the box has focus (it always does here), so draw our own hint.
        var hint = new Label
        {
            Text = "Search…   Enter opens  ·  Ctrl+N adds  ·  or drop a launcher / folder here",
            ForeColor = Color.FromArgb(105, 110, 120), BackColor = Input, Font = Small, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.IBeam, UseMnemonic = false,
        };
        hint.Click += (_, _) => _search.Focus();
        _search.Controls.Add(hint);
        _search.Resize += (_, _) => hint.Bounds = new Rectangle(S(4), 0, _search.ClientSize.Width - S(8), _search.ClientSize.Height);
        _search.TextChanged += (_, _) => { hint.Visible = _search.TextLength == 0; ApplyFilter(); };
        searchBar.Controls.Add(_search);

        // ---- list
        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(S(10), 0, 0, S(8)), BackColor = Background,
        };

        // ---- footer: who's on this port?
        _footer = new Panel { Dock = DockStyle.Bottom, Height = S(48), BackColor = Surface };
        var lookupTitle = new Label { Text = "Who's on port", ForeColor = Muted, AutoSize = true, Location = new Point(S(14), S(15)) };
        _portBox = new TextBox
        {
            Location = new Point(S(104), S(12)), Width = S(80), BackColor = Input, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "5173",
        };
        _portBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LookupPort(); } };
        var lookupBtn = Theme.Button("Look up", (_, _) => LookupPort());
        lookupBtn.Location = new Point(S(190), S(11));
        _lookupResult = new FlowLayoutPanel
        {
            Location = new Point(S(14), S(44)), Size = new Size(RowWidth, 0), FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoSize = true, BackColor = Surface,
        };
        _footer.Controls.AddRange(new Control[] { lookupTitle, _portBox, lookupBtn, _lookupResult });

        // ---- drop overlay
        _dropOverlay = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 38, 56), Visible = false };
        _dropOverlay.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Accent, Font = Theme.Bold,
            Text = "Drop a launcher (.cmd / .bat / .ps1)\nor a project folder to add it",
        });

        Controls.Add(_dropOverlay);
        Controls.Add(_list);
        Controls.Add(_footer);
        Controls.Add(searchBar);
        Controls.Add(header);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { if (_search.TextLength > 0) _search.Clear(); else Hide(); }
            else if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.N) { AddApp(); e.SuppressKeyPress = true; }
        };
        Deactivate += (_, _) =>
        {
            if (_modal > 0) return;
            Hide();
            _hiddenAt = DateTime.UtcNow;
        };
        EnableDrop(this);
    }

    // ================================================================ show / hide

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
        _search.Focus();
        _requestPoll();
    }

    /// <summary>Sizes the panel to its rows (up to 85% of the screen) and pins it to the tray corner.</summary>
    private void FitToContent()
    {
        if (_area.IsEmpty) _area = Screen.PrimaryScreen!.WorkingArea;
        int rows = _list.Controls.Cast<Control>().Where(c => !_filteredOut.Contains(c)).Sum(c => c.Height + c.Margin.Vertical);
        int contentHeight = S(54) + S(38) + _footer.Height + rows + S(20);
        Height = Math.Clamp(contentHeight, S(240), (int)(_area.Height * 0.85));
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
        int pref = 2; // DWMWCP_ROUND: Windows 11 rounded corners; harmless no-op elsewhere.
        DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
        SetWindowTheme(_list.Handle, "DarkMode_Explorer", null); // dark scrollbar
    }

    // ================================================================ rendering

    public void ForceRebuild() => _signature = "";

    public bool IsEmpty => _last is null;

    public void Render(Snapshot snap)
    {
        _last = snap;
        var tracked = snap.Apps.Where(a => a.State != AppState.NotConfigured).ToList();
        _summary.Text = $"{tracked.Count(a => a.State == AppState.Up)} of {tracked.Count} up" +
                        (snap.Discovered.Count > 0 ? $"  ·  {snap.Discovered.Count} unknown port{(snap.Discovered.Count == 1 ? "" : "s")}" : "");

        string sig = string.Join("|", snap.Groups.Select(g => g.Group.Name + ":" + string.Join(",", g.Group.Apps))) + "#" +
                     string.Join("|", snap.Apps.Select(a => a.App.Name)) + "#" +
                     string.Join("|", snap.Discovered.Select(d => $"{d.Port}/{d.Pid}"));
        if (sig != _signature)
        {
            Rebuild(snap);
            _signature = sig;
        }
        foreach (var s in snap.Apps)
            if (_rows.TryGetValue(s.App.Name, out var row)) row.Update(s);
        foreach (var g in snap.Groups)
            if (_groupRows.TryGetValue(g.Group.Name, out var row)) row.Update(g);
    }

    private void Rebuild(Snapshot snap)
    {
        _list.SuspendLayout();
        var old = _list.Controls.Cast<Control>().ToList();
        _list.Controls.Clear();
        foreach (var c in old) c.Dispose();
        _rows.Clear();
        _groupRows.Clear();

        if (snap.Groups.Count > 0)
        {
            _list.Controls.Add(SectionLabel("GROUPS", snap.Groups.Count));
            foreach (var g in snap.Groups)
            {
                var row = new GroupRow(g.Group, this);
                _groupRows[g.Group.Name] = row;
                _list.Controls.Add(row);
            }
        }

        _list.Controls.Add(SectionLabel("APPS", snap.Apps.Count));
        if (snap.Apps.Count == 0) _list.Controls.Add(EmptyState());
        foreach (var s in snap.Apps)
        {
            var row = new AppRow(s.App, this);
            _rows[s.App.Name] = row;
            _list.Controls.Add(row);
        }

        if (snap.Discovered.Count > 0)
        {
            _list.Controls.Add(SectionLabel("DISCOVERED — NOT IN YOUR DOCK", snap.Discovered.Count));
            foreach (var d in snap.Discovered) _list.Controls.Add(new DiscoveredRow(d, this));
        }

        foreach (Control c in _list.Controls) EnableDrop(c);
        _list.ResumeLayout();
        ApplyFilter();
    }

    private static Label SectionLabel(string text, int count) => new()
    {
        Text = $"{text}   {count}", Font = Header, ForeColor = Muted, AutoSize = true, Margin = new Padding(S(4), S(12), 0, S(5)),
        Tag = "section",
    };

    private Control EmptyState()
    {
        var panel = new Panel { Width = RowWidth, Height = S(118), BackColor = Surface, Margin = new Padding(0, 0, 0, S(4)) };
        panel.Controls.Add(new Label
        {
            Text = "No apps yet. Drop a launcher or project folder here, scan a folder you keep projects in, or add one by hand.",
            ForeColor = Muted, Location = new Point(S(14), S(12)), Size = new Size(RowWidth - S(28), S(40)),
        });
        var scan = Theme.Button("Scan a folder…", (_, _) => ScanFolder(), primary: true);
        scan.Location = new Point(S(14), S(66));
        var add = Theme.Button("Add app…", (_, _) => AddApp());
        add.Location = new Point(scan.Right + S(8), S(66));
        panel.Controls.AddRange(new Control[] { scan, add });
        return panel;
    }

    // ================================================================ search

    // Rows hidden by the search box. (Control.Visible can't be used for this: it reads false for every
    // child while the dock itself is hidden.)
    private readonly HashSet<Control> _filteredOut = new();

    private void ApplyFilter()
    {
        string q = _search.Text.Trim();
        _filteredOut.Clear();
        var controls = _list.Controls.Cast<Control>().ToList();
        foreach (var c in controls)
            if (c is IFilterable f && q.Length > 0 && !f.Matches(q)) _filteredOut.Add(c);

        // Hide section headers whose rows are all filtered out.
        for (int i = 0; i < controls.Count; i++)
        {
            if (controls[i].Tag as string != "section") continue;
            bool any = controls.Skip(i + 1).TakeWhile(c => c.Tag as string != "section").Any(c => !_filteredOut.Contains(c));
            if (!any) _filteredOut.Add(controls[i]);
        }

        _list.SuspendLayout();
        foreach (var c in controls) c.Visible = !_filteredOut.Contains(c);
        _list.ResumeLayout();
        if (Visible) FitToContent();
    }

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        var first = _list.Controls.OfType<AppRow>().FirstOrDefault(r => r.Visible);
        if (first is not null) Act(first.App, "open");
    }

    // ================================================================ adding apps

    private void ShowAddMenu()
    {
        var menu = DarkMenu.Create();
        menu.Items.Add(DarkMenu.Item("Add app…   (Ctrl+N)", AddApp));
        menu.Items.Add(DarkMenu.Item("Add from a launcher or project folder…", PickAndAdd));
        menu.Items.Add(DarkMenu.Item("Scan a folder for apps…", () => ScanFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(DarkMenu.Item("New group…", () => EditGroup(null)));
        menu.Show(_addButton, new Point(0, _addButton.Height));
    }

    private void AddApp() => OpenEditor(new AppEntry(), "Add app", detectFrom: null, note: null, isNew: true);

    public void PickAndAdd()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Pick a launcher — or open a folder and click “Open” with nothing selected",
            Filter = "Launchers and scripts|*.cmd;*.bat;*.ps1;*.exe;*.py;*.js|All files|*.*",
            CheckFileExists = false, ValidateNames = false, FileName = "Folder selection",
        };
        if (ShowModal(() => dlg.ShowDialog(this)) != DialogResult.OK) return;
        string path = dlg.FileName;
        if (!File.Exists(path)) path = Path.GetDirectoryName(path)!; // "Folder selection" trick
        AddFromPath(path);
    }

    /// <summary>One-step add from a launcher file or project folder (drag-drop, Explorer, --add).</summary>
    public void AddFromPath(string path)
    {
        path = path.Trim().Trim('"');
        if (Directory.Exists(path))
        {
            var found = AppDetector.Detect(path);
            if (found.Count == 0) { ScanFolder(path); return; }       // e.g. C:\dev → look deeper
            if (found.Count > 1) { ImportMany(path, found); return; } // monorepo / several launchers
        }
        OpenEditor(new AppEntry(), "Add app", detectFrom: path, note: null, isNew: true);
    }

    internal void AddDiscovered(DiscoveredPort d)
    {
        var details = ProcessInfo.Get(d.Pid, fresh: true) ?? new ProcessDetails(d.Pid, d.ProcessName, d.ExecutablePath, d.CommandLine, 0);
        var detection = AppDetector.FromProcess(details, d.Port);
        detection.App.Name = _controller.Registry.UniqueName(detection.App.Name);
        OpenEditor(detection.App, $"Add {d.Guess}", detectFrom: null, note: detection.Note, isNew: true);
    }

    public void ScanFolder(string? folder = null)
    {
        using var dlg = new ImportDialog(_controller.Registry, folder);
        if (folder is not null) dlg.Shown += (_, _) => dlg.AcceptButton?.PerformClick();
        if (ShowModal(() => dlg.ShowDialog(OwnerOrNull())) == DialogResult.OK) AddAll(dlg.Selected);
        else SaveAndRefresh(); // remembered scan folders
    }

    private void ImportMany(string folder, List<Detection> found)
    {
        using var dlg = new ImportDialog(_controller.Registry, folder, found);
        if (ShowModal(() => dlg.ShowDialog(OwnerOrNull())) == DialogResult.OK) AddAll(dlg.Selected);
    }

    private void AddAll(List<AppEntry> apps)
    {
        foreach (var a in apps)
        {
            a.Name = _controller.Registry.UniqueName(a.Name);
            _controller.Registry.Apps.Add(a);
        }
        SaveAndRefresh();
        AfterAdd(apps.Count == 1 ? apps[0].Name : $"{apps.Count} apps");
    }

    private void OpenEditor(AppEntry app, string title, string? detectFrom, string? note, bool isNew)
    {
        using var editor = new AppEditor(app, _controller.Registry, title, detectFrom, note);
        if (ShowModal(() => editor.ShowDialog(OwnerOrNull())) != DialogResult.OK) return;
        if (isNew) _controller.Registry.Apps.Add(app);
        SaveAndRefresh();
        if (isNew) AfterAdd(app.Name);
    }

    private void AfterAdd(string what)
    {
        _search.Clear();
        if (!Visible) ShowNearTray();
        Added?.Invoke(what);
    }

    /// <summary>Raised after apps are added (the tray shows a confirmation).</summary>
    public event Action<string>? Added;

    // ---- drag & drop
    private void EnableDrop(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += OnDragEnter;
        c.DragLeave += (_, _) => _dropOverlay.Visible = false;
        c.DragDrop += OnDragDrop;
        foreach (Control child in c.Controls) EnableDrop(child);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        bool ok = e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok && !_dropOverlay.Visible) { _dropOverlay.Visible = true; _dropOverlay.BringToFront(); }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        _dropOverlay.Visible = false;
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        BeginInvoke(() =>
        {
            if (paths.Length == 1) { AddFromPath(paths[0]); return; }
            var found = paths.SelectMany(AppDetector.Detect).ToList();
            ImportMany(Path.GetDirectoryName(paths[0]) ?? paths[0], found);
        });
    }

    // ================================================================ app actions

    internal async void Act(AppEntry app, string action)
    {
        try
        {
            switch (action)
            {
                case "open": _controller.Open(app); Hide(); break;
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

    internal void ShowAppMenu(AppEntry app, Control anchor, Point at)
    {
        var reg = _controller.Registry;
        var menu = DarkMenu.Create();
        string? folder = app.Folder;
        menu.Items.Add(DarkMenu.Item("View logs", () => LogViewer.ShowFor(app)));
        menu.Items.Add(DarkMenu.Item("Copy URL", () => Clipboard.SetText(app.EffectiveUrl!), app.EffectiveUrl is not null));
        menu.Items.Add(DarkMenu.Item("Open folder", () => Process.Start("explorer.exe", $"\"{folder}\""), folder is not null));
        menu.Items.Add(DarkMenu.Item("Open in VS Code", () => Shell.OpenInEditor(folder!), folder is not null && Shell.HasVsCode));
        menu.Items.Add(DarkMenu.Item("Open terminal here", () => Shell.OpenTerminal(folder!), folder is not null));
        menu.Items.Add(new ToolStripSeparator());

        var groups = new ToolStripMenuItem("Add to group");
        foreach (var g in reg.Groups)
        {
            bool member = g.Apps.Contains(app.Name, StringComparer.OrdinalIgnoreCase);
            var gi = DarkMenu.Item(g.Name, () => ToggleGroupMember(g, app));
            gi.Checked = member;
            groups.DropDownItems.Add(gi);
        }
        if (reg.Groups.Count > 0) groups.DropDownItems.Add(new ToolStripSeparator());
        groups.DropDownItems.Add(DarkMenu.Item("New group…", () => EditGroup(null, app.Name)));
        menu.Items.Add(groups);

        int index = reg.Apps.IndexOf(app);
        menu.Items.Add(DarkMenu.Item("Move up", () => MoveApp(app, -1), index > 0));
        menu.Items.Add(DarkMenu.Item("Move down", () => MoveApp(app, 1), index >= 0 && index < reg.Apps.Count - 1));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(DarkMenu.Item("Edit…", () => EditApp(app)));
        menu.Items.Add(DarkMenu.Item("Duplicate…", () => DuplicateApp(app)));
        menu.Items.Add(DarkMenu.Item("Remove", () => RemoveApp(app), danger: true));
        _modal++;
        menu.Closed += (_, _) => BeginInvoke(() => { _modal--; if (Visible) Activate(); });
        menu.Show(anchor, at);
    }

    internal void EditApp(AppEntry app)
    {
        var copy = app.Clone();
        using var editor = new AppEditor(copy, _controller.Registry, $"Edit {app.Name}");
        // AppEditor renames group memberships itself; compare against the original entry for uniqueness.
        int i = _controller.Registry.Apps.IndexOf(app);
        _controller.Registry.Apps[i] = copy;
        if (ShowModal(() => editor.ShowDialog(OwnerOrNull())) != DialogResult.OK)
        {
            _controller.Registry.Apps[i] = app;
            return;
        }
        SaveAndRefresh();
    }

    private void DuplicateApp(AppEntry app)
    {
        var copy = app.Clone();
        copy.Name = _controller.Registry.UniqueName(app.Name + " copy");
        OpenEditor(copy, $"Duplicate {app.Name}", detectFrom: null, note: null, isNew: true);
    }

    internal void RemoveApp(AppEntry app)
    {
        var answer = ShowModal(() => MessageBox.Show(this,
            $"Remove {app.Name} from the dock?\n\nThis doesn't stop it or touch its files.", "Remove app",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2));
        if (answer != DialogResult.Yes) return;
        _controller.Registry.Remove(app);
        SaveAndRefresh();
    }

    private void MoveApp(AppEntry app, int delta)
    {
        var apps = _controller.Registry.Apps;
        int i = apps.IndexOf(app), j = i + delta;
        if (i < 0 || j < 0 || j >= apps.Count) return;
        (apps[i], apps[j]) = (apps[j], apps[i]);
        SaveAndRefresh();
    }

    // ================================================================ groups

    internal async void ActGroup(GroupEntry group, string action)
    {
        try
        {
            if (action == "start") await _controller.StartGroupAsync(group, _ => _requestPoll());
            else await _controller.StopGroupAsync(group);
        }
        catch (Exception ex)
        {
            ShowModal(() => MessageBox.Show(this, ex.Message, group.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
        _requestPoll();
    }

    internal void ShowGroupMenu(GroupEntry group, Control anchor, Point at)
    {
        var menu = DarkMenu.Create();
        menu.Items.Add(DarkMenu.Item("Open all", () =>
        {
            foreach (var a in group.Apps.Select(_controller.Registry.Find).Where(a => a?.EffectiveUrl is not null)) _controller.Open(a!);
        }));
        menu.Items.Add(DarkMenu.Item("Edit…", () => EditGroup(group)));
        menu.Items.Add(DarkMenu.Item("Delete group", () =>
        {
            _controller.Registry.Groups.Remove(group);
            SaveAndRefresh();
        }, danger: true));
        _modal++;
        menu.Closed += (_, _) => BeginInvoke(() => { _modal--; if (Visible) Activate(); });
        menu.Show(anchor, at);
    }

    private void EditGroup(GroupEntry? group, string? withApp = null)
    {
        if (_controller.Registry.Apps.Count == 0)
        {
            ShowModal(() => MessageBox.Show(this, "Add some apps first; a group starts several apps together.", "New group"));
            return;
        }
        bool isNew = group is null;
        var target = group ?? new GroupEntry { Apps = withApp is null ? new() : new() { withApp } };
        var working = new GroupEntry { Name = target.Name, Apps = target.Apps.ToList() };
        using var editor = new GroupEditor(working, _controller.Registry, isNew ? "New group" : $"Edit {target.Name}");
        if (ShowModal(() => editor.ShowDialog(OwnerOrNull())) != DialogResult.OK) return;
        target.Name = working.Name;
        target.Apps = working.Apps;
        if (isNew) _controller.Registry.Groups.Add(target);
        SaveAndRefresh();
    }

    private void ToggleGroupMember(GroupEntry g, AppEntry app)
    {
        if (g.Apps.RemoveAll(n => n.Equals(app.Name, StringComparison.OrdinalIgnoreCase)) == 0) g.Apps.Add(app.Name);
        SaveAndRefresh();
    }

    // ================================================================ discovered & lookup

    internal void Ignore(int port)
    {
        if (!_controller.Registry.IgnoredPorts.Contains(port)) _controller.Registry.IgnoredPorts.Add(port);
        SaveAndRefresh();
    }

    private void LookupPort()
    {
        _lookupResult.Controls.Clear();
        _footer.Height = S(48);
        if (!int.TryParse(_portBox.Text.Trim().TrimStart(':'), out int port) || port is < 1 or > 65535)
        {
            AddLookupLine(Hint("Enter a port number between 1 and 65535."));
            return;
        }

        var holders = PortTable.GetListeners().Where(l => l.Port == port).GroupBy(l => l.Pid).ToList();
        if (holders.Count == 0)
        {
            AddLookupLine(Hint(ExcludedPorts.Find(port) is { } r
                ? $"Nothing is listening, but Windows reserves :{r.Start}–{r.End} (Hyper-V/WSL/Docker), so nothing can bind :{port} either. Pick another port."
                : $":{port} is free; nothing is listening."));
            return;
        }

        foreach (var g in holders)
        {
            var d = ProcessInfo.Get(g.Key, fresh: true);
            string addrs = string.Join(", ", g.Select(l => l.Address).Distinct());
            var owner = _controller.Registry.Apps.FirstOrDefault(a => a.AllPorts.Contains(port));
            var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Surface };
            var lbl = new Label
            {
                Text = $"{d?.Name ?? "?"} · pid {g.Key} · {addrs}" + (owner is not null ? $" · {owner.Name}" : ""),
                AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, S(6), S(6), 0),
            };
            Tips.SetToolTip(lbl, d?.CommandLine ?? d?.ExecutablePath ?? "");
            line.Controls.Add(lbl);
            if (d is not null && !d.IsCritical)
                line.Controls.Add(Theme.Button("Kill", (_, _) => ConfirmKill(port, g.Key), danger: true));
            AddLookupLine(line);
        }
    }

    private void AddLookupLine(Control c)
    {
        _lookupResult.Controls.Add(c);
        _footer.Height = S(48) + _lookupResult.Controls.Cast<Control>().Sum(x => x.Height + x.Margin.Vertical) + S(8);
        if (Visible) FitToContent();
    }

    internal void ConfirmKill(int port, int pid)
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

    // ================================================================ helpers

    internal static Label Hint(string text) => new()
    {
        Text = text, ForeColor = Muted, Font = Small, MaximumSize = new Size(RowWidth, 0), AutoSize = true,
        Margin = new Padding(0, S(2), 0, S(4)),
    };

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

    private IWin32Window? OwnerOrNull() => Visible ? this : null;

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
}
