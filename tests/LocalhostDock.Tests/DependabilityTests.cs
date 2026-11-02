using System.Net;
using System.Net.Sockets;
using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Health;
using LocalhostDock.Integration;
using LocalhostDock.Net;
using LocalhostDock.Workspace;
using Xunit;

namespace LocalhostDock.Tests;

public class DependabilityTests
{
    [Fact]
    public void Console_broadcasts_are_refused_when_an_unrelated_process_shares_the_terminal()
    {
        using var tp = new TestProcesses();
        var owned = tp.Listen(TestProcesses.FreePort(), tp.TempDir("owned-console"));
        var stranger = tp.Listen(TestProcesses.FreePort(), tp.TempDir("foreign-console"));
        Assert.True(ProcessGuard.CanSignalConsole(new[] { Environment.ProcessId, owned.Pid }, new[] { owned }));
        Assert.False(ProcessGuard.CanSignalConsole(new[] { owned.Pid, stranger.Pid }, new[] { owned }));
        Assert.False(ProcessGuard.CanSignalConsole(new[] { owned.Pid }, new[] { owned with { CreationTicks = owned.CreationTicks - 1 } }));
        Assert.True(owned.IsAlive());
        Assert.True(stranger.IsAlive());
    }

    [Fact]
    public async Task Quoted_relative_PowerShell_launcher_runs_with_arguments_in_the_expected_folder()
    {
        using var tp = new TestProcesses();
        string dir = tp.TempDir("startup path with spaces");
        string marker = Path.Combine(dir, "started.txt");
        File.WriteAllText(Path.Combine(dir, "Start Tool.ps1"), "param([int]$Port)\n[IO.File]::WriteAllText((Join-Path $PWD 'started.txt'), [string]$Port)\n");
        using var launched = Launcher.StartWithLog("\"Start Tool.ps1\" -Port 1234", dir, Path.Combine(dir, "test.log"), "");
        Assert.True(await TestProcesses.UntilAsync(() => launched.HasExited, 10));
        Assert.Equal(0, launched.ExitCode);
        Assert.Equal("1234", File.ReadAllText(marker));
    }

    [Fact]
    public void Shipped_project_presets_parse_and_import_without_enabling_integration_or_startup()
    {
        string file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../presets/nabh-projects.example.json"));
        var source = WorkspacePresets.Read(file);
        var target = new Registry();
        Assert.Equal(3, WorkspacePresets.Import(target, source, source.Workspaces.Select(w => w.Id)));
        Assert.Equal(5, target.Apps.Count);
        Assert.All(target.Apps, app => { Assert.True(app.StartUnverified); Assert.Null(app.Stop); });
        Assert.False(target.Aura.Enabled);
        Assert.All(target.Workspaces, ws => Assert.Null(WorkspacePlan.FindCycle(ws)));
    }

    [Fact]
    public async Task Failed_dependent_stop_preserves_its_dependencies()
    {
        using var tp = new TestProcesses();
        string dir = tp.TempDir("dependency");
        int apiPort = TestProcesses.FreePort(), webPort = TestProcesses.FreePort();
        var apiProcess = tp.Listen(apiPort, dir);
        var foreign = tp.Listen(webPort, tp.TempDir("foreign-dependent"));
        var api = new AppEntry { Id = "api", Name = "API", Port = apiPort, Cwd = dir, StopPolicy = "force" };
        var web = new AppEntry { Id = "web", Name = "Web", Port = webPort, Cwd = tp.TempDir("web") };
        var ws = new WorkspaceEntry { Id = "workspace", Name = "Stack", Apps = { api.Id, web.Id }, DependsOn = { [web.Id] = new() { api.Id } } };
        using var controller = new AppController(new Registry { Apps = { api, web } });
        var result = await controller.StopWorkspaceAsync(ws, null);
        Assert.False(result.Ok);
        Assert.Contains("Kept API running", result.Message);
        Assert.True(apiProcess.IsAlive());
        Assert.True(foreign.IsAlive());
    }

