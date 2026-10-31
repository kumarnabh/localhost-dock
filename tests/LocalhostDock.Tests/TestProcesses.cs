using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LocalhostDock.Net;

namespace LocalhostDock.Tests;

/// <summary>
/// Starts real listening processes for tests and cleans up ONLY those, by verified identity (PID + creation
/// time), through the same ProcessGuard the app uses. A test can never kill a process it didn't start, even if
/// some other program happens to be on the same port.
/// </summary>
internal sealed class TestProcesses : IDisposable
{
    private readonly List<ProcessIdentity> _started = new();
    private readonly List<string> _dirs = new();

    /// <summary>
    /// A node.exe listening on 127.0.0.1:<paramref name="port"/>, running in <paramref name="cwd"/>. (Node rather
    /// than PowerShell: discovery intentionally ignores programs under C:\Windows.) Its command line doesn't
    /// mention <paramref name="cwd"/>, so ownership tests exercise the working-directory check.
    /// </summary>
    public ProcessIdentity Listen(int port, string cwd, int seconds = 120)
    {
        Directory.CreateDirectory(cwd);
        var psi = new ProcessStartInfo("node",
            $"-e \"require('net').createServer().listen({port},'127.0.0.1');setTimeout(()=>{{}},{seconds * 1000})\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
        };
        using var p = Process.Start(psi)!;
        var id = ProcessIdentity.Capture(p.Id) ?? throw new InvalidOperationException("listener exited immediately");
        _started.Add(id);
        if (!WaitUntil(() => PortTable.GetListeners().Any(l => l.Port == port && l.Pid == id.Pid), 20))
            throw new InvalidOperationException($"test listener never bound :{port}");
        return id;
    }

    /// <summary>Text for a .cmd launcher that listens on <paramref name="port"/>.</summary>
    public static string ServerCmd(int port, int seconds = 120) =>
        $"@echo off\r\npowershell -NoProfile -NonInteractive -Command \"$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,{port});$l.Start();Start-Sleep {seconds}\"\r\n";

    public string TempDir(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ldock-t-" + Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(dir);
        _dirs.Add(Path.GetDirectoryName(dir)!);
        return dir;
    }

    /// <summary>Registers a process the code under test started (e.g. via the dock) so cleanup may stop it.</summary>
    public void Track(int pid)
    {
        if (ProcessIdentity.Capture(pid) is { } id) _started.Add(id);
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static bool WaitUntil(Func<bool> cond, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (cond()) return true;
            Thread.Sleep(200);
        }
        return cond();
    }

    public static async Task<bool> UntilAsync(Func<bool> cond, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (cond()) return true;
            await Task.Delay(200);
        }
        return cond();
    }

    public void Dispose()
    {
        foreach (var id in _started)
            if (id.IsAlive()) ProcessGuard.TerminateTreeAsync(id, StopPolicy.Force, TimeSpan.Zero).GetAwaiter().GetResult();
        foreach (var d in _dirs)
            try { Directory.Delete(d, true); } catch { }
    }
}
