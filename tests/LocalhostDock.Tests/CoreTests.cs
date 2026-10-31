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
    public void PortTable_keeps_ipv4_and_ipv6_bindings_of_the_same_port_separate()
    {
        int port = TestProcesses.FreePort();
        var v4 = new TcpListener(IPAddress.Loopback, port);
        var v6 = new TcpListener(IPAddress.IPv6Loopback, port);
        v4.Start();
        try
        {
            v6.Start();
            var hits = PortTable.GetListeners().Where(l => l.Port == port).ToList();
            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, h => h.Address == "127.0.0.1");
            Assert.Contains(hits, h => h.Address.StartsWith("["));
        }
        catch (SocketException) { /* IPv6 loopback unavailable on this machine: nothing to assert */ }
        finally { v4.Stop(); v6.Stop(); }
    }

    [Fact]
    public void ProcessInfo_describes_current_process_with_identity()
    {
        var d = ProcessInfo.Get(Environment.ProcessId, fresh: true);
        Assert.NotNull(d);
        Assert.NotNull(d!.Identity);
        Assert.True(d.IsCritical); // the dock never terminates itself
        Assert.False(string.IsNullOrEmpty(d.CommandLine));
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
    public async Task Start_then_Stop_with_a_cmd_launcher_whose_path_has_spaces_and_symbols()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        string dir = tp.TempDir("My App & Co (dev)");
        string launcher = Path.Combine(dir, "Start My App.cmd");
        File.WriteAllText(launcher, TestProcesses.ServerCmd(port));

        var app = new AppEntry { Id = "t1", Name = "Test server", Port = port, Start = launcher, Window = "hidden", GracefulTimeoutSec = 2 };
        using var controller = new AppController(new Registry { Apps = { app } });

        var started = await controller.StartAsync(app);
        Assert.True(started.Ok, started.Message);
        Assert.Equal(AppState.Starting, controller.Poll().Apps[0].State);
        Assert.True(await TestProcesses.UntilAsync(() => controller.Poll().Apps[0].State == AppState.Up, 30), "never came up");
        foreach (var l in PortTable.GetListeners().Where(l => l.Port == port)) tp.Track(l.Pid);

        var status = controller.Poll().Apps[0];
        Assert.Equal(Ownership.Owned, status.PrimaryOwnership);

        var stopped = await controller.StopAsync(app, confirm: null);
        Assert.True(stopped.Ok, stopped.Message);
        Assert.DoesNotContain(PortTable.GetListeners(), l => l.Port == port);
        Assert.Equal(AppState.Down, controller.Poll().Apps[0].State);
    }

    [Fact]
    public async Task Start_refuses_to_launch_a_second_copy()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        string dir = tp.TempDir("dup");
        tp.Listen(port, dir); // already running, in the app's own folder
        string marker = Path.Combine(dir, "launched.txt");
        string launcher = Path.Combine(dir, "start.cmd");
        File.WriteAllText(launcher, $"@echo off\r\necho x> \"{marker}\"\r\n");

        var app = new AppEntry { Id = "dup", Name = "Dup", Port = port, Start = launcher, Window = "hidden" };
        using var controller = new AppController(new Registry { Apps = { app } });
        var r = await controller.StartAsync(app);

        Assert.False(r.Ok);
        Assert.Contains("already running", r.Message);
        await Task.Delay(500);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Discovery_lists_unregistered_dev_ports_but_not_registered_or_ignored()
    {
        using var tp = new TestProcesses();
        int port = FreePortBelowEphemeral();
        string dir = tp.TempDir("disc");
        var server = tp.Listen(port, dir);

        var reg = new Registry();
        using var controller = new AppController(reg);
        var found = controller.Poll().Discovered.SingleOrDefault(d => d.Port == port);
        Assert.NotNull(found);
        Assert.Equal(server.Pid, found!.Pid);
        Assert.NotNull(found.Identity);

        reg.IgnoredPorts.Add(port);
        Assert.DoesNotContain(port, controller.Poll().Discovered.Select(d => d.Port));

        reg.IgnoredPorts.Clear();
        reg.Apps.Add(new AppEntry { Id = "m", Name = "mine", Port = port, Cwd = dir });
        Assert.DoesNotContain(port, controller.Poll().Discovered.Select(d => d.Port));
        var status = controller.Poll().Apps[0];
        Assert.Equal(AppState.Up, status.State);
        Assert.Equal(Ownership.Matched, status.PrimaryOwnership); // its working directory is the app's folder
    }

    [Fact]
    public void Discovery_never_lists_system_or_service_processes()
    {
        using var controller = new AppController(new Registry());
        var names = controller.Poll().Discovered.Select(d => d.ProcessName.ToLowerInvariant()).ToList();
        foreach (var system in new[] { "svchost.exe", "services.exe", "lsass.exe", "wininit.exe", "spoolsv.exe", "system" })
            Assert.DoesNotContain(system, names);
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
        for (int port = 23456; port < 23656; port++)
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            try { l.Start(); l.Stop(); return port; }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("no free test port");
    }
}
