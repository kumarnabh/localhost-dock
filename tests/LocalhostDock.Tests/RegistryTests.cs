using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Integration;
using Xunit;

namespace LocalhostDock.Tests;

public class RegistryTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ldock-reg-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _sandbox;

    public RegistryTests() => _sandbox = TestSandbox.Use(_home);

    public void Dispose()
    {
        _sandbox.Dispose();
        try { Directory.Delete(_home, true); } catch { }
    }

    [Fact]
    public void First_run_seeds_from_the_example_with_ids()
    {
        var reg = Registry.Load();
        Assert.NotEmpty(reg.Apps);
        Assert.All(reg.Apps, a => Assert.False(string.IsNullOrEmpty(a.Id)));
        Assert.Equal(Registry.CurrentSchema, reg.SchemaVersion);
    }

    [Fact]
    public void Migrates_a_v1_file_names_in_groups_become_ids_and_a_backup_is_kept()
    {
        File.WriteAllText(Registry.FilePath, """
            {
              // v0.2 file: no schemaVersion, no ids, groups by name
              "apps": [ { "name": "API", "port": 8000 }, { "name": "Web", "port": 3000 } ],
              "groups": [ { "name": "Dev", "apps": ["api", "Web", "Missing"] } ],
            }
            """);
        var reg = Registry.Load();

        Assert.True(reg.SchemaVersion >= 2);
        var api = reg.Find("API")!;
        var web = reg.Find("Web")!;
        Assert.Equal(new[] { api.Id, web.Id }, reg.Workspaces[0].Apps);
        Assert.Equal(new[] { api.Id }, reg.Workspaces[0].DependenciesOf(web.Id)); // old group order kept as a dependency chain
        Assert.Equal(3, reg.SchemaVersion);
        Assert.Contains(reg.LoadWarnings, w => w.Contains("Upgraded"));
        Assert.Contains(reg.LoadWarnings, w => w.Contains("Missing"));
        Assert.Contains(Directory.GetFiles(Registry.BackupFolder), f => f.Contains("pre-v3"));

        // Renaming no longer breaks groups.
        api.Name = "Backend";
        Assert.Equal(api.Id, reg.Workspaces[0].Apps[0]);
    }

    [Fact]
    public void Normalizes_bad_data_from_hand_edits()
    {
        File.WriteAllText(Registry.FilePath, """
            { "schemaVersion": 2,
              "apps": [
                { "id": "x", "name": "Dup", "port": 70000, "extraPorts": null, "window": "weird", "stopPolicy": "nope" },
                { "id": "x", "name": "dup", "port": 3000, "extraPorts": [3000, 3000, 0, 4000] },
                { "name": "   " }
              ],
              "groups": null, "ignoredPorts": [ -1, 80, 80 ], "notifications": "loud", "pollMs": 10 }
            """);
        var reg = Registry.Load();

        Assert.Equal(2, reg.Apps.Count);
        Assert.NotEqual(reg.Apps[0].Id, reg.Apps[1].Id);                    // duplicate ids fixed
        Assert.NotEqual(reg.Apps[0].Name, reg.Apps[1].Name, StringComparer.OrdinalIgnoreCase);
        Assert.Null(reg.Apps[0].Port);                                       // 70000 cleared
        Assert.Empty(reg.Apps[0].ExtraPorts);                                // null → []
        Assert.Equal("minimized", reg.Apps[0].Window);
        Assert.Equal("graceful", reg.Apps[0].StopPolicy);
        Assert.Equal(new[] { 4000 }, reg.Apps[1].ExtraPorts);               // dupes, 0 and the primary removed
        Assert.NotNull(reg.Workspaces);
        Assert.Equal(new[] { 80 }, reg.IgnoredPorts);
        Assert.Equal("all", reg.Notifications);
        Assert.Equal(500, reg.PollMs);
        Assert.NotEmpty(reg.LoadWarnings);
    }

    [Fact]
    public void Recovers_from_a_corrupt_file_using_the_newest_backup()
    {
        var reg = Registry.Load();
        reg.Apps.Clear();
        reg.Add(new AppEntry { Name = "Precious", Port = 8123 });
        reg.Save();
        reg.Add(new AppEntry { Name = "Second", Port = 8124 });
        reg.Save(); // the previous version is now a backup

        File.WriteAllText(Registry.FilePath, "{ this is not json");
        var recovered = Registry.Load();

        Assert.Contains(recovered.Apps, a => a.Name == "Precious");
        Assert.Contains(recovered.LoadWarnings, w => w.Contains("couldn't be read"));
        Assert.Contains(recovered.LoadWarnings, w => w.Contains("Restored"));
        Assert.Single(Directory.GetFiles(_home, "apps.corrupt-*.json"));
    }

    [Fact]
    public void Never_overwrites_an_edit_made_outside_the_dock()
    {
        var reg = Registry.Load();
        File.WriteAllText(Registry.FilePath, File.ReadAllText(Registry.FilePath).Replace("\"pollMs\": 2000", "\"pollMs\": 3000"));
        Assert.True(reg.ChangedOnDisk());

        reg.Add(new AppEntry { Name = "Mine" });
        Assert.Throws<RegistryConflictException>(reg.Save);
        Assert.Contains("3000", File.ReadAllText(Registry.FilePath)); // their edit survived

        var reloaded = Registry.Load();
        Assert.Equal(3000, reloaded.PollMs);
        reloaded.Add(new AppEntry { Name = "Mine" });
        reloaded.Save(); // fine after reloading
    }

    [Fact]
    public void Refuses_a_file_from_a_newer_version()
    {
        File.WriteAllText(Registry.FilePath, """{ "schemaVersion": 99, "apps": [] }""");
        var ex = Assert.Throws<InvalidOperationException>(() => Registry.Load());
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void Backups_are_rotated()
    {
        var reg = Registry.Load();
        for (int i = 0; i < 25; i++)
        {
            reg.PollMs = 1000 + i;
            reg.Save();
        }
        Assert.InRange(Directory.GetFiles(Registry.BackupFolder, "apps-*.json").Length, 1, 20);
    }

    [Fact]
    public void Captured_log_names_never_collide()
    {
        var a = new AppEntry { Id = "aaaa", Name = "a/b" };
        var b = new AppEntry { Id = "bbbb", Name = "a?b" };
        Assert.NotEqual(Registry.CapturedLogPath(a), Registry.CapturedLogPath(b));
        Assert.StartsWith(Registry.LogsFolder, Registry.CapturedLogPath(a));
    }
}

