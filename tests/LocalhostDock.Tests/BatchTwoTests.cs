using System.Diagnostics;
using System.Text.Json;
using LocalhostDock.Cli;
using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Health;
using LocalhostDock.Net;
using LocalhostDock.Workspace;
using Xunit;

namespace LocalhostDock.Tests;

/// <summary>Small HTTP servers (node) for health tests: /ok 200, /fail 503, /hang never answers, /warm 503 for the first 2.5 s.</summary>
internal static class HttpServers
{
    public const string Script = """
        const port = Number(process.argv[2]); const t0 = Date.now();
        require('http').createServer((q, r) => {
          if (q.url === '/hang') return;
          if (q.url === '/fail') { r.statusCode = 503; return r.end('down'); }
          if (q.url === '/warm') { r.statusCode = Date.now() - t0 < 2500 ? 503 : 200; return r.end(); }
          r.end('ok');
        }).listen(port, '127.0.0.1');
        setTimeout(() => {}, 120000);
        """;

    /// <summary>Writes server.js plus a launcher .cmd into <paramref name="dir"/>; returns the launcher path.</summary>
    public static string Launcher(string dir, int port)
    {
        File.WriteAllText(Path.Combine(dir, "server.js"), Script);
        string cmd = Path.Combine(dir, "Start-Server.cmd");
        File.WriteAllText(cmd, $"@echo off\r\nnode \"%~dp0server.js\" {port}\r\n");
        return cmd;
    }

    public static ProcessIdentity Run(TestProcesses tp, string dir, int port)
    {
        File.WriteAllText(Path.Combine(dir, "server.js"), Script);
        using var p = Process.Start(new ProcessStartInfo("node", $"server.js {port}") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir })!;
        var id = ProcessIdentity.Capture(p.Id)!;
        tp.Track(p.Id);
        Assert.True(TestProcesses.WaitUntil(() => PortTable.GetListeners().Any(l => l.Port == port), 15), "http server never listened");
        return id;
    }
}

public class HealthTests
{
    [Theory]
    [InlineData("/healthz", 3000, "http://localhost:3000/healthz")]
    [InlineData("http://localhost:4000/ready", null, "http://localhost:4000/ready")]
    [InlineData("http://127.0.0.2:8080/x", null, "http://127.0.0.2:8080/x")]
    [InlineData("http://[::1]:9000/h", null, "http://[::1]:9000/h")]
    [InlineData("http://app.localhost:3000/h", null, "http://app.localhost:3000/h")]
    public void Health_urls_resolve_on_this_machine(string health, int? port, string expected) =>
        Assert.Equal(expected, HealthUrl.Resolve(new AppEntry { Health = health, Port = port })!.ToString());

    [Theory]
    [InlineData("http://example.com/health")]
    [InlineData("https://192.168.1.10:3000/health")]
    [InlineData("ftp://localhost/x")]
    [InlineData("healthz")]
    public void Health_urls_that_leave_the_machine_are_rejected(string health) =>
        Assert.Null(HealthUrl.Resolve(new AppEntry { Health = health, Port = 3000 }));

    [Fact]
    public void A_path_without_a_port_has_no_url() => Assert.Null(HealthUrl.Resolve(new AppEntry { Health = "/h" }));

    [Fact]
    public async Task Checks_report_status_latency_timeouts_and_keep_bounded_history()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        HttpServers.Run(tp, tp.TempDir("health"), port);
        using var monitor = new HealthMonitor();

        var ok = await monitor.CheckNowAsync(new AppEntry { Id = "a", Port = port, Health = "/ok" }, default);
        Assert.True(ok.Ok);
        Assert.Equal(200, ok.Status);
        Assert.True(ok.LatencyMs > 0);
        // The server listens on 127.0.0.1 only; "localhost" must not pay the ~2 s IPv6-first penalty.
        var fresh = new HealthMonitor();
        var first = await fresh.CheckNowAsync(new AppEntry { Id = "z", Port = port, Health = "/ok" }, default);
        fresh.Dispose();
        Assert.True(first.LatencyMs < 1000, $"first check took {first.LatencyMs:0} ms");

