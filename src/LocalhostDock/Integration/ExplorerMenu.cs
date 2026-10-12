namespace LocalhostDock.Integration;

/// <summary>
/// Optional "Add to Localhost Dock" entry in Explorer's right-click menu for folders and .cmd/.bat/.ps1 files.
/// Per-user only (HKCU\Software\Classes); toggled from the tray menu, off by default.
/// </summary>
internal static class ExplorerMenu
{
    private const string Verb = "LocalhostDock.Add";
    private const string Label = "Add to Localhost Dock";

    private static readonly string[] Roots =
    {
        @"Software\Classes\Directory\shell\" + Verb,
        @"Software\Classes\SystemFileAssociations\.cmd\shell\" + Verb,
        @"Software\Classes\SystemFileAssociations\.bat\shell\" + Verb,
        @"Software\Classes\SystemFileAssociations\.ps1\shell\" + Verb,
    };

    public static bool IsInstalled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Roots[0]);
        return key is not null;
    }

    public static void Install()
    {
        string exe = Environment.ProcessPath!;
        foreach (string root in Roots)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root);
            key.SetValue("", Label);
            key.SetValue("Icon", $"\"{exe}\"");
            using var cmd = key.CreateSubKey("command");
            cmd.SetValue("", $"\"{exe}\" --add \"%1\"");
        }
    }

    public static void Uninstall()
    {
        foreach (string root in Roots)
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
    }
}
