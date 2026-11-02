using LocalhostDock.Config;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

internal sealed class PresetImportDialog : Form
{
    private readonly CheckedListBox _choices;
    public List<string> Selected => _choices.CheckedItems.Cast<WorkspaceEntry>().Select(w => w.Id).ToList();

    public PresetImportDialog(Config.Registry source)
    {
        Text = "Import workspace presets";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(S(780), S(540));
        MinimumSize = new Size(S(600), S(420));
        Padding = new Padding(S(14));
        _choices = new CheckedListBox { Dock = DockStyle.Top, Height = S(110), CheckOnClick = true, DisplayMember = "Name" };
        foreach (var workspace in source.Workspaces) _choices.Items.Add(workspace, false);
        var preview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, TabStop = false, Font = Mono };
        _choices.SelectedIndexChanged += (_, _) =>
        {
            if (_choices.SelectedItem is not WorkspaceEntry w) return;
            preview.Text = string.Join(Environment.NewLine + Environment.NewLine, w.Apps.Select(id =>
            {
                var app = source.ById(id)!;
                return $"{app.Name} · :{app.Port}\r\nStart: {app.Start}\r\nFolder: {app.Cwd}\r\nHealth: {app.Health ?? "TCP only"}\r\nNeeds: {string.Join(", ", w.DependenciesOf(id).Select(d => source.ById(d)!.Name))}";
            }));
        };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = S(64), FlowDirection = FlowDirection.RightToLeft };
        var import = Theme.Button("Import selected", (_, _) => DialogResult = DialogResult.OK, primary: true);
        import.Enabled = false;
        _choices.ItemCheck += (_, e) => import.Enabled = _choices.CheckedItems.Count + (e.NewValue == CheckState.Checked ? 1 : -1) > 0;
        footer.Controls.Add(import);
        footer.Controls.Add(Theme.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel));
        var hint = new Label { Dock = DockStyle.Top, Height = S(46), Text = "Review each workspace and tick the ones to add. Import preserves existing entries. Services start only when you choose Start.", ForeColor = Muted };
        Controls.Add(preview);
        Controls.Add(_choices);
        Controls.Add(hint);
        Controls.Add(footer);
        Theme.Dark(this);
        ActiveControl = _choices;
        if (_choices.Items.Count > 0) _choices.SelectedIndex = 0;
    }
}
