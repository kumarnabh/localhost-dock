using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Net;
using Xunit;

namespace LocalhostDock.Tests;

public sealed class DetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ldock-detect-" + Guid.NewGuid().ToString("N"));

    public DetectorTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void Cmd_launcher_that_calls_a_ps1_finds_port_and_stop_sibling()
    {
        // Start-X.cmd → %~dp0Start-X.ps1 with param([int]$Port = 8765), plus Stop-X.cmd next to it.
        string start = Write(@"Inventory\Start-Inventory.cmd", "@echo off\r\npowershell.exe -NoProfile -File \"%~dp0Start-Inventory.ps1\" %*\r\n");
        Write(@"Inventory\Start-Inventory.ps1", "param([ValidateRange(1024, 65535)][int]$Port = 8765, [switch]$NoBrowser)\r\nif ($Port -lt 1024 -or $Port -gt 65535) { throw 'bad' }\r\nStart-Process python -ArgumentList server.py --port $Port\r\n");
        string stop = Write(@"Inventory\Stop-Inventory.cmd", "@echo off\r\n");

        var d = Assert.Single(AppDetector.Detect(start));
        Assert.Equal("Inventory", d.App.Name);
        Assert.Equal(8765, d.App.Port);
        Assert.Empty(d.App.ExtraPorts);
        Assert.Equal(start, d.App.Start);
        Assert.Equal(stop, d.App.Stop);
    }

    [Fact]
    public void Launcher_with_spaces_and_scripts_subfolder_uses_the_url_port()
    {
        string start = Write(@"Ops - Control Room\Start Ops.cmd", "@echo off\r\ncd /d \"%~dp0\"\r\npowershell -File \"%~dp0scripts\\start-ops.ps1\"\r\n");
        Write(@"Ops - Control Room\scripts\start-ops.ps1", "$LocalUrl = 'http://127.0.0.1:4780'\r\n$ErrorLog = 'x'\r\n");
        string stop = Write(@"Ops - Control Room\Stop Ops.cmd", "@echo off\r\n");

        var d = Assert.Single(AppDetector.Detect(start)).App;
        Assert.Equal("Ops", d.Name);
        Assert.Equal(4780, d.Port);
        Assert.Equal("http://127.0.0.1:4780", d.Url);
        Assert.Equal(stop, d.Stop);
    }

    [Fact]
    public void Two_port_launcher_gets_primary_from_url_and_extra_port()
    {
        string start = Write(@"studio\StudioOS.cmd", "powershell -File \"%~dp0StudioOS-Start.ps1\"\r\n");
        Write(@"studio\StudioOS-Start.ps1",
            "$OpenUrl = \"http://localhost:3000\"\r\nStop-PortOwner -Port 3000\r\nStop-PortOwner -Port 8000\r\nWait-ForPort -Port 8000 -Seconds 30\r\n");

        var d = Assert.Single(AppDetector.Detect(start)).App;
        Assert.Equal("StudioOS", d.Name);
        Assert.Equal(3000, d.Port);
        Assert.Contains(8000, d.ExtraPorts);
    }

    [Fact]
    public void Folder_with_npm_script_running_a_local_server_file()
    {
        Write(@"games\package.json", """{ "name": "games-portal", "scripts": { "build": "x", "preview": "node scripts/preview-server.js" } }""");
        Write(@"games\scripts\preview-server.js", "const PORT = Number(process.env.PORT || process.argv[2] || 8088);\nserver.listen(PORT, '127.0.0.1');\n");

        var d = Assert.Single(AppDetector.Detect(Path.Combine(_root, "games"))).App;
        Assert.Equal("games", d.Name);
        Assert.Equal(8088, d.Port);
        Assert.Equal("npm run preview", d.Start);
        Assert.Equal(Path.Combine(_root, "games"), d.Cwd);
    }

    [Fact]
    public void Monorepo_dev_scripts_follow_workspaces_and_framework_defaults()
    {
        Write(@"suite\package.json", """
            { "name": "suite", "workspaces": ["apps/*"],
              "scripts": { "dev:web": "npm run dev -w apps/web-app", "dev:marketing": "npm run dev -w apps/marketing-site", "build": "x" } }
            """);
        Write(@"suite\pnpm-lock.yaml", "");
        Write(@"suite\apps\web-app\package.json", """{ "scripts": { "dev": "next dev -p 3001" } }""");
        Write(@"suite\apps\marketing-site\package.json", """{ "scripts": { "dev": "next dev" }, "dependencies": { "next": "15.0.0" } }""");

        var found = AppDetector.Detect(Path.Combine(_root, "suite")).Select(d => d.App).ToList();
        Assert.Equal(2, found.Count);
        var web = found.Single(a => a.Start == "pnpm run dev:web");
        Assert.Equal(3001, web.Port);
        Assert.Equal("suite Web", web.Name);
        var marketing = found.Single(a => a.Start == "pnpm run dev:marketing");
        Assert.Equal(3000, marketing.Port); // Next.js default
    }

    [Fact]
    public void Vite_project_uses_config_port_over_default()
    {
        Write(@"web\package.json", """{ "name": "@me/web", "scripts": { "dev": "vite" }, "devDependencies": { "vite": "5" } }""");
        Write(@"web\vite.config.ts", "export default { server: { port: 5280 } }");
        var d = Assert.Single(AppDetector.Detect(Path.Combine(_root, "web"))).App;
        Assert.Equal("web", d.Name);
        Assert.Equal(5280, d.Port);
    }

    [Fact]
    public void Generic_launcher_name_falls_back_to_folder_name()
    {
        string start = Write(@"Billing Service\start.bat", "uvicorn main:app --port 8100\r\n");
        var d = Assert.Single(AppDetector.Detect(start)).App;
        Assert.Equal("Billing Service", d.Name);
        Assert.Equal(8100, d.Port);
    }

    [Fact]
    public void Scan_finds_projects_but_skips_node_modules_and_does_not_descend_into_projects()
    {
        Write(@"scan\a\Start-A.cmd", "python -m http.server 8201\r\n");
        Write(@"scan\b\package.json", """{ "name": "b", "scripts": { "dev": "vite --port 8202" } }""");
        Write(@"scan\b\packages\inner\package.json", """{ "name": "inner", "scripts": { "dev": "vite --port 8203" } }""");
        Write(@"scan\c\node_modules\x\package.json", """{ "name": "x", "scripts": { "dev": "vite --port 8204" } }""");
        Write(@"scan\d\notes.txt", "nothing here");

        var found = AppDetector.Scan(Path.Combine(_root, "scan")).Select(d => d.App.Port).ToList();
        Assert.Contains(8201, found);
        Assert.Contains(8202, found);
        Assert.DoesNotContain(8203, found);
        Assert.DoesNotContain(8204, found);
    }

    [Fact]
    public void FromProcess_traces_node_modules_path_back_to_the_project()
    {
        Write(@"traced\package.json", """{ "name": "traced", "scripts": { "dev": "vite" }, "devDependencies": { "vite": "5" } }""");
        string vite = Write(@"traced\node_modules\vite\bin\vite.js", "// vite");
        var proc = new ProcessDetails(1234, "node.exe", @"C:\Program Files\nodejs\node.exe",
            $"\"C:\\Program Files\\nodejs\\node.exe\" \"{vite}\" --port 5190", 1);

        var d = AppDetector.FromProcess(proc, 5190);
        Assert.Equal("traced", d.App.Name);
        Assert.Equal(5190, d.App.Port);   // the port it's actually on wins over the guessed default
        Assert.Equal("npm run dev", d.App.Start);
        Assert.Equal(Path.Combine(_root, "traced"), d.App.Cwd);
    }

    [Fact]
    public void FromProcess_falls_back_to_the_exact_command_line()
    {
        var proc = new ProcessDetails(1, "thing.exe", @"C:\nowhere\thing.exe", @"C:\nowhere\thing.exe --serve", 0);
        var d = AppDetector.FromProcess(proc, 9123);
        Assert.Equal(9123, d.App.Port);
        Assert.Equal(@"C:\nowhere\thing.exe --serve", d.App.Start);
    }

    [Theory]
    [InlineData("next dev -p 3001", 3001)]
    [InlineData("vite --port=5174", 5174)]
    [InlineData("PORT=4000 node server.js", 4000)]
    [InlineData("python -m uvicorn main:app --host 127.0.0.1 --port 8000", 8000)]
    [InlineData("echo open http://localhost:4321/docs", 4321)]
    public void PortsIn_reads_common_forms(string text, int expected) =>
        Assert.Equal(expected, AppDetector.PortsIn(text).First());

    [Fact]
    public void PortsIn_ignores_versions_and_ip_fragments()
    {
        Assert.Empty(AppDetector.PortsIn("import x from 'y'; version 1.2.3; port 127.0.0.1"));
    }
}

