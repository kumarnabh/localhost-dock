using System.Collections.Concurrent;
using System.Management;

namespace LocalhostDock.Net;

/// <summary>Details about a process. <see cref="Identity"/> pins it to one specific process instance.</summary>
public sealed record ProcessDetails(int Pid, string Name, string? ExecutablePath, string? CommandLine, int ParentPid)
{
    public ProcessIdentity? Identity { get; init; }

    /// <summary>Protected from termination (system, service, critical, the dock itself).</summary>
    public bool IsCritical => Identity is { } id ? ProcessGuard.WhyProtected(id) is not null : ProcessGuard.WhyProtected(Pid, Name) is not null;

    /// <summary>Why details are incomplete, e.g. the process is elevated.</summary>
    public string? Restriction { get; init; }
}

public static class ProcessInfo
{
    // Keyed by PID *and* creation time: a recycled PID never inherits another process's command line.
    private static readonly ConcurrentDictionary<(int Pid, long Created), (DateTime At, ProcessDetails Details)> Cache = new();

    public static ProcessDetails? Get(int pid, bool fresh = false)
    {
        if (pid == 0) return new ProcessDetails(0, "Idle", null, null, 0);
        if (pid == 4) return new ProcessDetails(4, "System", null, null, 0);

        var id = ProcessIdentity.Capture(pid);
        if (id is null)
        {
            // Can't even open it: exited, or protected (e.g. some system processes).
            string? name = ProcessTree.NameOf(pid);
            return name is null ? null : new ProcessDetails(pid, name, null, null, 0)
            {
                Restriction = "Windows doesn't allow reading this process's details (it's protected or elevated).",
            };
        }

        var key = (pid, id.CreationTicks);
        if (!fresh && Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(2))
            return hit.Details;

        var details = Query(id);
        Cache[key] = (DateTime.UtcNow, details);
        if (Cache.Count > 512)
            foreach (var stale in Cache.Where(kv => DateTime.UtcNow - kv.Value.At > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList())
                Cache.TryRemove(stale, out _);
        return details;
    }

    private static ProcessDetails Query(ProcessIdentity id)
    {
        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                new ObjectQuery($"SELECT Name, ExecutablePath, CommandLine, ParentProcessId FROM Win32_Process WHERE ProcessId = {id.Pid}"), options);
            foreach (ManagementObject mo in searcher.Get())
            {
                string? cmd = mo["CommandLine"] as string;
                return new ProcessDetails(id.Pid, mo["Name"] as string ?? id.Name,
                    mo["ExecutablePath"] as string ?? id.ImagePath, cmd, Convert.ToInt32(mo["ParentProcessId"] ?? 0))
                {
                    Identity = id,
                    Restriction = cmd is null ? "Its command line isn't readable (the process is elevated or belongs to another user)." : null,
                };
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or TimeoutException)
        {
            return new ProcessDetails(id.Pid, id.Name, id.ImagePath, null, 0)
            {
                Identity = id,
                Restriction = $"Windows Management Instrumentation couldn't describe it ({ex.GetType().Name}).",
            };
        }
        return new ProcessDetails(id.Pid, id.Name, id.ImagePath, null, 0) { Identity = id };
    }
}