    [Fact]
    public async Task Observed_orphans_can_be_stopped_after_launcher_crashes_without_touching_a_stranger()
    {
        using var tp = new TestProcesses();
        string dir = tp.TempDir("orphan");
        int port = TestProcesses.FreePort();
        var stranger = tp.Listen(TestProcesses.FreePort(), tp.TempDir("unrelated"));
        File.WriteAllText(Path.Combine(dir, "parent.js"), $$"""
            const fs = require('fs');
            const child = require('child_process').spawn(process.execPath, ['-e', 'setInterval(()=>{},1000)'], {stdio:'ignore', detached:true, windowsHide:true});
            child.unref();
            fs.writeFileSync('child.pid', String(child.pid));
            require('net').createServer().listen({{port}}, '127.0.0.1');
            setInterval(() => { if (fs.existsSync('crash')) process.exit(23); }, 100);
            """);
        var app = new AppEntry { Id = "orphan", Name = "Orphan", Cwd = dir, Port = port, Start = "node parent.js", CaptureLogs = true, StopPolicy = "force" };
        using var controller = new AppController(new Registry { Apps = { app } });
        try
        {
            Assert.True((await controller.StartAndWaitAsync(app)).Ok);
            var status = controller.Poll().Apps[0]; // Observe/pin the tree while its parent still exists.
            Assert.True(status.RecoverableProcesses >= 2);
            int childPid = int.Parse(File.ReadAllText(Path.Combine(dir, "child.pid")));
            var child = ProcessIdentity.Capture(childPid)!;
            tp.Track(childPid);
            foreach (var listener in status.Listeners) tp.Track(listener.Listener.Pid);
            File.WriteAllText(Path.Combine(dir, "crash"), "");
            Assert.True(await TestProcesses.UntilAsync(() => controller.Poll().Apps[0].State == AppState.Down, 10));
            status = controller.Poll().Apps[0];
            Assert.True(status.RecoverableProcesses > 0);
            Assert.False((await controller.StartAsync(app)).Ok); // No duplicate copy over an orphan.
            Assert.True(child.IsAlive()); // No automatic recovery/termination.
            var stopped = await controller.StopAsync(app, null);
            Assert.True(stopped.Ok, stopped.Message);
            Assert.False(child.IsAlive());
            Assert.True(stranger.IsAlive());
        }
        finally { await controller.StopAsync(app, null); }
    }

    [Fact]
    public async Task Readiness_wait_keeps_the_operation_gate_until_failure_and_records_diagnostics()
    {
        using var tp = new TestProcesses();
        string dir = tp.TempDir("failed-start");
        var app = new AppEntry { Id = "failure", Name = "Failure", Port = TestProcesses.FreePort(), Cwd = dir,
            Start = "node -e \"setTimeout(()=>process.exit(19),700)\"", CaptureLogs = true, StartTimeoutSec = 5 };
        using var controller = new AppController(new Registry { Apps = { app } });
        var starting = controller.StartAndWaitAsync(app);
        Assert.True(controller.IsBusy(app.Id));
        Assert.False((await controller.StartAsync(app)).Ok);
        var result = await starting;
        Assert.False(result.Ok);
        Assert.Contains("19", result.Message);
        Assert.False(controller.IsBusy(app.Id));
        Assert.Contains("19", controller.Poll().Apps[0].LastError);
    }