        var fail = await monitor.CheckNowAsync(new AppEntry { Id = "b", Port = port, Health = "/fail" }, default);
        Assert.False(fail.Ok);
        Assert.Equal(503, fail.Status);

        var hang = await monitor.CheckNowAsync(new AppEntry { Id = "c", Port = port, Health = "/hang", HealthTimeoutMs = 300 }, default);
        Assert.False(hang.Ok);
        Assert.Contains("no answer within 300 ms", hang.Error);

        var app = new AppEntry { Id = "d", Port = port, Health = "/ok" };
        for (int i = 0; i < HealthMonitor.HistorySize + 5; i++) await monitor.CheckNowAsync(app, default);
        Assert.Equal(HealthMonitor.HistorySize, monitor.History("d").Count);
        Assert.Equal(1.0, monitor.Get("d")!.Availability);
    }

    [Fact]
    public async Task Failing_health_makes_a_running_app_degraded()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        string dir = tp.TempDir("degraded");
        HttpServers.Run(tp, dir, port);
        var app = new AppEntry { Id = "g", Name = "Sick", Port = port, Cwd = dir, Health = "/fail", HealthIntervalSec = 2 };
        using var controller = new AppController(new Registry { Apps = { app } });

        await controller.Health.CheckNowAsync(app, default);
        await controller.Health.CheckNowAsync(app, default);
        var s = controller.Poll().Apps[0];
        Assert.Equal(AppState.Degraded, s.State);
        Assert.Equal(503, s.Health!.Last!.Status);
    }

    [Fact]
    public async Task StartAndWait_waits_for_health_not_just_the_port()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        string dir = tp.TempDir("warm");
        var app = new AppEntry
        {
            Id = "w", Name = "Warm", Port = port, Start = HttpServers.Launcher(dir, port), Window = "hidden",
            Health = "/warm", HealthTimeoutMs = 1000, StartTimeoutSec = 30, GracefulTimeoutSec = 1,
        };
        using var controller = new AppController(new Registry { Apps = { app } });
        try
        {
            var r = await controller.StartAndWaitAsync(app);
            Assert.True(r.Ok, r.Message);
            Assert.Contains("healthy", r.Message);
            Assert.True(r.Duration >= TimeSpan.FromSeconds(2), $"ready too early: {r.Duration}"); // port opened at once, health after 2.5 s
            Assert.Equal(AppState.Up, controller.Poll().Apps[0].State);
            Assert.NotNull(controller.Poll().Apps[0].LastStartDuration);
        }
        finally
        {
            await controller.StopAsync(app, null);
        }
    }
}

public class WorkspaceTests
{
    private static WorkspaceEntry Diamond() => new()
    {
        Id = "w", Name = "Diamond", Apps = { "A", "B", "C", "D" },
        DependsOn = { ["B"] = new() { "A" }, ["C"] = new() { "A" }, ["D"] = new() { "B", "C" } },
    };

    [Fact]
    public void Levels_start_dependencies_first_and_stop_in_reverse()
    {
        var w = Diamond();
        Assert.Equal(new[] { new[] { "A" }, new[] { "B", "C" }, new[] { "D" } }, WorkspacePlan.Levels(w).Select(l => l.ToArray()).ToArray());
        Assert.Equal(new[] { "D", "C", "B", "A" }, WorkspacePlan.StopOrder(w));
        Assert.Equal(new[] { "B", "C", "D" }, WorkspacePlan.DependentsOf(w, "A").OrderBy(x => x));
        Assert.Null(WorkspacePlan.FindCycle(w));
    }

    [Fact]
    public void Cycles_are_found_and_removed_on_load()
    {
        var w = Diamond();
        w.DependsOn["A"] = new() { "D" };
        Assert.NotNull(WorkspacePlan.FindCycle(w));
        Assert.Throws<InvalidOperationException>(() => WorkspacePlan.Levels(w));

        var reg = new Registry
        {
            Apps = { new() { Id = "A", Name = "A" }, new() { Id = "B", Name = "B" }, new() { Id = "C", Name = "C" }, new() { Id = "D", Name = "D" } },
            Workspaces = { w },
        };
        var notes = reg.Normalize();
        Assert.Contains(notes, n => n.Contains("loop"));
        Assert.Null(WorkspacePlan.FindCycle(reg.Workspaces[0]));
    }

