using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;

namespace LocalhostDock.Net;

/// <summary>Details about a process, looked up through WMI (works for most non-elevated processes).</summary>
public sealed record ProcessDetails(int Pid, string Name, string? ExecutablePath, string? CommandLine, int ParentPid)
{
    public bool IsCritical => Pid <= 4 || ProcessInfo.CriticalNames.Contains(Name);
}

public static class ProcessInfo
{
    /// <summary>Processes we never offer to kill and never list as "discovered".</summary>
    public static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "svchost.exe", "spoolsv.exe", "MsMpEng.exe", "explorer.exe", "dwm.exe",
    };

    private static readonly ConcurrentDictionary<int, (DateTime At, ProcessDetails? Details)> Cache = new();

    public static ProcessDetails? Get(int pid, bool fresh = false)
    {
        if (!fresh && Cache.TryGetValue(pid, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(30))
            return hit.Details;

        ProcessDetails? details = Query(pid);
        Cache[pid] = (DateTime.UtcNow, details);
        return details;
    }

    private static ProcessDetails? Query(int pid)
    {
        if (pid == 0) return new ProcessDetails(0, "Idle", null, null, 0);
        if (pid == 4) return new ProcessDetails(4, "System", null, null, 0);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name, ExecutablePath, CommandLine, ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject mo in searcher.Get())
            {
                return new ProcessDetails(pid,
                    mo["Name"] as string ?? "?",
                    mo["ExecutablePath"] as string,
                    mo["CommandLine"] as string,
                    Convert.ToInt32(mo["ParentProcessId"] ?? 0));
            }
        }
        catch
        {
            // Fall through to the Process API.
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            return new ProcessDetails(pid, p.ProcessName + ".exe", null, null, 0);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Kills a process and all of its children (taskkill /T /F). Returns an error message or null.</summary>
    public static string? KillTree(int pid)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return "Refusing to kill a system process or Localhost Dock itself.";
        try
        {
            var psi = new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            Cache.TryRemove(pid, out _);
            return p.ExitCode == 0 ? null : err.Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
