using LocalhostDock.Config;
using LocalhostDock.Core;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

/// <summary>"Scan folder for apps": finds launchers and dev scripts under a folder and imports the ticked ones.</summary>
internal sealed class ImportDialog : Form
{
    private readonly Registry _registry;
    private readonly TextBox _folder;
    private readonly ListView _list;
    private readonly Label _status;
    private readonly Button _add, _scan;
    private CancellationTokenSource? _cts;

    public List<AppEntry> Selected { get; } = new();

    public ImportDialog(Registry registry, string? folder = null, List<Detection>? preloaded = null)
    {
        _registry = registry;
        Text = "Add apps from a folder";
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        MinimizeBox = false;
        Size = new Size(S(820), S(520));
        MinimumSize = new Size(S(560), S(360));
        Padding = new Padding(S(14));

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, S(8)) };
        top.Controls.Add(new Label { Text = "Folder", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, S(6), S(8), 0) });
        _folder = new TextBox { Width = S(480), Text = folder ?? registry.ScanFolders.LastOrDefault() ?? "", PlaceholderText = @"e.g. C:\dev — scans up to 3 levels deep" };
        top.Controls.Add(_folder);
        top.Controls.Add(Theme.Button("Browse…", (_, _) => Browse()));
        _scan = Theme.Button("Scan", async (_, _) => await ScanAsync(), primary: true);
        top.Controls.Add(_scan);

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, LabelEdit = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, GridLines = false,
        };
        _list.Columns.Add("Name (click to rename)", S(190));
        _list.Columns.Add("Port", S(80));
        _list.Columns.Add("Start", S(300));
        _list.Columns.Add("How it was found", S(320));
        // ListView headers ignore BackColor; draw them in the dark palette.
        _list.OwnerDraw = true;
        _list.DrawItem += (_, e) => e.DrawDefault = true;
        _list.DrawSubItem += (_, e) => e.DrawDefault = true;
        _list.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(Surface);
            using var line = new Pen(Border);
            e.Graphics.FillRectangle(bg, e.Bounds);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + S(4), e.Bounds.Right - 1, e.Bounds.Bottom - S(4));
            var r = e.Bounds;
            r.Inflate(-S(6), 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, Small, r, Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        _list.ItemChecked += (_, _) => UpdateButton();
        _list.MouseDoubleClick += (_, e) => _list.GetItemAt(e.X, e.Y)?.BeginEdit();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(44), Padding = new Padding(0, S(10), 0, 0) };
        _status = new Label { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        _add = Theme.Button("Add selected", (_, _) => Accept(), primary: true);
        buttons.Controls.Add(_add);
        buttons.Controls.Add(Theme.Button("Cancel", (_, _) => { _cts?.Cancel(); DialogResult = DialogResult.Cancel; }));
        bottom.Controls.Add(_status);
        bottom.Controls.Add(buttons);

        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(top);
        Theme.Dark(this);
        _list.HandleCreated += (_, _) => SetWindowTheme(_list.Handle, "DarkMode_Explorer", null);
        AcceptButton = _scan;
        CancelButton = null;

        if (preloaded is not null)
        {
            top.Visible = false;
            Text = $"Add apps from {Path.GetFileName(folder?.TrimEnd('\\')) ?? "folder"}";
            ShowResults(preloaded);
        }
        else
        {
            _status.Text = "Pick a folder that holds your projects (e.g. C:\\dev) and click Scan.";
            UpdateButton();
        }
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : "" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _folder.Text = dlg.SelectedPath;
            _ = ScanAsync();
        }
    }

    private async Task ScanAsync()
    {
        string root = _folder.Text.Trim().Trim('"');
        if (!Directory.Exists(root)) { _status.Text = "That folder doesn't exist."; return; }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _scan.Enabled = false;
        _list.Items.Clear();
        UseWaitCursor = true;
        string current = "";
        using var tick = new System.Windows.Forms.Timer { Interval = 150 };
        tick.Tick += (_, _) => _status.Text = "Scanning " + current;
        tick.Start();
        try
        {
            var found = await Task.Run(() => AppDetector.Scan(root, 3, token, d => current = d), token);
            if (!_registry.ScanFolders.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                _registry.ScanFolders.Add(root);
                if (_registry.ScanFolders.Count > 5) _registry.ScanFolders.RemoveAt(0);
            }
            ShowResults(found);
        }
        catch (OperationCanceledException) { }
        finally
        {
            tick.Stop();
            UseWaitCursor = false;
            _scan.Enabled = true;
        }
    }

    private void ShowResults(List<Detection> found)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        int already = 0;
        var names = new HashSet<string>(_registry.Apps.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var d in found)
        {
            if (IsRegistered(d.App)) { already++; continue; }
            var a = d.App;
            string name = a.Name;
            for (int i = 2; names.Contains(name); i++) name = $"{a.Name} {i}";
            names.Add(name);
            a.Name = name;

            var item = new ListViewItem(a.Name) { Tag = a, Checked = a.Port is not null };
            item.SubItems.Add(string.Join(" ", a.AllPorts.Select(p => ":" + p)));
            item.SubItems.Add(a.Start ?? "");
            item.SubItems.Add(d.Note);
            if (a.Port is null) item.ForeColor = Muted;
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        _status.Text = _list.Items.Count == 0
            ? already > 0 ? $"Everything found here ({already}) is already in your dock." : "No launchers or dev scripts found here."
            : $"Found {_list.Items.Count} app(s)" + (already > 0 ? $" ({already} already in your dock)" : "") +
              ". Ones without a port are unticked; you can set the port after adding.";
        UpdateButton();
    }

    private bool IsRegistered(AppEntry a) => _registry.Apps.Any(r =>
        (r.Start is not null && a.Start is not null && r.Start.Trim('"').Equals(a.Start.Trim('"'), StringComparison.OrdinalIgnoreCase) &&
         string.Equals(r.Cwd ?? "", a.Cwd ?? "", StringComparison.OrdinalIgnoreCase)) ||
        (a.Port is not null && r.Port == a.Port && string.Equals(r.Folder, a.Folder, StringComparison.OrdinalIgnoreCase)));

    private void UpdateButton()
    {
        int n = _list.CheckedItems.Count;
        _add.Text = n == 0 ? "Add selected" : $"Add {n} app{(n == 1 ? "" : "s")}";
        _add.Width = Math.Max(S(48), TextRenderer.MeasureText(_add.Text, Small).Width + S(18));
        _add.Enabled = n > 0;
    }

    private void Accept()
    {
        foreach (ListViewItem item in _list.CheckedItems)
        {
            var app = (AppEntry)item.Tag!;
            app.Name = string.IsNullOrWhiteSpace(item.Text) ? app.Name : item.Text.Trim();
            Selected.Add(app);
        }
        DialogResult = DialogResult.OK;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosing(e);
    }
}
