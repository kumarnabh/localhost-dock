using System.Text.Json;
using System.Text.RegularExpressions;
using LocalhostDock.Config;
using LocalhostDock.Net;

namespace LocalhostDock.Core;

/// <summary>A suggested registry entry and a human-readable note on how it was worked out.</summary>
public sealed record Detection(AppEntry App, string Note);

/// <summary>
/// Turns a launcher file, a project folder or a running process into a ready-to-save <see cref="AppEntry"/>:
/// name, port(s), URL, start/stop commands and working directory.
/// </summary>
public static partial class AppDetector
{
    private static readonly string[] LauncherExtensions = { ".cmd", ".bat", ".ps1", ".exe", ".py", ".js", ".mjs" };
    private static readonly string[] ScriptExtensions = { ".cmd", ".bat", ".ps1", ".py", ".js", ".mjs", ".cjs", ".ts" };
    private static readonly string[] GenericNames =
        { "start", "run", "launch", "launcher", "dev", "serve", "server", "index", "main", "app", "go", "up", "scripts", "bin", "tools" };
    private static readonly string[] NotLaunchers =
        { "stop", "build", "test", "install", "setup", "enable", "create", "diagnos", "uninstall", "clean", "deploy", "update", "backup", "migrate", "lint", "gradlew", "mvnw" };
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", "venv", "__pycache__", "coverage",
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "$Recycle.Bin", "PerfLogs",
    };

    // Framework → default dev port, used when nothing in the scripts names a port.
    private static readonly (string Dep, int Port)[] FrameworkPorts =
    {
        ("next", 3000), ("nuxt", 3000), ("astro", 4321), ("@angular/core", 4200), ("gatsby", 8000),
        ("@docusaurus/core", 3000), ("react-scripts", 3000), ("@sveltejs/kit", 5173), ("@remix-run/dev", 5173),
        ("storybook", 6006), ("@vue/cli-service", 8080), ("webpack-dev-server", 8080), ("parcel", 1234),
        ("expo", 8081), ("vite", 5173),
    };

    // ------------------------------------------------------------------ entry points

    /// <summary>Detects apps from a file (launcher/script) or a folder (project, monorepo, folder of launchers).</summary>
    public static List<Detection> Detect(string path)
    {
        path = path.Trim().Trim('"');
        if (File.Exists(path)) return FromFile(path) is { } d ? new() { d } : new();
        if (Directory.Exists(path)) return FromFolder(path);
        return new();
    }

    /// <summary>
    /// Traces a listening process back to its project (e.g. node …\my-app\node_modules\vite\bin\vite.js → my-app)
    /// and suggests an entry that restarts it the same way.
    /// </summary>
    public static Detection FromProcess(ProcessDetails d, int port)
    {
        foreach (string dir in CandidateDirsFromCommandLine(d))
        {
            for (string? probe = dir; probe is not null; probe = Path.GetDirectoryName(probe))
            {
                var found = FromFolder(probe);
                if (found.Count > 0)
                {
                    var best = found.FirstOrDefault(f => f.App.AllPorts.Contains(port)) ?? found[0];
                    var app = best.App;
                    if (app.Port != port)
                    {
                        app.ExtraPorts.Remove(port);
                        app.Port = port;
                        app.Url = null;
                    }
                    return new Detection(app, $"Traced :{port} to {probe}. {best.Note}");
                }
                if (Path.GetPathRoot(probe) == probe || Depth(dir) - Depth(probe) >= 4) break;
            }
        }

        var fallback = new AppEntry
        {
            Name = AppController.Guess(d, port).Split(" :")[0],
            Port = port,
            Start = d.CommandLine,
            Cwd = CandidateDirsFromCommandLine(d).FirstOrDefault(),
        };
        return new Detection(fallback, "Couldn't find a launcher or project for this process, so Start re-runs its exact command line.");
    }

    /// <summary>Walks a folder tree (not into node_modules, .git, bin, …) and collects every app it can find.</summary>
    public static List<Detection> Scan(string root, int maxDepth = 3, CancellationToken ct = default, Action<string>? progress = null)
    {
        var results = new List<Detection>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;

        while (queue.Count > 0 && visited < 4000)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            visited++;
            progress?.Invoke(dir);

            List<Detection> here;
            try { here = FromFolder(dir); }
            catch { continue; }
            results.AddRange(here);

            // A project's own subfolders are its business (src, apps/* of a monorepo, …): don't descend.
            if (here.Count > 0 || depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || name.StartsWith('$') || SkipDirs.Contains(name)) continue;
                    if ((File.GetAttributes(sub) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch { /* access denied etc. */ }
        }
        return Dedupe(results);
    }

    // ------------------------------------------------------------------ files

    private static Detection? FromFile(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (!LauncherExtensions.Contains(ext)) return null;

        string dir = Path.GetDirectoryName(file)!;
        var app = new AppEntry { Name = NameFor(file) };
        var notes = new List<string>();

        app.Start = ext switch
        {
            ".py" => $"python \"{file}\"",
            ".js" or ".mjs" => $"node \"{file}\"",
            _ => file,
        };
        if (ext is ".py" or ".js" or ".mjs") app.Cwd = dir;

        if (FindStopSibling(file) is { } stop)
        {
            app.Stop = stop;
            notes.Add($"stop script {Path.GetFileName(stop)}");
        }

        if (ext != ".exe")
        {
            var scanned = new List<string>();
            var text = ReadScriptTree(file, scanned, depth: 0);
            ApplyPorts(app, text);
            if (app.Port is not null)
                notes.Add($"port {Ports(app)} from {string.Join(", ", scanned.Select(Path.GetFileName).Distinct())}");
        }

        if (app.Port is null && File.Exists(Path.Combine(dir, "package.json")))
        {
            // e.g. a start.bat that runs "npm run dev" next to package.json
            var pkg = FromPackageJson(dir).FirstOrDefault();
            if (pkg?.App.Port is int p)
            {
                app.Port = p;
                notes.Add($"port :{p} from package.json");
            }
        }

        if (app.Port is null) notes.Add("no port found; set it so the status dot works");
        return new Detection(app, Sentence(notes));
    }

    private static string NameFor(string file)
    {
        string stem = Path.GetFileNameWithoutExtension(file);
        string cleaned = LauncherPrefix().Replace(stem, "");
        cleaned = LauncherSuffix().Replace(cleaned, "").Trim(' ', '-', '_', '.');
        if (cleaned.Length == 0 || GenericNames.Contains(cleaned.ToLowerInvariant()))
            return FolderName(Path.GetDirectoryName(file)!);
        return cleaned;
    }

    private static string FolderName(string dir)
    {
        string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (GenericNames.Contains(name.ToLowerInvariant()) && Path.GetDirectoryName(dir) is { } parent)
            return FolderName(parent);
        return name.Length > 0 ? name : dir;
    }

    private static string? FindStopSibling(string file)
    {
        string dir = Path.GetDirectoryName(file)!;
        string stem = Path.GetFileNameWithoutExtension(file);
        string core = LauncherPrefix().Replace(stem, "");
        core = LauncherSuffix().Replace(core, "").Trim(' ', '-', '_', '.');

        var names = new List<string>();
        var m = StartWord().Match(stem);
        if (m.Success)
        {
            string stop = m.Value.All(char.IsUpper) ? "STOP" : char.IsUpper(m.Value[0]) ? "Stop" : "stop";
            names.Add(stem[..m.Index] + stop + stem[(m.Index + m.Length)..]);
        }
        if (core.Length > 0)
            names.AddRange(new[] { $"Stop-{core}", $"Stop {core}", $"Stop_{core}", $"{core}-Stop", $"{core} Stop", $"{core}_stop", $"stop-{core}" });

        foreach (string n in names.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string ext in new[] { ".cmd", ".bat", ".ps1" })
            {
                string candidate = Path.Combine(dir, n + ext);
                if (!candidate.Equals(file, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>Reads a script plus the scripts it calls (%~dp0x.ps1, $PSScriptRoot\x.ps1, "x.py", …), two levels deep.</summary>
    private static string ReadScriptTree(string file, List<string> scanned, int depth)
    {
        if (depth > 2 || scanned.Contains(file, StringComparer.OrdinalIgnoreCase)) return "";
        string text;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 512 * 1024) return "";
            text = File.ReadAllText(file);
        }
        catch { return ""; }
        scanned.Add(file);

        string dir = Path.GetDirectoryName(file)!;
        var parts = new List<string> { text };
        foreach (Match m in ScriptReference().Matches(text))
        {
            string rel = m.Groups["p"].Value.Trim().Replace('/', '\\');
            if (!ScriptExtensions.Contains(Path.GetExtension(rel).ToLowerInvariant())) continue;
            string full;
            try { full = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(dir, rel)); }
            catch { continue; }
            if (File.Exists(full)) parts.Add(ReadScriptTree(full, scanned, depth + 1));
        }
        return string.Join("\n", parts);
    }

    // ------------------------------------------------------------------ folders

    private static List<Detection> FromFolder(string dir)
    {
        var results = new List<Detection>();

        // 1. Launchers sitting in the folder (Start-X.cmd, X.cmd, start.bat, …)
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir).ToList(); }
        catch { return results; }

        var launchers = files
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".cmd" or ".bat")
            .Where(f => !NotLaunchers.Any(w => Path.GetFileNameWithoutExtension(f).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => StartWord().IsMatch(Path.GetFileNameWithoutExtension(f)))
            .ToList();
        foreach (var l in launchers)
            if (FromFile(l) is { } d) results.Add(d);

        // A folder full of launchers that all point at the same port is one app; keep the best-looking one.
        results = results
            .GroupBy(r => r.App.Port?.ToString() ?? r.App.Start)
            .Select(g => g.OrderByDescending(r => r.App.Stop is not null).ThenByDescending(r => StartWord().IsMatch(r.App.Start ?? "")).First())
            .ToList();

        // 2. Node projects / monorepos
        if (File.Exists(Path.Combine(dir, "package.json")))
        {
            var pkg = FromPackageJson(dir);
            foreach (var p in pkg)
                if (!results.Any(r => r.App.Port is not null && r.App.Port == p.App.Port)) results.Add(p);
        }

        // 3. Django
        if (results.Count == 0 && File.Exists(Path.Combine(dir, "manage.py")))
        {
            results.Add(new Detection(new AppEntry
            {
                Name = FolderName(dir), Port = 8000, Start = "python manage.py runserver 8000", Cwd = dir,
            }, "Django project (manage.py), default port :8000."));
        }

        return results;
    }

    private static List<Detection> FromPackageJson(string dir)
    {
        var results = new List<Detection>();
        JsonElement root;
        try { root = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "package.json")), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement; }
        catch { return results; }

        var scripts = Scripts(root);
        if (scripts.Count == 0) return results;
        string pm = PackageManager(dir);
        // The folder name beats package.json "name": copies/forks share a package name but not a folder,
        // and package names are often generic ("web", "frontend", "app").
        string baseName = FolderName(dir);

        // Monorepo: one app per "dev:<thing>" script.
        var devScripts = scripts.Keys.Where(k => k.StartsWith("dev:", StringComparison.OrdinalIgnoreCase)).ToList();
        if (devScripts.Count > 0)
        {
            foreach (var key in devScripts)
            {
                string suffix = key[4..];
                var (port, why) = PortForScript(dir, scripts[key], root, depth: 0);
                var app = new AppEntry { Name = $"{FolderName(dir)} {Title(suffix)}", Port = port, Start = $"{pm} run {key}", Cwd = dir };
                results.Add(new Detection(app, port is null ? $"\"{key}\" script; no port found." : $"\"{key}\" script, port :{port} {why}."));
            }
            return results;
        }

        foreach (string key in new[] { "dev", "start", "serve", "preview" })
        {
            if (!scripts.TryGetValue(key, out var script)) continue;
            if (key == "start" && script.Contains("node_modules/.bin/electron", StringComparison.OrdinalIgnoreCase)) continue;
            var (port, why) = PortForScript(dir, script, root, depth: 0);
            var app = new AppEntry { Name = baseName, Port = port, Start = $"{pm} run {key}", Cwd = dir };
            results.Add(new Detection(app, port is null ? $"\"{key}\" script; no port found." : $"\"{key}\" script, port :{port} {why}."));
            break;
        }
        return results;
    }

    /// <summary>Works out which port an npm script will listen on.</summary>
    private static (int? Port, string Why) PortForScript(string dir, string script, JsonElement pkg, int depth)
    {
        // Explicit in the script: "next dev -p 3001", "vite --port 5174", "PORT=4000 node server.js"
        var ports = PortsIn(script);
        if (ports.Count > 0) return (ports[0], "from the script");

        // Delegates to a workspace: "npm run dev -w apps/web", "--workspace=apps/web", "--prefix apps/web", "cd apps/web && …"
        var ws = WorkspaceArg().Match(script);
        if (ws.Success && depth < 3)
        {
            string sub = Path.GetFullPath(Path.Combine(dir, ws.Groups["p"].Value.Trim('"', '\'').Replace('/', '\\')));
            if (File.Exists(Path.Combine(sub, "package.json")))
            {
                try
                {
                    var subRoot = JsonDocument.Parse(File.ReadAllText(Path.Combine(sub, "package.json"))).RootElement;
                    var subScripts = Scripts(subRoot);
                    string runName = RunTarget().Match(script) is { Success: true } rt ? rt.Groups["s"].Value : "dev";
                    if (subScripts.TryGetValue(runName, out var subScript))
                    {
                        var (p, why) = PortForScript(sub, subScript, subRoot, depth + 1);
                        if (p is not null) return (p, $"in {Path.GetFileName(sub)}");
                    }
                }
                catch { /* ignore */ }
            }
        }

        // Runs a local script: "node scripts/preview-server.js" → read it
        foreach (Match m in LocalScript().Matches(script))
        {
            string file = Path.Combine(dir, m.Groups["p"].Value.Replace('/', '\\'));
            var scanned = new List<string>();
            var p = PortsIn(ReadScriptTree(file, scanned, 0));
            if (p.Count > 0) return (p[0], $"from {Path.GetFileName(file)}");
        }

        // Config files: vite.config.*, astro.config.* with "port: 1234"
        foreach (var cfg in new[] { "vite.config.ts", "vite.config.js", "vite.config.mjs", "astro.config.mjs", "nuxt.config.ts" })
        {
            string path = Path.Combine(dir, cfg);
            if (!File.Exists(path)) continue;
            var p = PortsIn(File.ReadAllText(path));
            if (p.Count > 0) return (p[0], $"from {cfg}");
        }

        // Framework default
        var deps = Deps(pkg);
        foreach (var (dep, port) in FrameworkPorts)
            if (deps.Contains(dep) || Regex.IsMatch(script, $@"(^|\s){Regex.Escape(dep)}(\s|$)"))
                return (port, $"({dep} default)");

        return (null, "");
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<string> CandidateDirsFromCommandLine(ProcessDetails d)
    {
        var dirs = new List<string>();
        foreach (Match m in CommandToken().Matches(d.CommandLine ?? ""))
        {
            string token = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value;
            if (token.Length < 4 || !Path.IsPathRooted(token)) continue;
            if (d.ExecutablePath is not null && token.Equals(d.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
            string? dir = File.Exists(token) ? Path.GetDirectoryName(token) : Directory.Exists(token) ? token : null;
            if (dir is null) continue;

            int nm = dir.IndexOf(@"\node_modules", StringComparison.OrdinalIgnoreCase);
            if (nm > 0) dir = dir[..nm];
            if (!dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) dirs.Add(dir);
        }
        return dirs;
    }

    private static void ApplyPorts(AppEntry app, string text)
    {
        var ports = PortsIn(text);
        if (ports.Count == 0) return;

        // Prefer the port in the first local URL (that's what you'd open), else the most-mentioned one.
        var url = LocalUrl().Matches(text).Select(m => m).FirstOrDefault(m => int.TryParse(m.Groups["port"].Value, out int p) && ports.Contains(p));
        int primary = url is not null ? int.Parse(url.Groups["port"].Value) : ports[0];
        app.Port = primary;
        app.ExtraPorts = ports.Where(p => p != primary).Take(3).ToList();
        if (url is not null && url.Value.Contains("127.0.0.1")) app.Url = TrimUrl(url.Value);
        else if (url is not null && url.Groups["path"].Value.Length > 1) app.Url = TrimUrl(url.Value);
    }

    /// <summary>Ports mentioned in text, most frequent first (ties: first mention wins).</summary>
    public static List<int> PortsIn(string text)
    {
        var hits = new List<(int Port, int Index)>();
        foreach (Match m in PortMention().Matches(text))
        {
            // "[ValidateRange(1024, 65535)][int]$Port" or "port must be between 1 and 65535" aren't ports.
            // Nor are comparisons: "if ($Port -lt 1024 …)", "port > 1023", "port == 0".
            if (m.Value.Contains("range", StringComparison.OrdinalIgnoreCase) || m.Value.Contains("between", StringComparison.OrdinalIgnoreCase) ||
                ComparisonOperator().IsMatch(m.Value))
                continue;
            foreach (Group g in m.Groups.Cast<Group>().Skip(1))
                if (g.Success && int.TryParse(g.Value, out int p) && p is >= 80 and < 65535 && p != 443)
                    hits.Add((p, m.Index));
        }

        return hits.GroupBy(h => h.Port)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(h => h.Index))
            .Select(g => g.Key)
            .ToList();
    }

    private static Dictionary<string, string> Scripts(JsonElement root)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
            foreach (var p in s.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString()!;
        return d;
    }

    private static HashSet<string> Deps(JsonElement root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "dependencies", "devDependencies" })
            if (root.TryGetProperty(key, out var deps) && deps.ValueKind == JsonValueKind.Object)
                foreach (var p in deps.EnumerateObject()) set.Add(p.Name);
        return set;
    }

    private static string PackageManager(string dir)
    {
        for (string? d = dir; d is not null; d = Path.GetDirectoryName(d))
        {
            if (File.Exists(Path.Combine(d, "pnpm-lock.yaml"))) return "pnpm";
            if (File.Exists(Path.Combine(d, "yarn.lock"))) return "yarn";
            if (File.Exists(Path.Combine(d, "bun.lockb")) || File.Exists(Path.Combine(d, "bun.lock"))) return "bun";
            if (File.Exists(Path.Combine(d, "package-lock.json"))) return "npm";
            if (Depth(dir) - Depth(d) >= 3) break;
        }
        return "npm";
    }

    private static List<Detection> Dedupe(List<Detection> all) =>
        all.GroupBy(d => (d.App.Start ?? "") + "|" + (d.App.Cwd ?? ""), StringComparer.OrdinalIgnoreCase)
           .Select(g => g.First())
           .ToList();

    private static string Ports(AppEntry a) => string.Join(" ", a.AllPorts.Select(p => ":" + p));

    private static string Title(string s) =>
        string.Join(" ", s.Split('-', '_', ':', ' ').Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string TrimUrl(string url) => url.TrimEnd('/', '.', ',', ';', ')');

    private static string Sentence(List<string> notes) =>
        notes.Count == 0 ? "" : char.ToUpperInvariant(notes[0][0]) + string.Join("; ", notes)[1..] + ".";

    private static int Depth(string path) => path.TrimEnd('\\').Count(c => c == '\\');

    [GeneratedRegex(@"^(start|launch|run|open)[\s\-_]*", RegexOptions.IgnoreCase)]
    private static partial Regex LauncherPrefix();

    [GeneratedRegex(@"[\s\-_]*(start|launcher|launch|run)$", RegexOptions.IgnoreCase)]
    private static partial Regex LauncherSuffix();

    [GeneratedRegex(@"(?<![a-z])start(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex StartWord();

    [GeneratedRegex(@"(?:%~dp0|\$PSScriptRoot[\\/]|\./|\.\\)(?<p>[^""'\s%*|<>]+)|""(?<p>[^""%*|<>\r\n]+\.(?:ps1|cmd|bat|py|js|mjs|cjs))""", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptReference();

    // "port 8765", "$Port = 8765", "--port=5173", "-Port 3000", "PORT: 4000", "-p 3001", "localhost:4780", "127.0.0.1:8000",
    // "python -m http.server 8000", "manage.py runserver 8000"
    [GeneratedRegex(@"\bport\b[^\n]{0,60}?(?<![\d.])(\d{2,5})(?![\d.])|(?:^|\s)-p\s+(\d{2,5})\b|(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):(\d{2,5})\b|(?:http\.server|runserver)\s+(?:[\d.]+:)?(\d{2,5})\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PortMention();

    [GeneratedRegex(@"\s-(?:lt|gt|le|ge|eq|ne)\s|[<>]|[=!]=", RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonOperator();

    [GeneratedRegex(@"https?://(?:localhost|127\.0\.0\.1):(?<port>\d{2,5})(?<path>/[^\s""'`)<>]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex LocalUrl();

    [GeneratedRegex(@"(?:-w|--workspace|--prefix|--filter|--cwd)[=\s]+(?<p>""[^""]+""|'[^']+'|[^\s&|]+)|\bcd\s+(?<p>[^\s&|]+)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkspaceArg();

    [GeneratedRegex(@"\brun\s+(?<s>[\w:\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RunTarget();

    [GeneratedRegex(@"\b(?:node|tsx|ts-node|python3?|bun)\s+(?<p>[\w./\\-]+\.(?:js|mjs|cjs|ts|py))", RegexOptions.IgnoreCase)]
    private static partial Regex LocalScript();

    [GeneratedRegex(@"""(?<q>[^""]+)""|(?<u>[^\s""]+)")]
    private static partial Regex CommandToken();
}
