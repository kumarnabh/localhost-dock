using LocalhostDock.Integration;
using LocalhostDock.UI;

namespace LocalhostDock;

internal static class Program
{
    /// <summary>
    /// LocalhostDock.exe [--show] [--add &lt;launcher-or-folder&gt;]
    /// If the dock is already running, the arguments are handed to it (so --add opens its Add dialog).
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        using var instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            if (!SingleInstance.Forward(args.Length > 0 ? args : new[] { "--show" }))
                MessageBox.Show("Localhost Dock is already running. Look for it in the system tray.",
                    "Localhost Dock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        var app = new TrayApp(args);
        instance.Listen(app.HandleArgs);
        Application.Run(app);
    }
}
