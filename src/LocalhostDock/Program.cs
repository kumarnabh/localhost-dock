using LocalhostDock.UI;

namespace LocalhostDock;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool show = args.Contains("--show", StringComparer.OrdinalIgnoreCase);
        using var mutex = new Mutex(true, @"Local\LocalhostDock.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Localhost Dock is already running. Look for it in the system tray.",
                "Localhost Dock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(showOnStart: show));
    }
}
