using System.Diagnostics;
using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Net;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

/// <summary>The popup panel: search, workspaces, apps, discovered ports, and the port lookup.</summary>
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
    private readonly Dictionary<string, WorkspaceRow> _workspaceRows = new(StringComparer.OrdinalIgnoreCase);
    private string _signature = "";
    private Snapshot? _last;
    private DateTime _hiddenAt;
    private int _modal;
    private Rectangle _area;
    private string? _workspaceScope;
    private int _resultIndex;

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
            Text = "Search apps / workspaces…  ↑↓ selects · Enter opens · Esc goes back",
            ForeColor = Color.FromArgb(105, 110, 120), BackColor = Input, Font = Small, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.IBeam, UseMnemonic = false,
        };
        hint.Click += (_, _) => _search.Focus();
        _search.Controls.Add(hint);
        _search.Resize += (_, _) => hint.Bounds = new Rectangle(S(4), 0, _search.ClientSize.Width - S(8), _search.ClientSize.Height);
        _search.TextChanged += (_, _) => { hint.Visible = _search.TextLength == 0; _resultIndex = 0; ApplyFilter(); };
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
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (_search.TextLength > 0) _search.Clear();
                else if (_workspaceScope is not null) NavigateWorkspace(null);
                else Hide();
            }
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

        string sig = string.Join("|", snap.Workspaces.Select(w => w.Workspace.Id + w.Workspace.Name + ":" + string.Join(",", w.Workspace.Apps) + ":" + w.Workspace.DependsOn.Count + w.Workspace.OnFailure)) + "#" +
                     string.Join("|", snap.Apps.Select(a => a.App.Id + "=" + a.App.Name + (a.App.StartUnverified ? "?" : ""))) + "#" +
                     string.Join("|", snap.Discovered.Select(d => $"{d.Port}/{d.Pid}"));
        if (sig != _signature)
        {
            Rebuild(snap);
            _signature = sig;
        }
        foreach (var s in snap.Apps)
            if (_rows.TryGetValue(s.App.Id, out var row)) row.Update(s);
        foreach (var w in snap.Workspaces)
            if (_workspaceRows.TryGetValue(w.Workspace.Id, out var row)) row.Update(w);
        ApplyFilter();
    }

    private void Rebuild(Snapshot snap)
    {
        _list.SuspendLayout();
        var old = _list.Controls.Cast<Control>().ToList();
        _list.Controls.Clear();
        foreach (var c in old) c.Dispose();
        _rows.Clear();
        _workspaceRows.Clear();

        if (snap.Workspaces.Count > 0)
        {
            _list.Controls.Add(SectionLabel("WORKSPACES", snap.Workspaces.Count));
            foreach (var w in snap.Workspaces)
            {
                var row = new WorkspaceRow(w.Workspace, this);
                _workspaceRows[w.Workspace.Id] = row;
                _list.Controls.Add(row);
            }
        }

        _list.Controls.Add(SectionLabel("APPS", snap.Apps.Count));
        if (snap.Apps.Count == 0) _list.Controls.Add(EmptyState());
        foreach (var s in snap.Apps)
        {
            var row = new AppRow(s.App, this);
            _rows[s.App.Id] = row;
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
        var workspace = _last?.Workspaces.FirstOrDefault(w => w.Workspace.Id == _workspaceScope);
        if (_workspaceScope is not null && workspace is null) _workspaceScope = null;
        if (workspace is not null) _summary.Text = workspace.Workspace.Name + " · Esc returns to all projects";
        _filteredOut.Clear();
        var controls = _list.Controls.Cast<Control>().ToList();
        foreach (var c in controls)
        {
            bool outside = workspace is not null && (c is not AppRow appRow || !workspace.Workspace.Apps.Contains(appRow.App.Id));
            bool matchesWorkspace = c is AppRow a && _last?.Workspaces.Any(w => w.Workspace.Apps.Contains(a.App.Id) &&
                DockSearch.Matches(q, w.Workspace.Name)) == true;
            if (outside || c is IFilterable f && q.Length > 0 && !f.Matches(q) && !matchesWorkspace) _filteredOut.Add(c);
        }

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
        SelectSearchResult(scroll: false);
        if (Visible) FitToContent();
    }

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Up or Keys.Down)
        {
            e.SuppressKeyPress = true;
            int count = SearchResults().Count;
            if (count > 0) _resultIndex = (_resultIndex + (e.KeyCode == Keys.Down ? 1 : count - 1)) % count;
            SelectSearchResult();
            return;
        }
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        var results = SearchResults();
        if (results.Count == 0) return;
        if (results[_resultIndex] is AppRow app) Act(app.App, "open");
        else if (results[_resultIndex] is WorkspaceRow workspace) NavigateWorkspace(workspace.Workspace.Id);
    }

    private List<HoverRow> SearchResults() => _list.Controls.OfType<HoverRow>().Where(r => r is AppRow or WorkspaceRow && !_filteredOut.Contains(r)).ToList();

    private void SelectSearchResult(bool scroll = true)
    {
        var results = SearchResults();
        _resultIndex = Math.Clamp(_resultIndex, 0, Math.Max(0, results.Count - 1));
        foreach (var row in _list.Controls.OfType<HoverRow>()) row.SelectResult(results.Count > 0 && row == results[_resultIndex]);
        if (scroll && Visible && results.Count > 0) _list.ScrollControlIntoView(results[_resultIndex]);
    }

    internal void NavigateWorkspace(string? id)
    {
        _workspaceScope = id;
        _resultIndex = 0;
        _search.Clear();
        if (_last is { } snapshot) Render(snapshot);
        _search.Focus();
    }

    // ================================================================ adding apps

    private void ShowAddMenu()
    {
        var menu = DarkMenu.Create();
        menu.Items.Add(DarkMenu.Item("Add app…   (Ctrl+N)", AddApp));
        menu.Items.Add(DarkMenu.Item("Add from a launcher or project folder…", PickAndAdd));
        menu.Items.Add(DarkMenu.Item("Scan a folder for apps…", () => ScanFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(DarkMenu.Item("New workspace…", () => EditWorkspace(null)));
        menu.Items.Add(DarkMenu.Item("Import workspace presets…", ImportPresets));
        menu.Show(_addButton, new Point(0, _addButton.Height));
    }

    private void AddApp() => OpenEditor(new AppEntry(), "Add app", detectFrom: null, note: null, isNew: true);

    private void ImportPresets()
    {
        using var picker = new OpenFileDialog { Title = "Choose workspace presets", Filter = "Workspace presets|*.json", InitialDirectory = AppContext.BaseDirectory };
        if (ShowModal(() => picker.ShowDialog(OwnerOrNull())) != DialogResult.OK) return;
        try
        {
            var source = Workspace.WorkspacePresets.Read(picker.FileName);
            using var review = new PresetImportDialog(source);
            if (ShowModal(() => review.ShowDialog(OwnerOrNull())) != DialogResult.OK) return;
            _controller.Mutate(r => Workspace.WorkspacePresets.Import(r, source, review.Selected));
            SaveAndRefresh();
        }
        catch (Exception ex) { ShowModal(() => MessageBox.Show(this, ex.Message, "Could not import presets", MessageBoxButtons.OK, MessageBoxIcon.Warning)); }
    }

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
        _controller.Mutate(r => { foreach (var a in apps) r.Add(a); });
        SaveAndRefresh();
        AfterAdd(apps.Count == 1 ? apps[0].Name : $"{apps.Count} apps");
    }

    private void OpenEditor(AppEntry app, string title, string? detectFrom, string? note, bool isNew)
    {
        using var editor = new AppEditor(app, _controller.Registry, title, detectFrom, note);
        if (ShowModal(() => editor.ShowDialog(OwnerOrNull())) != DialogResult.OK) return;
        if (isNew) _controller.Mutate(r => r.Add(app));
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

    /// <summary>Raised when an operation finishes with a message worth showing (the tray shows it as a toast).</summary>
    public event Action<string, string, bool>? OperationFinished;

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

    /// <summary>The live registry entry for an app shown in a row (rows hold snapshot copies).</summary>
    private AppEntry? Live(AppEntry app)
    {
        lock (_controller.Registry.SyncRoot) return _controller.Registry.ById(app.Id)?.Clone();
    }

    internal async void Act(AppEntry shown, string action)
    {
        var app = Live(shown);
        if (app is null) return;
        OperationResult? result = null;
        try
        {
            switch (action)
            {
                case "open":
                    _controller.Open(app);
                    Hide();
                    break;
                case "start":
                    if (app.StartUnverified && !ConfirmUnverifiedStart(app)) return;
                    result = await _controller.StartAsync(app);
                    break;
                case "stop":
                    _requestPoll();
                    result = await _controller.StopAsync(app, ConfirmForeignStop);
                    break;
                case "restart":
                    _requestPoll();
                    result = await _controller.RestartAsync(app, ConfirmForeignStop);
                    break;
            }
        }
        catch (Exception ex)
        {
            result = OperationResult.Fail(ex.Message);
        }
        _requestPoll();
        if (result is { Ok: false } && action is "start" or "restart" && result.Message.StartsWith("Port :"))
        {
            ResolveConflict(app); // someone else holds the port: offer the ways out instead of just an error
            return;
        }
        if (result is { Ok: false })
            ShowModal(() => MessageBox.Show(this, result.Message, $"{app.Name}: {action}", MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    private bool ConfirmUnverifiedStart(AppEntry app) =>
        ShowModal(() => MessageBox.Show(this,
            $"{app.Name}'s start command was worked out from a running process and hasn't been tested yet:\n\n{app.Start}\n\n" +
            (app.Cwd is null ? "" : $"in {app.Cwd}\n\n") + "Run it now? (Use ⋯ → Edit… first if it doesn't look right.)",
            $"Start {app.Name}", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1)) == DialogResult.Yes;

    /// <summary>
    /// Asked by Stop when the process on an app's port can't be verified as that app. Never answered implicitly:
    /// the default button is No.
    /// </summary>
    private Task<bool> ConfirmForeignStop(AppEntry app, ProcessDetails holder, string reason)
    {
        string cmd = holder.CommandLine ?? holder.ExecutablePath ?? "";
        if (cmd.Length > 400) cmd = cmd[..400] + "…";
        var answer = ShowModal(() => MessageBox.Show(this,
            $"{holder.Name} (pid {holder.Pid}) is listening on {app.Name}'s port.\n\n{reason}\n\n{cmd}\n\n" +
            "Stop it anyway? Only say Yes if you're sure it's safe to close.",
            "Process not verified", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2));
        return Task.FromResult(answer == DialogResult.Yes);
    }

    internal void ShowAppMenu(AppEntry shown, Control anchor, Point at)
    {
        var app = Live(shown);
        if (app is null) return;
        var reg = _controller.Registry;
        var menu = DarkMenu.Create();
        string? folder = app.Folder;
        if (app.Port is int conflictPort && PortTable.GetListeners().Any(l => l.Port == conflictPort) &&
            !PortTable.GetListeners().Where(l => l.Port == conflictPort).Select(l => _controller.Classify(app, l)).Any(o => o.Ownership != Ownership.Foreign))
        {
            menu.Items.Add(DarkMenu.Item($"Resolve port conflict on :{conflictPort}…", () => ResolveConflict(app)));
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(DarkMenu.Item("View logs", () => LogViewer.ShowFor(app)));
        menu.Items.Add(DarkMenu.Item("Copy URL", () => Clipboard.SetText(app.EffectiveUrl!), app.EffectiveUrl is not null));
        menu.Items.Add(DarkMenu.Item("Open folder", () => Process.Start("explorer.exe", $"\"{folder}\""), folder is not null));
        menu.Items.Add(DarkMenu.Item("Open in VS Code", () => Shell.OpenInEditor(folder!), folder is not null && Shell.HasVsCode));
        menu.Items.Add(DarkMenu.Item("Open terminal here", () => Shell.OpenTerminal(folder!), folder is not null));
        menu.Items.Add(new ToolStripSeparator());

        var groups = new ToolStripMenuItem("Add to workspace");
        List<WorkspaceEntry> groupList;
        int index, count;
        lock (reg.SyncRoot)
        {
            groupList = reg.Workspaces.ToList();
            index = reg.Apps.FindIndex(a => a.Id == app.Id);
            count = reg.Apps.Count;
        }
        foreach (var g in groupList)
        {
            var gi = DarkMenu.Item(g.Name, () => ToggleWorkspaceMember(g.Id, app.Id));
            gi.Checked = g.Apps.Contains(app.Id);
            groups.DropDownItems.Add(gi);
        }
        if (groupList.Count > 0) groups.DropDownItems.Add(new ToolStripSeparator());
        groups.DropDownItems.Add(DarkMenu.Item("New workspace…", () => EditWorkspace(null, app.Id)));
        menu.Items.Add(groups);

        menu.Items.Add(DarkMenu.Item("Move up", () => MoveApp(app.Id, -1), index > 0));
        menu.Items.Add(DarkMenu.Item("Move down", () => MoveApp(app.Id, 1), index >= 0 && index < count - 1));
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
        if (ShowModal(() => editor.ShowDialog(OwnerOrNull())) != DialogResult.OK) return;
        // Workspaces refer to apps by Id, so renaming needs no follow-up.
        _controller.Mutate(r => r.Replace(copy));
        SaveAndRefresh();
    }

    private void DuplicateApp(AppEntry app)
    {
        var copy = app.Clone();
        copy.Id = "";
        copy.Name = _controller.Registry.UniqueName(app.Name + " copy");
        OpenEditor(copy, $"Duplicate {app.Name}", detectFrom: null, note: null, isNew: true);
    }

    internal void RemoveApp(AppEntry app)
    {
        var answer = ShowModal(() => MessageBox.Show(this,
            $"Remove {app.Name} from the dock?\n\nThis doesn't stop it or touch its files.", "Remove app",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2));
        if (answer != DialogResult.Yes) return;
        _controller.Mutate(r => r.Remove(app));
        SaveAndRefresh();
    }

    private void MoveApp(string id, int delta)
    {
        _controller.Mutate(r =>
        {
            int i = r.Apps.FindIndex(a => a.Id == id), j = i + delta;
            if (i < 0 || j < 0 || j >= r.Apps.Count) return;
            (r.Apps[i], r.Apps[j]) = (r.Apps[j], r.Apps[i]);
        });
        SaveAndRefresh();
    }

    // ================================================================ workspaces

    internal async void ActWorkspace(WorkspaceEntry ws, string action)
    {
        OperationResult result;
        try
        {
            if (action == "start")
            {
                List<AppEntry> unverified;
                lock (_controller.Registry.SyncRoot) unverified = ws.Apps.Select(_controller.Registry.ById).Where(a => a?.StartUnverified == true).Select(a => a!.Clone()).ToList();
                foreach (var app in unverified) if (!ConfirmUnverifiedStart(app)) return;
            }
            result = action == "start"
                ? await _controller.StartWorkspaceAsync(ws, _ => _requestPoll())
                : await _controller.StopWorkspaceAsync(ws, ConfirmForeignStop);
        }
        catch (Exception ex)
        {
            result = OperationResult.Fail(ex.Message);
        }
        _requestPoll();
        // Successful starts report readiness timing (slowest member); failures say what didn't start and why.
        if (!result.Ok || action == "start") OperationFinished?.Invoke(ws.Name, result.Message, result.Ok);
    }

    internal void ShowWorkspaceMenu(WorkspaceEntry ws, Control anchor, Point at)
    {
        var menu = DarkMenu.Create();
        menu.Items.Add(DarkMenu.Item("Open all", () =>
        {
            List<AppEntry> members;
            lock (_controller.Registry.SyncRoot)
                members = ws.Apps.Select(_controller.Registry.ById).Where(a => a?.EffectiveUrl is not null).Select(a => a!.Clone()).ToList();
            foreach (var a in members) _controller.Open(a);
        }));
        menu.Items.Add(DarkMenu.Item("Edit apps, dependencies and start order…", () => EditWorkspace(ws)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(DarkMenu.Item("Delete workspace (keeps the apps)", () =>
        {
            _controller.Mutate(r => r.Workspaces.RemoveAll(w => w.Id == ws.Id));
            SaveAndRefresh();
        }, danger: true));
        _modal++;
        menu.Closed += (_, _) => BeginInvoke(() => { _modal--; if (Visible) Activate(); });
        menu.Show(anchor, at);
    }

    private void EditWorkspace(WorkspaceEntry? ws, string? withAppId = null)
    {
        if (_controller.Registry.Apps.Count == 0)
        {
            ShowModal(() => MessageBox.Show(this, "Add some apps first; a workspace starts several apps together.", "New workspace"));
            return;
        }
        bool isNew = ws is null;
        var working = ws?.Clone() ?? new WorkspaceEntry { Id = Registry.NewId(), Apps = withAppId is null ? new() : new() { withAppId } };
        using var editor = new WorkspaceEditor(working, _controller.Registry, isNew ? "New workspace" : $"Edit {working.Name}");
        if (ShowModal(() => editor.ShowDialog(OwnerOrNull())) != DialogResult.OK) return;
        _controller.Mutate(r =>
        {
            int i = r.Workspaces.FindIndex(w => w.Id == working.Id);
            if (i >= 0) r.Workspaces[i] = working;
            else r.Workspaces.Add(working);
        });
        SaveAndRefresh();
    }

    private void ToggleWorkspaceMember(string wsId, string appId)
    {
        _controller.Mutate(r =>
        {
            if (r.WorkspaceById(wsId) is not { } w) return;
            if (w.Apps.Remove(appId))
            {
                w.DependsOn.Remove(appId);
                foreach (var deps in w.DependsOn.Values) deps.Remove(appId);
            }
            else w.Apps.Add(appId); // no dependencies until set in the workspace editor
        });
        SaveAndRefresh();
    }

    // ================================================================ port conflicts

    /// <summary>Opens the conflict wizard for an app whose port is taken, then carries out the choice.</summary>
    internal async void ResolveConflict(AppEntry shown)
    {
        var app = Live(shown);
        if (app?.Port is not int port) return;
        var conflict = PortTable.GetListeners().Where(l => app.AllPorts.Contains(l.Port))
            .FirstOrDefault(l => _controller.Classify(app, l).Ownership is Ownership.Foreign or Ownership.Unverified);
        if (conflict is not null) port = conflict.Port;
        using var wizard = new ConflictWizard(app, port, _controller.Registry, _controller);
        if (ShowModal(() => wizard.ShowDialog(OwnerOrNull())) != DialogResult.OK) return;

        switch (wizard.Choice)
        {
            case ConflictChoice.StopOtherApp when wizard.OtherApp is { } other:
                var stopped = await _controller.StopAsync(other, ConfirmForeignStop);
                if (!stopped.Ok) { ShowModal(() => MessageBox.Show(this, stopped.Message, $"Stop {other.Name}", MessageBoxButtons.OK, MessageBoxIcon.Warning)); return; }
                Act(app, "start");
                break;

            case ConflictChoice.StopHolder when wizard.Holder is { } holder:
                var killed = await _controller.KillAsync(holder); // identity revalidated inside the guard
                if (!killed.Ok) { ShowModal(() => MessageBox.Show(this, killed.Message, "Free the port", MessageBoxButtons.OK, MessageBoxIcon.Warning)); return; }
                Act(app, "start");
                break;

            case ConflictChoice.MovePort:
                _controller.Mutate(r =>
                {
                    if (r.ById(app.Id) is not { } live) return;
                    if (live.Port == port) live.Port = wizard.NewPort;
                    else live.ExtraPorts = live.ExtraPorts.Select(p => p == port ? wizard.NewPort : p).ToList();
                    if (Uri.TryCreate(live.Health, UriKind.Absolute, out var health) && health.Port == port)
                        live.Health = new UriBuilder(health) { Port = wizard.NewPort }.Uri.AbsoluteUri;
                    if (wizard.NewStart is not null) live.Start = wizard.NewStart;
                    if (wizard.NewUrl is not null) live.Url = wizard.NewUrl;
                });
                SaveAndRefresh();
                OperationFinished?.Invoke(app.Name, $"Moved to :{wizard.NewPort}." +
                    (wizard.NewStart is null && app.Start is not null ? " Remember to change the port in its launcher/config too." : " Press Start when ready."), true);
                break;
        }
        _requestPoll();
    }

    // ================================================================ discovered & lookup

    internal void Ignore(int port)
    {
        _controller.Mutate(r => { if (!r.IgnoredPorts.Contains(port)) r.IgnoredPorts.Add(port); });
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
            string? owner;
            lock (_controller.Registry.SyncRoot) owner = _controller.Registry.Apps.FirstOrDefault(a => a.AllPorts.Contains(port))?.Name;
            var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Surface };
            var lbl = new Label
            {
                Text = $"{d?.Name ?? "?"} · pid {g.Key} · {addrs}" + (owner is not null ? $" · configured for {owner}" : ""),
                AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, S(6), S(6), 0),
            };
            Tips.SetToolTip(lbl, d?.CommandLine ?? d?.ExecutablePath ?? d?.Restriction ?? "");
            line.Controls.Add(lbl);
            // Capture the identity now; the kill revalidates it, so a recycled PID is never hit.
            if (d?.Identity is { } id && ProcessGuard.WhyProtected(id) is null)
                line.Controls.Add(Theme.Button("Kill", (_, _) => ConfirmKill(port, id), danger: true));
            else if (d?.Identity is { } pid && ProcessGuard.WhyProtected(pid) is { } why)
                line.Controls.Add(new Label { Text = $"protected: {why}", ForeColor = Muted, Font = Small, AutoSize = true, Margin = new Padding(0, S(7), 0, 0) });
            AddLookupLine(line);
        }
    }

    private void AddLookupLine(Control c)
    {
        _lookupResult.Controls.Add(c);
        _footer.Height = S(48) + _lookupResult.Controls.Cast<Control>().Sum(x => x.Height + x.Margin.Vertical) + S(8);
        if (Visible) FitToContent();
    }

    internal async void ConfirmKill(int port, ProcessIdentity? identity)
    {
        if (identity is null || !identity.IsAlive())
        {
            string message = identity is null
                ? "Windows doesn't allow Localhost Dock to inspect this process (it's elevated, protected or belongs to another user), " +
                  "so it can't be verified or stopped from here. Use an elevated Task Manager if you're sure."
                : "That process has already exited.";
            ShowModal(() => MessageBox.Show(this, message, "Kill", MessageBoxButtons.OK, MessageBoxIcon.Information));
            _requestPoll();
            return;
        }
        if (ProcessGuard.WhyProtected(identity) is { } why)
        {
            ShowModal(() => MessageBox.Show(this, $"Localhost Dock won't stop {identity}: {why}.", "Kill", MessageBoxButtons.OK, MessageBoxIcon.Information));
            return;
        }

        var d = ProcessInfo.Get(identity.Pid, fresh: true);
        string detail = d?.CommandLine ?? d?.ExecutablePath ?? d?.Restriction ?? "";
        if (detail.Length > 400) detail = detail[..400] + "…";
        var answer = ShowModal(() => MessageBox.Show(this,
            $"Stop {identity} and its child processes?\n\nIt is listening on :{port}. It will be asked to exit first, then forced after 5 seconds.\n\n{detail}",
            "Confirm kill", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2));
        if (answer != DialogResult.Yes) return;

        var result = await _controller.KillAsync(identity);
        if (!result.Ok)
            ShowModal(() => MessageBox.Show(this, result.Message, "Kill", MessageBoxButtons.OK, MessageBoxIcon.Warning));
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
        try
        {
            lock (_controller.Registry.SyncRoot) _controller.Registry.Save();
        }
        catch (RegistryConflictException)
        {
            // Someone edited apps.json by hand meanwhile: never overwrite their edit. Reload theirs instead.
            ShowModal(() => MessageBox.Show(this,
                "apps.json was changed outside Localhost Dock, so your last change here wasn't saved (to avoid overwriting that edit).\n\n" +
                "The dock has reloaded the file; please redo the change.",
                "apps.json changed on disk", MessageBoxButtons.OK, MessageBoxIcon.Information));
            RegistryReloadRequested?.Invoke();
        }
        catch (Exception ex)
        {
            ShowModal(() => MessageBox.Show(this, ex.Message, "Could not save apps.json", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
        ForceRebuild();
        _requestPoll();
    }

    /// <summary>Raised when the dock needs the registry reloaded from disk (TrayApp owns reloading).</summary>
    public event Action? RegistryReloadRequested;

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