    [Fact]
    public void Combine_keeps_only_dependencies_between_the_chosen_apps()
    {
        var combined = WorkspacePlan.Combine("Last session", new[] { "B", "D" }, new[] { Diamond() });
        Assert.Equal(new[] { "B" }, combined.DependenciesOf("D"));
        Assert.Empty(combined.DependenciesOf("B")); // A wasn't chosen
    }

    [Fact]
    public async Task Independent_members_start_in_parallel_dependents_wait_and_maxParallel_is_enforced()
    {
        using var tp = new TestProcesses();
        var (apps, markers) = SlowServers(tp, "A", "B", "C", "D");
        var ws = Diamond();
        using var controller = new AppController(new Registry { Apps = { apps[0], apps[1], apps[2], apps[3] }, Workspaces = { ws } });

        var r = await controller.StartWorkspaceAsync(ws);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Slowest", r.Message);
        var t = markers.ToDictionary(kv => kv.Key, kv => new DateTime(long.Parse(File.ReadAllText(kv.Value).Trim()), DateTimeKind.Utc));
        Assert.True(t["B"] > t["A"].AddMilliseconds(1200) && t["C"] > t["A"].AddMilliseconds(1200), "B/C started before A was ready");
        Assert.True(Math.Abs((t["B"] - t["C"]).TotalMilliseconds) < 1200, "B and C didn't start in parallel");
        Assert.True(t["D"] > (t["B"] > t["C"] ? t["B"] : t["C"]).AddMilliseconds(1200), "D started before B and C were ready");
        Assert.True((await controller.StopWorkspaceAsync(ws, null)).Ok);

        // One at a time: B and C now start one after the other.
        foreach (var m in markers.Values) File.Delete(m);
        ws.MaxParallel = 1;
        Assert.True((await controller.StartWorkspaceAsync(ws)).Ok);
        var b = new DateTime(long.Parse(File.ReadAllText(markers["B"]).Trim()));
        var c = new DateTime(long.Parse(File.ReadAllText(markers["C"]).Trim()));
        Assert.True(Math.Abs((b - c).TotalMilliseconds) >= 1200, "maxParallel = 1 still started B and C together");
        Assert.True((await controller.StopWorkspaceAsync(ws, null)).Ok);
    }

    /// <summary>Launchers that record when they started, wait 1.5 s, then listen.</summary>
    private static (List<AppEntry> Apps, Dictionary<string, string> Markers) SlowServers(TestProcesses tp, params string[] names)
    {
        var apps = new List<AppEntry>();
        var markers = new Dictionary<string, string>();
        foreach (var name in names)
        {
            int port = TestProcesses.FreePort();
            string dir = tp.TempDir("slow-" + name);
            string marker = Path.Combine(dir, "started.txt");
            string cmd = Path.Combine(dir, "start.cmd");
            File.WriteAllText(cmd, "@echo off\r\npowershell -NoProfile -NonInteractive -Command \"" +
                $"[IO.File]::WriteAllText('{marker}', [DateTime]::UtcNow.Ticks); Start-Sleep -Milliseconds 1500; " +
                $"$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,{port});$l.Start();Start-Sleep 120\"\r\n");
            apps.Add(new AppEntry { Id = name, Name = name, Port = port, Start = cmd, Window = "hidden", GracefulTimeoutSec = 1, StartTimeoutSec = 40 });
            markers[name] = marker;
        }
        return (apps, markers);
    }
}