    [Fact]
    public async Task Cancelled_health_probe_does_not_leave_monitor_stuck_in_flight()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        HttpServers.Run(tp, tp.TempDir("cancel-health"), port);
        using var health = new HealthMonitor();
        var app = new AppEntry { Id = "cancel", Port = port, Health = "/hang", HealthTimeoutMs = 10000 };
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health.CheckNowAsync(app, cancel.Token));
        app.Health = "/ok";
        health.MaybeCheck(app, urgent: true);
        Assert.True(await TestProcesses.UntilAsync(() => health.Get(app.Id)?.Healthy == true, 5));
    }

    [Theory]
    [InlineData(@"node C:\dev\project-other\server.js", false)]
    [InlineData(@"node C:\elsewhere\server.js --log C:\dev\project\log.txt", false)]
    [InlineData(@"node C:\elsewhere\server.js --label=C:\dev\project", false)]
    [InlineData(@"node C:\dev\project\server.js", true)]
    [InlineData("node \"C:\\dev\\project\\server.js\"", true)]
    [InlineData(@"node C:\dev\project\..\stranger\server.js", false)]
    public void Ownership_requires_an_entry_path_with_directory_boundaries(string command, bool match) =>
        Assert.Equal(match, ProcessOwnership.MatchesProject(@"C:\Program Files\nodejs\node.exe", command, new[] { @"C:\dev\project" }));

    [Fact]
    public async Task Secondary_port_conflicts_block_launch_and_skip_unsafe_stop_script()
    {
        using var tp = new TestProcesses();
        int primary = TestProcesses.FreePort(), extra = TestProcesses.FreePort();
        string dir = tp.TempDir("multiport");
        var stranger = tp.Listen(extra, tp.TempDir("stranger"));
        string marker = Path.Combine(dir, "ran.txt");
        string command = Path.Combine(dir, "command.cmd");
        File.WriteAllText(command, $"@echo off\r\necho unsafe>\"{marker}\"\r\n");
        var app = new AppEntry { Id = "extra", Name = "Extra", Port = primary, ExtraPorts = { extra }, Cwd = dir, Start = command, Stop = command, GracefulTimeoutSec = 1 };
        using var controller = new AppController(new Registry { Apps = { app } });
        Assert.Equal(AppState.Conflict, controller.Poll().Apps[0].State);
        Assert.False((await controller.StartAsync(app)).Ok);
        Assert.False((await controller.StopAsync(app, null)).Ok);
        Assert.False(File.Exists(marker));
        Assert.True(stranger.IsAlive());
    }

    [Fact]
    public async Task Port_reuse_clears_old_health_and_crash_recovery_is_user_controlled()
    {
        using var tp = new TestProcesses();
        string dir = tp.TempDir("reuse");
        int port = TestProcesses.FreePort();
        var first = HttpServers.Run(tp, dir, port);
        var app = new AppEntry { Id = "reuse", Name = "Reuse", Cwd = dir, Port = port, Health = "/ok", HealthIntervalSec = 3600 };
        using var controller = new AppController(new Registry { Apps = { app } });
        controller.Poll();
        await controller.Health.CheckNowAsync(app, default);
        Assert.True(controller.Poll().Apps[0].Ready);
        await ProcessGuard.TerminateTreeAsync(first, StopPolicy.Force, TimeSpan.Zero);
        var second = HttpServers.Run(tp, dir, port);
        var status = controller.Poll().Apps[0]; // No intervening 'down' poll: PID/creation change invalidates cached health.
        Assert.True(status.Health?.Last is null || status.Health.Last.AtUtc > second.CreatedUtc);
        Assert.True(second.IsAlive());
        await ProcessGuard.TerminateTreeAsync(second, StopPolicy.Force, TimeSpan.Zero);
        // Windows can retain a dead listener briefly after the kill handle signals exit.
        Assert.True(await TestProcesses.UntilAsync(() => controller.Poll().Apps[0].State == AppState.Down, 10));
        status = controller.Poll().Apps[0];
        Assert.Equal(AppState.Down, status.State);
        Assert.False(status.LastExit!.Expected);
        Assert.NotNull(status.LastExit.Code);
        Assert.DoesNotContain(PortTable.GetListeners(), l => l.Port == port);
    }

    [Fact]
    public async Task Health_failure_never_marks_workspace_ready()
    {
        using var tp = new TestProcesses();
        string dir = tp.TempDir("notready");
        int port = TestProcesses.FreePort();
        HttpServers.Run(tp, dir, port);
        var app = new AppEntry { Id = "notready", Name = "Notready", Cwd = dir, Port = port, Health = "/fail" };
        var ws = new WorkspaceEntry { Id = "w", Name = "W", Apps = { app.Id } };
        using var controller = new AppController(new Registry { Apps = { app }, Workspaces = { ws } });
        await controller.Health.CheckNowAsync(app, default);
        var snap = controller.Poll();
        Assert.False(snap.Apps[0].Ready);
        Assert.Equal(0, snap.Workspaces[0].Up);
        Assert.False((await controller.StartWorkspaceAsync(ws)).Ok);
    }

    [Fact]
    public void Unverified_and_mixed_listeners_never_satisfy_readiness()
    {
        var app = new AppEntry { Port = 3000 };
        var status = new AppStatus(app, AppState.Up, new[] {
            new OwnedListener(new Listener(3000, 1, "127.0.0.1"), Ownership.Owned, null, ""),
            new OwnedListener(new Listener(3000, 2, "[::1]"), Ownership.Foreign, null, "") }, null, 0, null, null);
        Assert.Equal(Ownership.Foreign, status.PrimaryOwnership);
        Assert.False(status.Ready);
    }

    [Fact]
    public void Relative_launchers_and_quoted_scripts_preserve_working_directory_and_arguments()
    {
        using var tp = new TestProcesses();
        string dir = tp.TempDir("path with spaces");
        string script = Path.Combine(dir, "Start Tool.ps1");
        File.WriteAllText(script, "param([int]$Port)\n");
        var relative = Launcher.Build("Start Tool.ps1", dir);
        Assert.Equal("powershell.exe", relative.File);
        Assert.Equal(dir, relative.DefaultCwd);
        var withArgs = Launcher.Build($"\"{script}\" -Port 1234");
        Assert.Equal(dir, withArgs.DefaultCwd);
        Assert.EndsWith("-Port 1234", withArgs.Args);
        Assert.Equal(dir, new AppEntry { Start = $"\"{script}\" -Port 1234" }.Folder);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Localhost Dock\LocalhostDock.exe")]
    [InlineData(@"C:\tools\LocalhostDock.exe")]
    public void Windows_startup_is_quoted_and_does_not_add_service_commands(string path)
    {
        string command = WindowsStartup.Command(path);
        Assert.Equal($"\"{path}\"", command);
        Assert.True(WindowsStartup.Matches(command, path));
        Assert.False(WindowsStartup.Matches(command + " --show", path));
        Assert.False(WindowsStartup.Matches("cmd /c " + command, path));
        Assert.Throws<ArgumentException>(() => WindowsStartup.Command("LocalhostDock.exe"));
    }

    [Fact]
    public void Presets_preserve_existing_entries_and_are_idempotent()
    {
        var existing = new AppEntry { Id = "owner", Name = "My web", Port = 3001, Cwd = @"C:\web", Start = "npm run dev", StopPolicy = "graceful-only" };
        var candidate = existing.Clone(); candidate.Id = "preset";
        var source = new Registry { Apps = { candidate }, Workspaces = { new WorkspaceEntry { Id = "w", Name = "Project", Apps = { candidate.Id } } } };
        var target = new Registry { Apps = { existing } };
        Assert.Equal(1, WorkspacePresets.Import(target, source, new[] { "w" }));
        Assert.Single(target.Apps);
        Assert.Equal("owner", Assert.Single(target.Workspaces).Apps[0]);
        Assert.Equal("graceful-only", target.Apps[0].StopPolicy);
        Assert.Equal(0, WorkspacePresets.Import(target, source, new[] { "w" }));
    }

    [Fact]
    public void Search_matches_multiple_words_across_fields_and_colon_ports()
    {
        Assert.True(DockSearch.Matches("vyora :3001", "Vyora Web", ":3001", @"C:\VyoraSuite"));
        Assert.True(DockSearch.Matches("web dev", "Web", "npm run dev"));
        Assert.False(DockSearch.Matches("web :8765", "Web", ":3001"));
    }
}