public class FeatureTests
{
    [Fact]
    public async Task CaptureLogs_writes_stdout_and_stderr_without_shell_redirection()
    {
        string home = Path.Combine(Path.GetTempPath(), "ldock-log-" + Guid.NewGuid().ToString("N"));
        using var sandbox = TestSandbox.Use(home);
        try
        {
            // Spaces, & and parentheses in the path: the old nested-cmd quoting broke on these.
            string dir = Path.Combine(home, "Say & Hello (x)");
            Directory.CreateDirectory(dir);
            string launcher = Path.Combine(dir, "Say Hello.cmd");
            File.WriteAllText(launcher, "@echo off\r\necho hello from the launcher\r\necho oops 1>&2\r\npause\r\necho after pause\r\n");
            var app = new AppEntry { Id = "logger", Name = "Logger", Port = 1, Start = launcher, CaptureLogs = true };
            using var controller = new AppController(new Registry { Apps = { app } });
            Assert.True((await controller.StartAsync(app)).Ok);

            string log = app.EffectiveLogFile!;
            string text = "";
            Assert.True(await TestProcesses.UntilAsync(() =>
            {
                try { text = File.ReadAllText(log); } catch (IOException) { }
                return text.Contains("after pause");
            }, 15), "log: " + text);
            Assert.Contains("starting Logger", text);
            Assert.Contains("hello from the launcher", text);
            Assert.Contains("oops", text);
            // stdin is NUL, so "pause" returned instead of hanging a hidden window forever.
        }
        finally
        {
            try { Directory.Delete(home, true); } catch { }
        }
    }

    [Fact]
    public void Logs_rotate_and_keep_three()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ldock-rot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string log = Path.Combine(dir, "app.log");
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(log, new string('x', 2048));
                Launcher.Rotate(log, limit: 1024);
            }
            Assert.False(File.Exists(log));
            Assert.True(File.Exists(Path.Combine(dir, "app.1.log")));
            Assert.True(File.Exists(Path.Combine(dir, "app.3.log")));
            Assert.False(File.Exists(Path.Combine(dir, "app.4.log")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(@"C:\x y\run.cmd", "/d /s /c \"\"C:\\x y\\run.cmd\"\"")]
    [InlineData("npm run dev", "/d /s /c \"npm run dev\"")]
    public void Command_lines_use_predictable_quoting(string command, string expectedArgs)
    {
        // A .cmd path is only treated as a file when it exists; create it for the first case.
        if (command.EndsWith(".cmd"))
        {
            string dir = Path.Combine(Path.GetTempPath(), "ldock-q-" + Guid.NewGuid().ToString("N"), "x y");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "run.cmd");
            File.WriteAllText(file, "@echo off");
            var (_, args, cwd) = Launcher.Build(file);
            Assert.Equal($"/d /s /c \"\"{file}\"\"", args);
            Assert.Equal(dir, cwd);
            Directory.Delete(Path.GetDirectoryName(dir)!, true);
            return;
        }
        Assert.Equal(expectedArgs, Launcher.Build(command).Args);
    }

    [Fact]
    public void Pipe_messages_keep_only_known_arguments_and_real_paths()
    {
        string existing = Path.GetTempPath().TrimEnd('\\');
        var args = SingleInstance.Sanitize(new[]
        {
            "--show", "--add", existing, "--add", @"C:\does\not\exist", "--evil", "rm -rf", "--add", "relative\\path",
            "--add", new string('a', 5000),
        });
        Assert.Equal(new[] { "--show", "--add", existing }, args);
    }
}