public class SchemaV3Tests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ldock-v3-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _sandbox;

    public SchemaV3Tests() => _sandbox = TestSandbox.Use(_home);

    public void Dispose()
    {
        _sandbox.Dispose();
        try { Directory.Delete(_home, true); } catch { }
    }

    [Fact]
    public void Migrates_v2_groups_to_workspace_chains_and_writes_plain_hotkeys()
    {
        File.WriteAllText(Registry.FilePath, """
            { "schemaVersion": 2,
              "apps": [ { "id": "a1", "name": "API", "port": 8000 }, { "id": "w1", "name": "Web", "port": 3000 }, { "id": "k1", "name": "Worker", "port": 9000 } ],
              "groups": [ { "id": "g1", "name": "Dev", "apps": ["a1", "w1", "k1"], "onFailure": "continue" } ],
              "hotkey": "Ctrl+Alt+L" }
            """);
        var reg = Registry.Load();
        var ws = Assert.Single(reg.Workspaces);
        Assert.Equal("g1", ws.Id);
        Assert.Equal("continue", ws.OnFailure);
        Assert.Equal(new[] { new[] { "a1" }, new[] { "w1" }, new[] { "k1" } }, WorkspacePlan.Levels(ws).Select(l => l.ToArray()).ToArray());

        string json = File.ReadAllText(Registry.FilePath);
        Assert.Contains("\"workspaces\"", json);
        Assert.DoesNotContain("\"groups\"", json);
        Assert.Contains("\"hotkey\": \"Ctrl+Alt+L\"", json); // not Ctrl+Alt+L
        Assert.Contains("\"schemaVersion\": 3", json);
    }

    [Fact]
    public void Remote_or_malformed_health_checks_are_turned_off_on_load()
    {
        File.WriteAllText(Registry.FilePath, """{ "schemaVersion": 3, "apps": [ { "name": "X", "port": 3000, "health": "http://evil.example/steal" } ] }""");
        var reg = Registry.Load();
        Assert.Null(reg.Apps[0].Health);
        Assert.Contains(reg.LoadWarnings, w => w.Contains("health check"));
    }

    [Fact]
    public void Read_only_load_never_writes_not_even_a_migration()
    {
        string v1 = """{ "apps": [ { "name": "Old", "port": 8000 } ], "groups": [ { "name": "G", "apps": ["Old"] } ] }""";
        File.WriteAllText(Registry.FilePath, v1);
        var reg = Registry.Load(readOnly: true);
        Assert.Single(reg.Workspaces); // migrated in memory…
        Assert.Equal(v1, File.ReadAllText(Registry.FilePath)); // …but the file is untouched
        Assert.False(Directory.Exists(Registry.BackupFolder));
        Assert.Throws<InvalidOperationException>(reg.Save);
    }

    [Fact]
    public void Session_is_saved_and_only_down_apps_with_a_start_command_are_offered()
    {
        SessionStore.Save(new[] { "up", "down", "nostart", "gone" }, cleanExit: false);
        var s = SessionStore.Load()!;
        Assert.Equal(4, s.Running.Count);

        AppStatus St(string id, AppState state, string? start = "x") =>
            new(new AppEntry { Id = id, Name = id, Start = start, Port = 1 }, state, Array.Empty<OwnedListener>(), null, 0, null, null);
        var candidates = SessionStore.Candidates(s, new[] { St("up", AppState.Up), St("down", AppState.Down), St("nostart", AppState.Down, start: null) });
        Assert.Equal(new[] { "down" }, candidates.Select(c => c.Id));

        File.WriteAllText(SessionStore.FilePath, "{ broken");
        Assert.Null(SessionStore.Load());
    }
}

public class PortAdvisorTests
{
    [Fact]
    public void Suggests_a_port_that_is_free_unreserved_unregistered_and_not_privileged()
    {
        var listening = new HashSet<int> { 3001 };
        var reserved = new HashSet<int> { 3002 };
        int? p = PortAdvisor.SuggestFree(3000, new[] { 3003 }, listening.Contains, reserved.Contains);
        Assert.Equal(3004, p);
        Assert.Equal(1024, PortAdvisor.SuggestFree(1000, Array.Empty<int>(), _ => false, _ => false));   // skips privileged ports
        Assert.Equal(49150, PortAdvisor.SuggestFree(49151, Array.Empty<int>(), _ => false, _ => false)); // skips the ephemeral range
    }

