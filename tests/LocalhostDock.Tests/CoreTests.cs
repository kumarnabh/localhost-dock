using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Net;
using Xunit;

namespace LocalhostDock.Tests;

public class CoreTests
{
    [Fact]
    public void PortTable_sees_our_own_listener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var hit = PortTable.GetListeners().SingleOrDefault(l => l.Port == port);
            Assert.NotNull(hit);
            Assert.Equal(Environment.ProcessId, hit!.Pid);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void PortTable_sees_ipv6_listener()
    {
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.Contains(PortTable.GetListeners(), l => l.Port == port && l.Address.StartsWith("["));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void ProcessInfo_describes_current_process()
    {
        var d = ProcessInfo.Get(Environment.ProcessId, fresh: true);
        Assert.NotNull(d);
        Assert.False(d!.IsCritical);
        Assert.False(string.IsNullOrEmpty(d.CommandLine));
    }

    [Fact]
    public void ProcessInfo_refuses_to_kill_system_or_self()
    {
        Assert.NotNull(ProcessInfo.KillTree(4));
        Assert.NotNull(ProcessInfo.KillTree(Environment.ProcessId));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" C:\x\node_modules\vite\bin\vite.js", 5173, "Vite :5173")]
    [InlineData(@"python.exe -m uvicorn main:app --port 8000", 8000, "Uvicorn :8000")]
    [InlineData(@"C:\tools\thing.exe --serve", 9000, "thing :9000")]
    public void Guess_names_common_dev_servers(string cmd, int port, string expected)
    {
        var name = cmd.Contains("thing") ? "thing.exe" : "node.exe";
        Assert.Equal(expected, AppController.Guess(new ProcessDetails(1234, name, null, cmd, 1), port));
    }

    [Fact]
    public void Registry_round_trips_and_seeds_from_example()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ldock-test-" + Guid.NewGuid().ToString("N"));
        using var sandbox = TestSandbox.Use(dir);
        try
        {
            var reg = Registry.Load(); // seeded from apps.example.json (copied to the output folder)
            Assert.NotEmpty(reg.Apps);

            reg.Apps.Add(new AppEntry { Name = "Test", Port = 1234, ExtraPorts = { 5678 } });
            reg.IgnoredPorts.Add(9999);
            reg.Save();

            var again = Registry.Load();
            var t = again.Apps.Single(a => a.Name == "Test");
            Assert.Equal(new[] { 1234, 5678 }, t.AllPorts);
            Assert.Equal("http://localhost:1234", t.EffectiveUrl);
            Assert.Contains(9999, again.IgnoredPorts);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Start_reports_up_then_Stop_frees_the_port()
    {
        int port = FreePort();
        string dir = Path.Combine(Path.GetTempPath(), "ldock-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string launcher = Path.Combine(dir, "Start Test Server.cmd");
        // A tiny server written as an existing-style .cmd launcher, path with spaces on purpose.
        File.WriteAllText(launcher, $"@echo off\r\npowershell -NoProfile -Command \"$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,{port});$l.Start();Start-Sleep 120\"\r\n");

        var app = new AppEntry { Name = "Test server", Port = port, Start = launcher, Window = "hidden" };
        var controller = new AppController(new Registry { Apps = { app } });
        try
        {
            controller.Start(app);
            Assert.Equal(AppState.Starting, controller.Poll().Apps[0].State);
            Assert.True(await Until(() => controller.Poll().Apps[0].State == AppState.Up, 30), "never came up");

            await controller.StopAsync(app);
            Assert.DoesNotContain(PortTable.GetListeners(), l => l.Port == port);
            Assert.Equal(AppState.Down, controller.Poll().Apps[0].State);
        }
        finally
        {
            foreach (var l in PortTable.GetListeners().Where(l => l.Port == port)) ProcessInfo.KillTree(l.Pid);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Discovery_lists_unregistered_dev_ports_but_not_registered_or_ignored()
    {
        int port = FreePortBelowEphemeral();
        using var server = Process.Start(new ProcessStartInfo("node",
            $"-e \"require('http').createServer((q,r)=>r.end('ok')).listen({port},'127.0.0.1')\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Assert.True(await Until(() => PortTable.GetListeners().Any(l => l.Port == port), 15), "node never listened");
            var reg = new Registry();
            var controller = new AppController(reg);
            var found = controller.Poll().Discovered.SingleOrDefault(d => d.Port == port);
            Assert.NotNull(found);
            Assert.Equal(server.Id, found!.Pid);
            Assert.Equal("node.exe", found.ProcessName);

            reg.IgnoredPorts.Add(port);
            Assert.DoesNotContain(port, controller.Poll().Discovered.Select(d => d.Port));

            reg.IgnoredPorts.Clear();
            reg.Apps.Add(new AppEntry { Name = "mine", Port = port });
            Assert.DoesNotContain(port, controller.Poll().Discovered.Select(d => d.Port));
            Assert.Equal(AppState.Up, controller.Poll().Apps[0].State);
        }
        finally { ProcessInfo.KillTree(server.Id); }
    }

    [Fact]
    public void ExcludedPorts_parses_netsh_output()
    {
        const string sample = "\r\nProtocol tcp Port Exclusion Ranges\r\n\r\nStart Port    End Port\r\n----------    --------\r\n" +
                              "      5117        5216\r\n     50000       50059     *\r\n\r\n* - Administered port exclusions.\r\n";
        Assert.Equal(new[] { (5117, 5216), (50000, 50059) }, ExcludedPorts.Parse(sample).ToArray());
    }

    private static int FreePortBelowEphemeral()
    {
        for (int port = 23456; port < 23556; port++)
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            try { l.Start(); l.Stop(); return port; }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("no free test port");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<bool> Until(Func<bool> cond, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (cond()) return true;
            await Task.Delay(300);
        }
        return false;
    }
}
