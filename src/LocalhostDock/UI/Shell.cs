using System.Diagnostics;

namespace LocalhostDock.UI;

/// <summary>Small helpers for opening a folder in other tools.</summary>
internal static class Shell
{
    private static readonly Lazy<string?> VsCode = new(() => FindOnPath("code.cmd"));
    private static readonly Lazy<string?> WindowsTerminal = new(() =>
    {
        if (FindOnPath("wt.exe") is { } onPath) return onPath;
        string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        return File.Exists(alias) ? alias : null;
    });

    public static bool HasVsCode => VsCode.Value is not null;

    public static void OpenInEditor(string folder)
    {
        if (VsCode.Value is not { } code) return;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{code}\" \"{folder}\"\"")
        {
            CreateNoWindow = true, UseShellExecute = false,
        });
    }

    public static void OpenTerminal(string folder)
    {
        if (WindowsTerminal.Value is { } wt)
            Process.Start(new ProcessStartInfo(wt, $"-d \"{folder}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true });
    }

    private static string? FindOnPath(string exe)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
