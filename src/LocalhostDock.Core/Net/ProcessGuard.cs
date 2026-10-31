using System.Diagnostics;

namespace LocalhostDock.Net;

public enum KillOutcome { Stopped, AlreadyExited, Refused, Failed }

public sealed record KillResult(KillOutcome Outcome, string Message)
{
    public bool Ok => Outcome is KillOutcome.Stopped or KillOutcome.AlreadyExited;
}

/// <summary>How hard Stop may push.</summary>
public enum StopPolicy
{
    /// <summary>Ask nicely (Ctrl+C / close window), then force after the timeout. Default.</summary>
    Graceful,
    /// <summary>Ask nicely only; never force-kill (for apps that must shut down cleanly).</summary>
    GracefulOnly,
    /// <summary>Force immediately.</summary>
    Force,
}

/// <summary>
/// The only code path in Localhost Dock that terminates processes. Every caller goes through here, and every
/// termination: (1) revalidates the exact process identity (PID + creation time + image) captured earlier,
/// (2) refuses protected processes (system, services, critical, the dock itself and its ancestors),
/// (3) asks the process tree to exit gracefully, and (4) force-kills only if the policy allows it,
/// re-checking each process's identity right before killing it.
/// </summary>
public static class ProcessGuard
{
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "lsaiso.exe", "svchost.exe", "spoolsv.exe", "MsMpEng.exe", "explorer.exe", "dwm.exe",
        "fontdrvhost.exe", "sihost.exe", "ctfmon.exe", "audiodg.exe", "MemCompression", "Secure System",
    };

    private static readonly object ConsoleLock = new();

    /// <summary>Why a process must not be terminated, or null if it may be (after confirmation/ownership checks).</summary>
    public static string? WhyProtected(ProcessIdentity id)
    {
        if (id.Pid <= 4) return "it's a core Windows process";
        if (ProcessTree.SelfAndAncestors().Contains(id.Pid)) return "it's Localhost Dock itself or the program that started it";
        if (CriticalNames.Contains(id.Name)) return $"{id.Name} is a Windows system process";
        if (Native.ProcessIdToSessionId(id.Pid, out int session) && session == 0) return $"{id.Name} runs as a Windows service";
        return null;
    }

    /// <summary>
    /// Protection check for a process we may not be able to open (e.g. a service): by PID, name and session.
    /// Unopenable processes that run as services or carry a critical name are protected.
    /// </summary>
    public static string? WhyProtected(int pid, string name)
    {
        if (ProcessIdentity.Capture(pid) is { } id) return WhyProtected(id);
        if (pid <= 4) return "it's a core Windows process";
        if (CriticalNames.Contains(name)) return $"{name} is a Windows system process";
        if (Native.ProcessIdToSessionId(pid, out int session) && session == 0) return $"{name} runs as a Windows service";
        return null;
    }

    public static bool IsProtected(int pid) => ProcessIdentity.Capture(pid) is not { } id || WhyProtected(id) is not null;

    /// <summary>
    /// Stops <paramref name="expected"/> and its descendants. Never touches a process whose identity no longer
    /// matches (PID reused) or that is protected.
    /// </summary>
    public static async Task<KillResult> TerminateTreeAsync(ProcessIdentity expected, StopPolicy policy, TimeSpan gracefulTimeout,
        CancellationToken ct = default)
    {
        var now = ProcessIdentity.Capture(expected.Pid);
        if (now is null) return new(KillOutcome.AlreadyExited, $"{expected} had already exited.");
        if (!expected.SameAs(now))
            return new(KillOutcome.Refused, $"PID {expected.Pid} now belongs to a different process ({now.Name}); nothing was stopped.");
        if (WhyProtected(now) is { } why) return new(KillOutcome.Refused, $"Not stopping {now}: {why}.");

        var tree = ProcessTree.Descendants(now).Where(p => WhyProtected(p) is null).ToList();

        if (policy != StopPolicy.Force)
        {
            AskToExit(now);
            if (await WaitForExitAsync(now, gracefulTimeout, ct))
            {
                // Children that outlived their parent are still the app's; they get the same treatment below.
                tree = tree.Where(p => p.IsAlive()).ToList();
                if (tree.Count == 0) return new(KillOutcome.Stopped, $"{now} exited gracefully.");
            }
            if (policy == StopPolicy.GracefulOnly)
                return now.IsAlive()
                    ? new(KillOutcome.Failed, $"{now} didn't exit within {gracefulTimeout.TotalSeconds:0} s, and this app's stop policy forbids force-killing it.")
                    : new(KillOutcome.Stopped, $"{now} exited gracefully (some child processes are still running).");
        }

        // Force: leaves first, revalidating each identity immediately before killing it.
        var failures = new List<string>();
        foreach (var p in tree)
        {
            var current = ProcessIdentity.Capture(p.Pid);
            if (current is null || !p.SameAs(current)) continue; // exited or PID reused: leave it alone
            try
            {
                using var proc = Process.GetProcessById(p.Pid);
                proc.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { /* exited meanwhile */ }
            catch (Exception ex) { failures.Add($"{p}: {Explain(ex)}"); }
        }

        await WaitForExitAsync(now, TimeSpan.FromSeconds(5), ct);
        if (now.IsAlive()) return new(KillOutcome.Failed, $"Couldn't stop {now}. {string.Join(" ", failures)}".Trim());
        return failures.Count == 0
            ? new(KillOutcome.Stopped, policy == StopPolicy.Force ? $"{now} was stopped." : $"{now} didn't exit in time and was force-stopped.")
            : new(KillOutcome.Stopped, $"{now} was stopped, but some child processes couldn't be: {string.Join(" ", failures)}");
    }

    /// <summary>Plain-language reason for a failed kill.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        System.ComponentModel.Win32Exception { NativeErrorCode: 5 } =>
            "Access denied: it runs as Administrator or as another user. Use an elevated Task Manager to stop it.",
        _ => ex.Message,
    };

    private static async Task<bool> WaitForExitAsync(ProcessIdentity id, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (!id.IsAlive()) return true;
            await Task.Delay(200, ct);
        }
        return !id.IsAlive();
    }

    /// <summary>Graceful request: close the main window if it has one, and send Ctrl+C to its console.</summary>
    private static void AskToExit(ProcessIdentity id)
    {
        try
        {
            using var proc = Process.GetProcessById(id.Pid);
            if (proc.MainWindowHandle != IntPtr.Zero) proc.CloseMainWindow();
        }
        catch { /* exited */ }

        // Ctrl+C reaches every process on the target's console, which for a launched dev server is its own tree.
        // Only possible from a process without its own console (the dock is a GUI app; test hosts are not).
        lock (ConsoleLock)
        {
            if (Native.GetConsoleWindow() != IntPtr.Zero) return;
            if (!Native.AttachConsole((uint)id.Pid)) return;
            try
            {
                Native.SetConsoleCtrlHandler(IntPtr.Zero, true); // ignore it ourselves (permanently; we're a GUI app)
                Native.GenerateConsoleCtrlEvent(Native.CTRL_C_EVENT, 0);
            }
            finally { Native.FreeConsole(); }
        }
    }
}
