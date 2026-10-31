using LocalhostDock.Config;
using LocalhostDock.Workspace;
using static LocalhostDock.UI.Theme;

namespace LocalhostDock.UI;

/// <summary>
/// Create / edit a workspace: its apps, what each one needs to be ready first, what happens when a member fails,
/// and how many may start at once. Shows the resulting start order live.
/// </summary>
internal sealed class WorkspaceEditor : Form
{
    private readonly WorkspaceEntry _ws;
    private readonly Registry _registry;
    private readonly TextBox _name;
    private readonly CheckedListBox _members, _deps;
    private readonly Label _depsTitle, _order;
    private readonly ComboBox _onFailure;
    private readonly NumericUpDown _parallel;
    private readonly Dictionary<string, List<string>> _dependsOn;
    private readonly List<(string Id, string Name)> _apps;
    private bool _loadingDeps;

    public WorkspaceEditor(WorkspaceEntry ws, Registry registry, string title)
    {
        _ws = ws;
        _registry = registry;
        _dependsOn = ws.DependsOn.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        lock (registry.SyncRoot) _apps = registry.Apps.Select(a => (a.Id, a.Name)).ToList();

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(S(640), S(520));
        Padding = new Padding(S(14));

        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true }; // label | field, one setting per row
        top.Controls.Add(Caption("Name"));
        _name = new TextBox { Text = ws.Name, Width = S(220), PlaceholderText = "e.g. Shop dev" };
        top.Controls.Add(_name);
        top.Controls.Add(Caption("If a member fails"));
        _onFailure = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(230) };
        _onFailure.Items.AddRange(new object[] { "stop starting anything else", "start whatever doesn't depend on it" });
        _onFailure.SelectedIndex = ws.OnFailure == "continue" ? 1 : 0;
        top.Controls.Add(_onFailure);
        top.Controls.Add(Caption("Start at once"));
        _parallel = new NumericUpDown { Minimum = 1, Maximum = 16, Value = Math.Clamp(ws.MaxParallel, 1, 16), Width = S(60) };
        top.Controls.Add(_parallel);

        // Left: members (ticked = in the workspace). Right: what the selected member depends on.
        var left = new Panel { Dock = DockStyle.Left, Width = S(290), Padding = new Padding(0, S(10), S(8), 0) };
        left.Controls.Add(_members = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false });
        left.Controls.Add(Caption("Apps in this workspace", dock: true));
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(8), S(10), 0, 0) };
        right.Controls.Add(_deps = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false });
        right.Controls.Add(_depsTitle = Caption("Select an app on the left to set what it needs first", dock: true));

        foreach (var id in ws.Apps.Where(id => _apps.Any(a => a.Id == id))) _members.Items.Add(new Item(id, NameOf(id)), true);
        foreach (var a in _apps.Where(a => !ws.Apps.Contains(a.Id))) _members.Items.Add(new Item(a.Id, a.Name), false);

        _members.SelectedIndexChanged += (_, _) => LoadDeps();
        _members.ItemCheck += (_, _) => BeginInvoke(() => { LoadDeps(); UpdateOrder(); });
        _deps.ItemCheck += (_, e) =>
        {
            if (_loadingDeps || _members.SelectedItem is not Item member) return;
            var dep = (Item)_deps.Items[e.Index];
            if (!_dependsOn.TryGetValue(member.Id, out var list)) _dependsOn[member.Id] = list = new();
            if (e.NewValue == CheckState.Checked) { if (!list.Contains(dep.Id)) list.Add(dep.Id); }
            else list.Remove(dep.Id);
            BeginInvoke(UpdateOrder);
        };

        _order = new Label { Dock = DockStyle.Bottom, Height = S(56), ForeColor = Muted, Font = Small, Padding = new Padding(0, S(8), 0, 0) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, S(6), 0, 0) };
        var save = Theme.Button("Save", (_, _) => Save(), primary: true);
        buttons.Controls.Add(save);
        buttons.Controls.Add(Theme.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel));
        AcceptButton = save;

        var middle = new Panel { Dock = DockStyle.Fill };
        middle.Controls.Add(right);
        middle.Controls.Add(left);
        Controls.Add(middle);
        Controls.Add(_order);
        Controls.Add(buttons);
        Controls.Add(top);
        Theme.Dark(this);
        Shown += (_, _) => { if (_members.Items.Count > 0) _members.SelectedIndex = 0; UpdateOrder(); };
    }

    private sealed record Item(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private string NameOf(string id) => _apps.FirstOrDefault(a => a.Id == id).Name ?? id;

    private List<string> CheckedIds() => _members.CheckedItems.Cast<Item>().Select(i => i.Id).ToList();

    private void LoadDeps()
    {
        _loadingDeps = true;
        _deps.Items.Clear();
        if (_members.SelectedItem is Item member && CheckedIds().Contains(member.Id))
        {
            _depsTitle.Text = $"{member.Name} needs these to be ready first:";
            var mine = _dependsOn.TryGetValue(member.Id, out var l) ? l : new();
            foreach (var other in CheckedIds().Where(id => id != member.Id)) _deps.Items.Add(new Item(other, NameOf(other)), mine.Contains(other));
        }
        else _depsTitle.Text = "Tick an app on the left, then select it to set what it needs first";
        _loadingDeps = false;
    }

    private WorkspaceEntry Draft()
    {
        var members = CheckedIds();
        return new WorkspaceEntry
        {
            Id = _ws.Id, Name = _name.Text.Trim(), Apps = members,
            DependsOn = _dependsOn.Where(kv => members.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Where(members.Contains).ToList())
                .Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
            OnFailure = _onFailure.SelectedIndex == 1 ? "continue" : "stop",
            MaxParallel = (int)_parallel.Value,
        };
    }

    private void UpdateOrder()
    {
        var draft = Draft();
        if (draft.Apps.Count == 0) { _order.Text = "Tick the apps that belong to this workspace."; return; }
        if (WorkspacePlan.FindCycle(draft) is { } cycle)
        {
            _order.ForeColor = Down;
            _order.Text = $"These depend on each other in a loop, so they can't start: {string.Join(", ", cycle.Select(NameOf))}.";
            return;
        }
        _order.ForeColor = Muted;
        var levels = WorkspacePlan.Levels(draft);
        _order.Text = "Start order: " + string.Join("  →  ", levels.Select(l => string.Join(" + ", l.Select(NameOf)))) +
                      "\nApps joined by + start at the same time. Stop goes in reverse.";
    }

    private void Save()
    {
        var draft = Draft();
        if (draft.Name.Length == 0) { MessageBox.Show(this, "Give the workspace a name.", Text); return; }
        if (_registry.Workspaces.Any(w => w.Id != _ws.Id && w.Name.Equals(draft.Name, StringComparison.OrdinalIgnoreCase)))
        { MessageBox.Show(this, $"There's already a workspace called “{draft.Name}”.", Text); return; }
        if (draft.Apps.Count == 0) { MessageBox.Show(this, "Tick at least one app.", Text); return; }
        if (WorkspacePlan.FindCycle(draft) is not null) { MessageBox.Show(this, "Remove the dependency loop first.", Text); return; }

        _ws.Name = draft.Name;
        _ws.Apps = draft.Apps;
        _ws.DependsOn = draft.DependsOn;
        _ws.OnFailure = draft.OnFailure;
        _ws.MaxParallel = draft.MaxParallel;
        DialogResult = DialogResult.OK;
    }

    private static Label Caption(string text, bool dock = false) => new()
    {
        Text = text, ForeColor = Muted, AutoSize = !dock, Dock = dock ? DockStyle.Top : DockStyle.None,
        Height = dock ? S(22) : 0, Margin = new Padding(0, S(6), S(8), 0),
    };
}
