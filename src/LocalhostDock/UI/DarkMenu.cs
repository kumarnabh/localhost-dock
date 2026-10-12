namespace LocalhostDock.UI;

/// <summary>Context menus in the dock's dark palette.</summary>
internal static class DarkMenu
{
    public static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new Colors()) { RoundedEdges = false },
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            ShowImageMargin = false,
        };
        menu.ItemAdded += (_, e) => { if (e.Item is not null) Style(e.Item); };
        return menu;
    }

    public static ToolStripMenuItem Item(string text, Action onClick, bool enabled = true, bool danger = false)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => onClick()) { Enabled = enabled };
        if (danger) item.ForeColor = Theme.Down;
        return item;
    }

    private static void Style(ToolStripItem item)
    {
        if (item.ForeColor == SystemColors.ControlText) item.ForeColor = Theme.Text;
        item.Padding = new Padding(Theme.S(2), Theme.S(3), Theme.S(2), Theme.S(3));
        if (item is ToolStripMenuItem { DropDown: ToolStripDropDownMenu dd })
        {
            dd.Renderer = new ToolStripProfessionalRenderer(new Colors()) { RoundedEdges = false };
            dd.BackColor = Theme.Surface;
            dd.ForeColor = Theme.Text;
            dd.ShowImageMargin = false;
            foreach (ToolStripItem child in dd.Items) StyleChild(dd, child);
            dd.ItemAdded += (_, e) => { if (e.Item is not null) StyleChild(dd, e.Item); };
        }
    }

    private static void StyleChild(ToolStripDropDownMenu dd, ToolStripItem child)
    {
        Style(child);
        // Only reserve the check-mark column when something in the submenu is actually checked.
        if (child is ToolStripMenuItem { Checked: true }) dd.ShowImageMargin = true;
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
        public override Color MenuItemBorder => Theme.Border;
        public override Color MenuBorder => Theme.Border;
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Surface;
        public override Color CheckBackground => Theme.SurfaceHover;
        public override Color CheckSelectedBackground => Theme.SurfaceHover;
        public override Color CheckPressedBackground => Theme.SurfaceHover;
        public override Color MenuItemPressedGradientBegin => Theme.SurfaceHover;
        public override Color MenuItemPressedGradientEnd => Theme.SurfaceHover;
    }
}