    [Theory]
    [InlineData("next dev -p 3000", "next dev -p 3005")]
    [InlineData("vite --port=3000", "vite --port=3005")]
    [InlineData("npm run dev -- --port 3000", "npm run dev -- --port 3005")]
    [InlineData("PORT=3000 node server.js", "PORT=3005 node server.js")]
    [InlineData("python -m http.server --bind localhost:3000", "python -m http.server --bind localhost:3005")]
    public void Rewrites_explicit_ports_in_start_commands(string start, string expected) =>
        Assert.Equal(expected, PortAdvisor.RewriteStartPort(start, 3000, 3005));

    [Theory]
    [InlineData("npm run dev")]          // port set elsewhere
    [InlineData("vite --port 30000")]    // different number
    [InlineData(null)]
    public void Leaves_commands_without_an_explicit_port_alone(string? start) =>
        Assert.Null(PortAdvisor.RewriteStartPort(start, 3000, 3005));
}

public class CliTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ldock-cli-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _sandbox;

    public CliTests()
    {
        _sandbox = TestSandbox.Use(_home);
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        _sandbox.Dispose();
        try { Directory.Delete(_home, true); } catch { }
    }

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = await CliRunner.RunAsync(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Status_json_is_versioned_and_reports_ownership_and_health_without_writing()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        string dir = tp.TempDir("cli-app");
        HttpServers.Run(tp, dir, port);
        string registry = $$"""
            { "apps": [ { "name": "Api", "port": {{port}}, "cwd": "{{dir.Replace("\\", "\\\\")}}", "health": "/ok" },
                        { "name": "Off", "port": {{TestProcesses.FreePort()}} } ],
              "groups": [ { "name": "Stack", "apps": ["Api", "Off"] } ] }
            """;
        File.WriteAllText(Registry.FilePath, registry);

        var (code, output, _) = await Run("status", "--json");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("localhost-dock/status@1", root.GetProperty("schema").GetString());
        var api = root.GetProperty("apps").EnumerateArray().First(a => a.GetProperty("name").GetString() == "Api");
        Assert.Equal("up", api.GetProperty("state").GetString());
        Assert.Equal("matched", api.GetProperty("processes")[0].GetProperty("ownership").GetString());
        Assert.True(api.GetProperty("health").GetProperty("healthy").GetBoolean());
        var off = root.GetProperty("apps").EnumerateArray().First(a => a.GetProperty("name").GetString() == "Off");
        Assert.Equal("down", off.GetProperty("state").GetString());
        Assert.Equal(1, root.GetProperty("workspaces")[0].GetProperty("ready").GetInt32());

        Assert.Equal(registry, File.ReadAllText(Registry.FilePath)); // read-only: no migration written
    }

    [Fact]
    public async Task Manifest_is_versioned_has_start_order_and_redacts_secrets()
    {
        File.WriteAllText(Registry.FilePath, """
            { "schemaVersion": 3,
              "apps": [ { "id": "a", "name": "Api", "port": 8000, "start": "API_KEY=abc123 node server.js --token s3cr3t --port 8000" },
                        { "id": "w", "name": "Web", "port": 3000, "start": "npm run dev" } ],
              "workspaces": [ { "id": "ws", "name": "Stack", "apps": ["a", "w"], "dependsOn": { "w": ["a"] } } ] }
            """);
        var (code, output, _) = await Run("manifest");
        Assert.Equal(0, code);
        Assert.DoesNotContain("abc123", output);
        Assert.DoesNotContain("s3cr3t", output);
        Assert.Contains("API_KEY=***", output);
        Assert.Contains("--port 8000", output); // non-secret flags survive
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("localhost-dock/manifest@1", doc.RootElement.GetProperty("schema").GetString());
        var order = doc.RootElement.GetProperty("workspaces")[0].GetProperty("startOrder");
        Assert.Equal("a", order[0][0].GetString());
        Assert.Equal("w", order[1][0].GetString());
    }

    [Fact]
    public async Task Who_and_errors_and_usage()
    {
        File.WriteAllText(Registry.FilePath, """{ "schemaVersion": 3, "apps": [] }""");
        var (code, output, _) = await Run("who", TestProcesses.FreePort().ToString(), "--json");
        Assert.Equal(0, code);
        Assert.Equal("localhost-dock/who@1", JsonDocument.Parse(output).RootElement.GetProperty("schema").GetString());

        Assert.Equal(2, (await Run("who", "notaport")).Code);
        Assert.Equal(2, (await Run("frobnicate")).Code);
        Assert.Equal(0, (await Run("help")).Code);

        File.WriteAllText(Registry.FilePath, "{ broken");
        var bad = await Run("status");
        Assert.Equal(1, bad.Code);
        Assert.Equal("{ broken", File.ReadAllText(Registry.FilePath)); // the CLI never "repairs"
    }

    [Theory]
    [InlineData("DB_PASSWORD=hunter2 npm start", "DB_PASSWORD=*** npm start")]
    [InlineData("tool --api-key abc --verbose", "tool --api-key *** --verbose")]
    [InlineData("set GITHUB_TOKEN='x y' && run", "set GITHUB_TOKEN=*** && run")]
    [InlineData("vite --port 5173", "vite --port 5173")]
    public void Redaction(string input, string expected) => Assert.Equal(expected, CliRunner.Redact(input));
}

