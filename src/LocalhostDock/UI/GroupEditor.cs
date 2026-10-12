using LocalhostDock.Config;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

/// <summary>Create / edit a start group: a name plus an ordered set of apps.</summary>
internal sealed class GroupEditor : Form
{
    private readonly GroupEntry _group;
    private readonly Registry _registry;
    private readonly TextBox _name;
    private readonly CheckedListBox _apps;

    public GroupEditor(GroupEntry group, Registry registry, string title)
    {
        _group = group;
        _registry = registry;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(S(380), S(420));
        Padding = new Padding(S(14));

        var nameLabel = new Label { Text = "Group name", ForeColor = Muted, AutoSize = true, Dock = DockStyle.Top };
        _name = new TextBox { Text = group.Name, Dock = DockStyle.Top, PlaceholderText = "e.g. Shop dev" };
        var appsLabel = new Label
        {
            Text = "Apps (started top to bottom, each waits for the previous one's port; stopped in reverse)",
            ForeColor = Muted, AutoSize = false, Height = S(38), Dock = DockStyle.Top, Padding = new Padding(0, S(10), 0, 0),
        };
        _apps = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };

        // Members first, in their order, then everything else.
        foreach (var n in group.Apps.Where(n => registry.Find(n) is not null)) _apps.Items.Add(registry.Find(n)!.Name, true);
        foreach (var a in registry.Apps.Where(a => !group.Apps.Contains(a.Name, StringComparer.OrdinalIgnoreCase))) _apps.Items.Add(a.Name, false);

        var order = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(S(6), 0, 0, 0) };
        order.Controls.Add(Theme.Button("Move up", (_, _) => MoveSelected(-1)));
        order.Controls.Add(Theme.Button("Move down", (_, _) => MoveSelected(1)));

        var middle = new Panel { Dock = DockStyle.Fill };
        middle.Controls.Add(_apps);
        middle.Controls.Add(order);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, S(10), 0, 0) };
        var save = Theme.Button("Save", (_, _) => Save(), primary: true);
        buttons.Controls.Add(save);
        buttons.Controls.Add(Theme.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel));
        AcceptButton = save;

        Controls.Add(middle);
        Controls.Add(appsLabel);
        Controls.Add(_name);
        Controls.Add(nameLabel);
        Controls.Add(buttons);
        Theme.Dark(this);
    }

    private void MoveSelected(int delta)
    {
        int i = _apps.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _apps.Items.Count) return;
        object item = _apps.Items[i];
        bool check = _apps.GetItemChecked(i);
        _apps.Items.RemoveAt(i);
        _apps.Items.Insert(j, item);
        _apps.SetItemChecked(j, check);
        _apps.SelectedIndex = j;
    }

    private void Save()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0) { MessageBox.Show(this, "Give the group a name.", Text); return; }
        if (_registry.Groups.Any(g => g != _group && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { MessageBox.Show(this, $"There's already a group called “{name}”.", Text); return; }
        if (_apps.CheckedItems.Count == 0) { MessageBox.Show(this, "Tick at least one app.", Text); return; }

        _group.Name = name;
        _group.Apps = _apps.CheckedItems.Cast<string>().ToList();
        DialogResult = DialogResult.OK;
    }
}
