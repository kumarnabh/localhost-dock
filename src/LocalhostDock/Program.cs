using LocalhostDock.Integration;
using LocalhostDock.UI;

namespace LocalhostDock;

internal static class Program
{
    /// <summary>
    /// LocalhostDock.exe [--show] [--add &lt;launcher-or-folder&gt;] [--selftest]
    /// If the dock is already running, the arguments are handed to it (so --add opens its Add dialog).
    /// --selftest checks that this build can run on this machine and exits (0 = OK); the installer uses it
    /// before replacing a working installation.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase)) return SelfTest();

        using var instance = new SingleInstance();
        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            if (!instance.IsFirst) SingleInstance.Forward(new[] { "--exit" });
            return 0;
        }
        if (!instance.IsFirst)
        {
            if (!SingleInstance.Forward(args.Length > 0 ? args : new[] { "--show" }))
                MessageBox.Show("Localhost Dock is already running. Look for it in the system tray.",
                    "Localhost Dock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        var app = new TrayApp(args);
        instance.Listen(app.HandleArgs);
        Application.Run(app);
        return 0;
    }

    private static int SelfTest()
    {
        try
        {
            Net.PortTable.GetListeners();
            if (Net.ProcessIdentity.Capture(Environment.ProcessId) is null) return 2;
            string example = Path.Combine(AppContext.BaseDirectory, "apps.example.json");
            if (!File.Exists(example)) return 3;
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "integrations", "aura", "localhost-dock.mjs")) ||
                !File.Exists(Path.Combine(AppContext.BaseDirectory, "presets", "nabh-projects.example.json"))) return 4;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(example),
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            using var icon = UI.Theme.TrayIcon(UI.Theme.Up);
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