public class FrameworkDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ldock-fw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Write(string relative, string content)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private AppEntry One(string dir) => Assert.Single(AppDetector.Detect(Path.Combine(_root, dir))).App;

    [Fact]
    public void FastApi_uses_the_venv_python_and_uvicorn()
    {
        Write(@"fa\main.py", "from fastapi import FastAPI\napi = FastAPI()\n");
        Write(@"fa\.venv\Scripts\python.exe", "");
        var a = One("fa");
        Assert.Equal(8000, a.Port);
        Assert.Contains(@".venv\Scripts\python.exe"" -m uvicorn main:api --port 8000", a.Start);
    }

    [Fact]
    public void Flask_reads_the_port_from_app_run()
    {
        Write(@"fl\app.py", "from flask import Flask\napp = Flask(__name__)\nif __name__ == '__main__':\n    app.run(port=5050)\n");
        var a = One("fl");
        Assert.Equal(5050, a.Port);
        Assert.Equal("python -m flask --app app run --port 5050", a.Start);
    }

    [Fact]
    public void AspNetCore_reads_launchSettings()
    {
        Write(@"web\Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write(@"web\Properties\launchSettings.json", """{ "profiles": { "Shop": { "applicationUrl": "https://localhost:7001;http://localhost:5123" } } }""");
        var a = One("web");
        Assert.Equal("Shop.Api", a.Name);
        Assert.Equal(5123, a.Port);
        Assert.StartsWith("dotnet run --project", a.Start);
    }

    [Fact]
    public void SpringBoot_with_maven_wrapper_and_server_port()
    {
        Write(@"sb\pom.xml", "<project><parent><artifactId>spring-boot-starter-parent</artifactId></parent></project>");
        Write(@"sb\mvnw.cmd", "@echo off");
        Write(@"sb\src\main\resources\application.properties", "spring.application.name=x\nserver.port=8081\n");
        var a = One("sb");
        Assert.Equal(8081, a.Port);
        Assert.Equal("mvnw.cmd spring-boot:run", a.Start);
    }

    [Fact]
    public void Rails_and_Laravel()
    {
        Write(@"rails\Gemfile", "source 'https://rubygems.org'\ngem \"rails\", \"~> 7.1\"\n");
        Write(@"rails\bin\rails", "#!/usr/bin/env ruby");
        Assert.Equal(3000, One("rails").Port);

        Write(@"lara\artisan", "#!/usr/bin/env php");
        Write(@"lara\composer.json", "{}");
        var l = One("lara");
        Assert.Equal(8000, l.Port);
        Assert.Equal("php artisan serve --port=8000", l.Start);
    }
}