public class FeatureTests
{
    [Fact]
    public void Registry_round_trips_groups_and_settings_and_omits_defaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ldock-reg-" + Guid.NewGuid().ToString("N"));
        using var sandbox = TestSandbox.Use(dir);
        try
        {
            var reg = new Registry
            {
                Apps = { new AppEntry { Name = "API", Port = 8000 }, new AppEntry { Name = "Web", Port = 3000, CaptureLogs = true } },
                Groups = { new GroupEntry { Name = "Dev", Apps = { "API", "Web" } } },
                Notifications = "crashes",
            };
            reg.Save();
            string json = File.ReadAllText(Registry.FilePath);
            Assert.DoesNotContain("\"logFile\"", json);      // null omitted
            Assert.Contains("\"captureLogs\": true", json);

            reg.Rename("Web", "Web app");
            Assert.Equal(new[] { "API", "Web app" }, reg.Groups[0].Apps);
            reg.Remove(reg.Apps[0]);
            Assert.Equal(new[] { "Web app" }, reg.Groups[0].Apps);

            var again = Registry.Load();
            Assert.Equal("crashes", again.Notifications);
            Assert.Equal("Dev", again.Groups.Single().Name);
            Assert.Equal("Web 2", again.UniqueName("Web") == "Web" ? "Web 2" : again.UniqueName("Web"));
            Assert.Equal("API 2", again.UniqueName("API"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CaptureLogs_writes_launcher_output_to_the_log()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ldock-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var sandbox = TestSandbox.Use(dir);
        try
        {
            string launcher = Path.Combine(dir, "Say Hello.cmd");
            File.WriteAllText(launcher, "@echo off\r\necho hello from the launcher\r\necho oops 1>&2\r\n");
            var app = new AppEntry { Name = "Logger", Port = 1, Start = launcher, CaptureLogs = true };
            var controller = new AppController(new Registry { Apps = { app } });
            controller.Start(app);

            string log = app.EffectiveLogFile!;
            string text = "";
            for (int i = 0; i < 50 && !text.Contains("oops"); i++)
            {
                await Task.Delay(200);
                try { text = File.ReadAllText(log); } catch (IOException) { }
            }
            Assert.Contains("starting Logger", text);
            Assert.Contains("hello from the launcher", text);
            Assert.Contains("oops", text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Group_starts_members_in_order_and_stops_them()
    {
        int p1 = FreePort(), p2 = FreePort();
        string dir = Path.Combine(Path.GetTempPath(), "ldock-grp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string Server(int port) =>
            $"@echo off\r\npowershell -NoProfile -Command \"$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,{port});$l.Start();Start-Sleep 120\"\r\n";
        File.WriteAllText(Path.Combine(dir, "a.cmd"), Server(p1));
        File.WriteAllText(Path.Combine(dir, "b.cmd"), Server(p2));

        var reg = new Registry
        {
            Apps =
            {
                new AppEntry { Name = "A", Port = p1, Start = Path.Combine(dir, "a.cmd"), Window = "hidden" },
                new AppEntry { Name = "B", Port = p2, Start = Path.Combine(dir, "b.cmd"), Window = "hidden" },
            },
            Groups = { new GroupEntry { Name = "Both", Apps = { "A", "B" } } },
        };
        var controller = new AppController(reg);
        try
        {
            var order = new List<string>();
            await controller.StartGroupAsync(reg.Groups[0], order.Add);
            Assert.Equal(new[] { "A", "B" }, order);
            var snap = controller.Poll();
            Assert.Equal(2, snap.Groups[0].Up);
            Assert.All(snap.Apps, a => Assert.True(a.UpSince is not null && a.MemoryBytes > 0));

            await controller.StopGroupAsync(reg.Groups[0]);
            Assert.Equal(0, controller.Poll().Groups[0].Up);
            Assert.True(controller.StoppedByUser("A"));
        }
        finally
        {
            foreach (var l in PortTable.GetListeners().Where(l => l.Port == p1 || l.Port == p2)) ProcessInfo.KillTree(l.Pid);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
