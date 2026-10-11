using LocalhostDock.Config;

namespace LocalhostDock.UI;

/// <summary>Add / edit dialog for one registry entry.</summary>
internal sealed class AppEditor : Form
{
    private readonly AppEntry _app;
    private readonly TextBox _name, _port, _extra, _url, _start, _stop, _cwd;
    private readonly ComboBox _window;

    public AppEditor(AppEntry app, string title)
    {
        _app = app;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Font = Theme.Body;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _name = Field(grid, "Name", app.Name);
        _port = Field(grid, "Port", app.Port?.ToString() ?? "");
        _extra = Field(grid, "Extra ports", string.Join(", ", app.ExtraPorts), "comma-separated, freed on Stop");
        _url = Field(grid, "URL", app.Url ?? "", "blank = http://localhost:{port}");
        _start = Field(grid, "Start", app.Start ?? "", "path to your .cmd launcher, or any command", browse: true);
        _stop = Field(grid, "Stop", app.Stop ?? "", "optional; blank = kill whatever owns the ports", browse: true);
        _cwd = Field(grid, "Working dir", app.Cwd ?? "", "blank = the launcher's folder", folder: true);

        grid.Controls.Add(new Label { Text = "Window", AutoSize = true, Anchor = AnchorStyles.Left });
        _window = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _window.Items.AddRange(new object[] { "minimized", "normal", "hidden" });
        _window.SelectedItem = _window.Items.Contains(app.Window) ? app.Window : "minimized";
        grid.Controls.Add(_window);
        grid.Controls.Add(new Label());

        var ok = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
        ok.Click += (_, _) => Save();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
    }

    private static TextBox Field(TableLayoutPanel grid, string label, string value, string? hint = null,
        bool browse = false, bool folder = false)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
        var box = new TextBox { Text = value, Width = 374, PlaceholderText = hint ?? "" };
        grid.Controls.Add(box);
        if (browse || folder)
        {
            var b = new Button { Text = "…", Width = 30 };
            b.Click += (_, _) =>
            {
                if (folder)
                {
                    using var dlg = new FolderBrowserDialog { SelectedPath = box.Text };
                    if (dlg.ShowDialog() == DialogResult.OK) box.Text = dlg.SelectedPath;
                }
                else
                {
                    using var dlg = new OpenFileDialog { Filter = "Launchers|*.cmd;*.bat;*.ps1;*.exe|All files|*.*" };
                    if (dlg.ShowDialog() == DialogResult.OK) box.Text = dlg.FileName;
                }
            };
            grid.Controls.Add(b);
        }
        else grid.Controls.Add(new Label());
        return box;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { Warn("Give the app a name."); return; }

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

        _app.Name = _name.Text.Trim();
        _app.Port = port;
        _app.ExtraPorts = extra;
        _app.Url = NullIfBlank(_url.Text);
        _app.Start = NullIfBlank(_start.Text);
        _app.Stop = NullIfBlank(_stop.Text);
        _app.Cwd = NullIfBlank(_cwd.Text);
        _app.Window = (string)_window.SelectedItem!;
        DialogResult = DialogResult.OK;
    }

    private void Warn(string msg) => MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
