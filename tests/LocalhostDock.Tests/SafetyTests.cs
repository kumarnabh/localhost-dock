using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Net;
using Xunit;

namespace LocalhostDock.Tests;

/// <summary>
/// Regression tests for process-control safety: an unrelated or unverified process is never terminated
/// without explicit confirmation, recycled PIDs are never hit, protected processes are refused, and
/// operations on one app can't interleave.
/// </summary>
public class SafetyTests
{
    [Fact]
    public async Task Stop_never_kills_an_unrelated_process_that_occupies_the_apps_port()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        var stranger = tp.Listen(port, tp.TempDir("someone-else"));
        string appFolder = tp.TempDir("my-app");

        var app = new AppEntry { Id = "a", Name = "My app", Port = port, Cwd = appFolder, GracefulTimeoutSec = 1 };
        using var controller = new AppController(new Registry { Apps = { app } });

        Assert.Equal(AppState.Conflict, controller.Poll().Apps[0].State);
        Assert.Equal(Ownership.Foreign, controller.Poll().Apps[0].PrimaryOwnership);

        var asked = new List<string>();
        var result = await controller.StopAsync(app, (_, holder, reason) => { asked.Add(reason); return Task.FromResult(false); });

        Assert.False(result.Ok);
        Assert.Single(asked);                // you were asked…
        Assert.True(stranger.IsAlive());     // …you said no, so it's still running
        Assert.Contains("left running", result.Message);
    }

    [Fact]
    public async Task Stop_without_a_confirmation_callback_never_kills_an_unverified_process()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        var stranger = tp.Listen(port, tp.TempDir("elsewhere"));

        // No folder at all: ownership can't be verified.
        var app = new AppEntry { Id = "b", Name = "No folder", Port = port, Start = "npm run dev", GracefulTimeoutSec = 1 };
        using var controller = new AppController(new Registry { Apps = { app } });
        Assert.Equal(Ownership.Unverified, controller.Poll().Apps[0].PrimaryOwnership);

        var result = await controller.StopAsync(app, confirm: null);
        Assert.False(result.Ok);
        Assert.True(stranger.IsAlive());
    }

    [Fact]
    public async Task Stop_kills_an_unverified_process_only_after_explicit_confirmation()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        var stranger = tp.Listen(port, tp.TempDir("elsewhere"));
        var app = new AppEntry { Id = "c", Name = "Confirmed", Port = port, Cwd = tp.TempDir("app"), GracefulTimeoutSec = 1 };
        using var controller = new AppController(new Registry { Apps = { app } });

        var result = await controller.StopAsync(app, (_, _, _) => Task.FromResult(true));
        Assert.True(result.Ok, result.Message);
        Assert.False(stranger.IsAlive());
    }

    [Fact]
    public async Task Stop_kills_a_process_verified_by_working_directory_without_asking()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        string folder = tp.TempDir("verified-app");
        var mine = tp.Listen(port, folder);
        var app = new AppEntry { Id = "d", Name = "Verified", Port = port, Cwd = folder, GracefulTimeoutSec = 1 };
        using var controller = new AppController(new Registry { Apps = { app } });

        Assert.Equal(Ownership.Matched, controller.Poll().Apps[0].PrimaryOwnership);
        bool asked = false;
        var result = await controller.StopAsync(app, (_, _, _) => { asked = true; return Task.FromResult(false); });
        Assert.True(result.Ok, result.Message);
        Assert.False(asked);
        Assert.False(mine.IsAlive());
    }

    [Fact]
    public async Task A_recycled_pid_is_never_terminated()
    {
        using var tp = new TestProcesses();
        var real = tp.Listen(TestProcesses.FreePort(), tp.TempDir("pid-reuse"));
        // Same PID, different creation time: what a stale record looks like after Windows reuses the PID.
        var stale = real with { CreationTicks = real.CreationTicks - TimeSpan.TicksPerHour };

        var r = await ProcessGuard.TerminateTreeAsync(stale, StopPolicy.Force, TimeSpan.Zero);
        Assert.Equal(KillOutcome.Refused, r.Outcome);
        Assert.True(real.IsAlive());

        var differentImage = real with { ImagePath = @"C:\somewhere\else.exe" };
        Assert.Equal(KillOutcome.Refused, (await ProcessGuard.TerminateTreeAsync(differentImage, StopPolicy.Force, TimeSpan.Zero)).Outcome);
        Assert.True(real.IsAlive());
    }

    [Fact]
    public async Task Protected_processes_are_refused()
    {
        var self = ProcessIdentity.Capture(Environment.ProcessId)!;
        var r = await ProcessGuard.TerminateTreeAsync(self, StopPolicy.Force, TimeSpan.Zero);
        Assert.Equal(KillOutcome.Refused, r.Outcome);

        Assert.NotNull(ProcessGuard.WhyProtected(new ProcessIdentity(4, 0, null, "System")));
        Assert.NotNull(ProcessGuard.WhyProtected(new ProcessIdentity(99999, 0, null, "svchost.exe")));
        Assert.NotNull(ProcessGuard.WhyProtected(new ProcessIdentity(99999, 0, null, "lsass.exe")));

        // Service processes are protected even when Windows won't let us open them (so no identity is available).
        foreach (var name in new[] { "svchost", "services", "lsass", "spoolsv" })
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name).Take(3))
            {
                Assert.NotNull(ProcessGuard.WhyProtected(p.Id, p.ProcessName + ".exe"));
                Assert.True(ProcessInfo.Get(p.Id)?.IsCritical ?? true, $"{name} ({p.Id}) must be protected");
            }

        // A real service process (session 0), if one is readable on this machine.
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("svchost").Take(5))
        {
            if (ProcessIdentity.Capture(p.Id) is { } svc)
            {
                Assert.NotNull(ProcessGuard.WhyProtected(svc));
                break;
            }
        }
    }

    [Fact]
    public async Task Exited_process_is_reported_not_retried()
    {
        using var tp = new TestProcesses();
        var p = tp.Listen(TestProcesses.FreePort(), tp.TempDir("exits"));
        Assert.True((await ProcessGuard.TerminateTreeAsync(p, StopPolicy.Force, TimeSpan.Zero)).Ok);
        var again = await ProcessGuard.TerminateTreeAsync(p, StopPolicy.Force, TimeSpan.Zero);
        Assert.Equal(KillOutcome.AlreadyExited, again.Outcome);
    }

    [Fact]
    public async Task Graceful_only_policy_never_force_kills()
    {
        using var tp = new TestProcesses();
        var p = tp.Listen(TestProcesses.FreePort(), tp.TempDir("graceful-only"));
        // Test hosts have a console, so Ctrl+C can't be delivered: a graceful-only stop must give up, not force.
        var r = await ProcessGuard.TerminateTreeAsync(p, StopPolicy.GracefulOnly, TimeSpan.FromSeconds(1));
        if (r.Outcome == KillOutcome.Failed)
        {
            Assert.True(p.IsAlive());
            Assert.Contains("forbids force", r.Message);
        }
        else Assert.Equal(KillOutcome.Stopped, r.Outcome); // the graceful request worked
    }

    [Fact]
    public async Task Concurrent_operations_on_one_app_are_serialized()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        string folder = tp.TempDir("serial");
        string stop = Path.Combine(folder, "stop.cmd");
        File.WriteAllText(stop, "@echo off\r\nping -n 3 127.0.0.1 > nul\r\n"); // a slow stop script (~2 s)
        tp.Listen(port, folder);
        var app = new AppEntry { Id = "s", Name = "Serial", Port = port, Cwd = folder, Stop = stop, Start = "echo", GracefulTimeoutSec = 1 };
        using var controller = new AppController(new Registry { Apps = { app } });

        var first = controller.StopAsync(app, null);
        await Task.Delay(300);
        var second = await controller.StopAsync(app, null);
        var start = await controller.StartAsync(app);
        var restart = await controller.RestartAsync(app, null);

        Assert.False(second.Ok);
        Assert.Contains("already starting or stopping", second.Message);
        Assert.False(start.Ok);
        Assert.False(restart.Ok);
        Assert.True((await first).Ok);
    }

    [Fact]
    public async Task Restart_does_not_start_when_the_stop_did_not_complete()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        var stranger = tp.Listen(port, tp.TempDir("holder"));
        string folder = tp.TempDir("restart-app");
        string marker = Path.Combine(folder, "started.txt");
        string launcher = Path.Combine(folder, "start.cmd");
        File.WriteAllText(launcher, $"@echo off\r\necho x> \"{marker}\"\r\n");
        var app = new AppEntry { Id = "r", Name = "Restart", Port = port, Cwd = folder, Start = launcher, Window = "hidden", GracefulTimeoutSec = 1 };
        using var controller = new AppController(new Registry { Apps = { app } });

        var result = await controller.RestartAsync(app, (_, _, _) => Task.FromResult(false));
        Assert.False(result.Ok);
        Assert.Contains("Restart cancelled", result.Message);
        await Task.Delay(500);
        Assert.False(File.Exists(marker));
        Assert.True(stranger.IsAlive());
    }

    [Fact]
    public async Task Port_takeover_is_reported_as_a_conflict_and_blocks_start()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        string folder = tp.TempDir("takeover-app");
        string launcher = Path.Combine(folder, "Start.cmd");
        File.WriteAllText(launcher, TestProcesses.ServerCmd(port));
        var app = new AppEntry { Id = "t", Name = "Takeover", Port = port, Start = launcher, Window = "hidden", GracefulTimeoutSec = 1 };
        using var controller = new AppController(new Registry { Apps = { app } });

        Assert.True((await controller.StartAsync(app)).Ok);
        Assert.True(await TestProcesses.UntilAsync(() => controller.Poll().Apps[0].State == AppState.Up, 30));
        Assert.True((await controller.StopAsync(app, null)).Ok);

        var intruder = tp.Listen(port, tp.TempDir("intruder"));
        var status = controller.Poll().Apps[0];
        Assert.Equal(AppState.Conflict, status.State);
        Assert.Equal(intruder.Pid, status.ForeignHolder?.Identity?.Pid);

        var start = await controller.StartAsync(app);
        Assert.False(start.Ok);
        Assert.Contains("isn't Takeover", start.Message);
        Assert.True(intruder.IsAlive());
    }

    [Fact]
    public async Task Crash_is_detected_with_exit_code_and_identity()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        string folder = tp.TempDir("crashy");
        var server = tp.Listen(port, folder);
        var app = new AppEntry { Id = "x", Name = "Crashy", Port = port, Cwd = folder };
        using var controller = new AppController(new Registry { Apps = { app } });
        Assert.Equal(AppState.Up, controller.Poll().Apps[0].State);

        // Something other than the dock kills it.
        using (var p = System.Diagnostics.Process.GetProcessById(server.Pid)) p.Kill();
        Assert.True(await TestProcesses.UntilAsync(() => !server.IsAlive(), 10));

        // Windows can take a moment to drop the dead process's socket from the TCP table.
        AppStatus status = controller.Poll().Apps[0];
        Assert.True(await TestProcesses.UntilAsync(() => (status = controller.Poll().Apps[0]).State == AppState.Down, 10));
        Assert.NotNull(status.LastExit);
        Assert.False(status.LastExit!.Expected);
        Assert.NotNull(status.LastExit.Code);
        Assert.Contains("node", status.LastExit.Process);
    }

    [Fact]
    public async Task User_stop_is_not_reported_as_a_crash()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        string folder = tp.TempDir("calm");
        tp.Listen(port, folder);
        var app = new AppEntry { Id = "y", Name = "Calm", Port = port, Cwd = folder, GracefulTimeoutSec = 1 };
        using var controller = new AppController(new Registry { Apps = { app } });
        Assert.Equal(AppState.Up, controller.Poll().Apps[0].State);
        Assert.True((await controller.StopAsync(app, null)).Ok);
        var status = controller.Poll().Apps[0];
        Assert.True(status.LastExit?.Expected ?? true);
    }

    [Fact]
    public async Task Group_stops_members_one_at_a_time_in_reverse_order()
    {
        using var tp = new TestProcesses();
        string log = Path.Combine(tp.TempDir("order"), "order.txt");
        var apps = new List<AppEntry>();
        foreach (var name in new[] { "A", "B", "C" })
        {
            int port = TestProcesses.FreePort();
            string folder = tp.TempDir("grp-" + name);
            string stop = Path.Combine(folder, "stop.cmd");
            // Each stop script records begin/end; overlapping runs would interleave the lines.
            File.WriteAllText(stop, $"@echo off\r\necho begin {name}>> \"{log}\"\r\nping -n 2 127.0.0.1 > nul\r\necho end {name}>> \"{log}\"\r\n");
            tp.Listen(port, folder);
            apps.Add(new AppEntry { Id = name, Name = name, Port = port, Cwd = folder, Stop = stop, GracefulTimeoutSec = 1 });
        }
        var group = WorkspaceEntry.Chain("g", "All", new() { "A", "B", "C" }, "stop"); // C needs B needs A
        using var controller = new AppController(new Registry { Apps = { apps[0], apps[1], apps[2] }, Workspaces = { group } });

        var r = await controller.StopWorkspaceAsync(group, null);
        Assert.True(r.Ok, r.Message);
        var lines = File.ReadAllLines(log).Select(l => l.Trim()).ToArray();
        Assert.Equal(new[] { "begin C", "end C", "begin B", "end B", "begin A", "end A" }, lines);
    }

    [Fact]
    public async Task Group_start_stops_at_a_failed_dependency_unless_told_to_continue()
    {
        using var tp = new TestProcesses();
        string folder = tp.TempDir("deps");
        string marker = Path.Combine(folder, "web-started.txt");
        string failing = Path.Combine(folder, "api.cmd");
        File.WriteAllText(failing, "@echo off\r\nexit /b 3\r\n");
        string web = Path.Combine(folder, "web.cmd");
        File.WriteAllText(web, $"@echo off\r\necho x> \"{marker}\"\r\n");

        var api = new AppEntry { Id = "api", Name = "API", Port = TestProcesses.FreePort(), Start = failing, Window = "hidden", StartTimeoutSec = 10 };
        var webApp = new AppEntry { Id = "web", Name = "Web", Port = TestProcesses.FreePort(), Start = web, Window = "hidden", StartTimeoutSec = 5 };
        var group = WorkspaceEntry.Chain("g", "Stack", new() { "api", "web" }, "stop"); // Web needs API
        using var controller = new AppController(new Registry { Apps = { api, webApp }, Workspaces = { group } });

        var r = await controller.StartWorkspaceAsync(group);
        Assert.False(r.Ok);
        Assert.Contains("stopped at API", r.Message);
        Assert.Contains("exited with code 3", r.Message);
        await Task.Delay(500);
        Assert.False(File.Exists(marker));

        // "continue" never starts an app whose dependency failed…
        group.OnFailure = "continue";
        var r2 = await controller.StartWorkspaceAsync(group);
        Assert.False(r2.Ok);
        Assert.Contains("Not started (a dependency failed): Web", r2.Message);
        await Task.Delay(500);
        Assert.False(File.Exists(marker));

        // …but does start members that don't depend on the failed one.
        group.DependsOn.Clear();
        var r3 = await controller.StartWorkspaceAsync(group);
        Assert.False(r3.Ok); // still reports API's failure
        Assert.True(await TestProcesses.UntilAsync(() => File.Exists(marker), 10));
    }
}
