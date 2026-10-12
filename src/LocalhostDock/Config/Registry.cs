using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalhostDock.Config;

/// <summary>One app or dev server in the registry.</summary>
public sealed class AppEntry
{
    public string Name { get; set; } = "";

    /// <summary>Primary port. The status dot is green when something is listening on it.</summary>
    public int? Port { get; set; }

    /// <summary>Other ports that belong to the app (e.g. an API behind a web front end). Stop frees these too.</summary>
    public List<int> ExtraPorts { get; set; } = new();

    /// <summary>What Open launches. Defaults to http://localhost:{port}.</summary>
    public string? Url { get; set; }

    /// <summary>Start command. Usually the path to an existing .cmd/.bat launcher; any command line works.</summary>
    public string? Start { get; set; }

    /// <summary>Optional stop command (e.g. your Stop-*.cmd). If empty, Stop kills whatever owns the app's ports.</summary>
    public string? Stop { get; set; }

    /// <summary>Working directory for Start/Stop. Defaults to the launcher's folder.</summary>
    public string? Cwd { get; set; }

    /// <summary>Window style for the launched console: "minimized" (default), "normal" or "hidden".</summary>
    public string Window { get; set; } = "minimized";

    /// <summary>Redirect the start command's output to a log file Localhost Dock can tail (runs hidden).</summary>
    public bool CaptureLogs { get; set; }

    /// <summary>A log file your app already writes, shown by View logs.</summary>
    public string? LogFile { get; set; }

    [JsonIgnore]
    public IEnumerable<int> AllPorts => (Port is int p ? new[] { p } : Array.Empty<int>()).Concat(ExtraPorts).Distinct();

    [JsonIgnore]
    public string? EffectiveUrl => !string.IsNullOrWhiteSpace(Url) ? Url : Port is int p ? $"http://localhost:{p}" : null;

    /// <summary>The folder the app lives in: cwd, or the launcher's folder.</summary>
    [JsonIgnore]
    public string? Folder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Cwd) && Directory.Exists(Cwd)) return Cwd;
            string? start = Start?.Trim().Trim('"');
            return start is not null && File.Exists(start) ? Path.GetDirectoryName(start) : null;
        }
    }

    [JsonIgnore]
    public string? EffectiveLogFile =>
        !string.IsNullOrWhiteSpace(LogFile) ? LogFile : CaptureLogs ? Registry.CapturedLogPath(Name) : null;

    public AppEntry Clone() => new()
    {
        Name = Name, Port = Port, ExtraPorts = ExtraPorts.ToList(), Url = Url, Start = Start, Stop = Stop,
        Cwd = Cwd, Window = Window, CaptureLogs = CaptureLogs, LogFile = LogFile,
    };
}

/// <summary>A named set of apps started and stopped together.</summary>
public sealed class GroupEntry
{
    public string Name { get; set; } = "";
    public List<string> Apps { get; set; } = new();
}

public sealed class Registry
{
    public List<AppEntry> Apps { get; set; } = new();

    public List<GroupEntry> Groups { get; set; } = new();

    /// <summary>Ports the "discovered" list should never offer (you clicked Ignore).</summary>
    public List<int> IgnoredPorts { get; set; } = new();

    /// <summary>Status poll interval in milliseconds.</summary>
    public int PollMs { get; set; } = 2000;

    /// <summary>Global shortcut that opens the dock, e.g. "Ctrl+Alt+L". Empty to disable.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+L";

    /// <summary>Tray notifications: "all" (crashes + started), "crashes", or "off".</summary>
    public string Notifications { get; set; } = "all";

    /// <summary>Folders remembered from "Scan folder for apps".</summary>
    public List<string> ScanFolders { get; set; } = new();

    public AppEntry? Find(string name) =>
        Apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a name not yet used by any app ("Web", "Web 2", …).</summary>
    public string UniqueName(string name, AppEntry? except = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? "App" : name.Trim();
        string candidate = name;
        for (int i = 2; Apps.Any(a => a != except && a.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{name} {i}";
        return candidate;
    }

    /// <summary>Renames an app and keeps group memberships pointing at it.</summary>
    public void Rename(string oldName, string newName)
    {
        foreach (var g in Groups)
            for (int i = 0; i < g.Apps.Count; i++)
                if (g.Apps[i].Equals(oldName, StringComparison.OrdinalIgnoreCase)) g.Apps[i] = newName;
    }

    public void Remove(AppEntry app)
    {
        Apps.Remove(app);
        foreach (var g in Groups) g.Apps.RemoveAll(n => n.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Folder =>
        Environment.GetEnvironmentVariable("LOCALHOST_DOCK_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalhostDock");

    public static string FilePath => Path.Combine(Folder, "apps.json");

    public static string LogsFolder => Path.Combine(Folder, "logs");

    public static string CapturedLogPath(string appName)
    {
        string safe = string.Concat(appName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(LogsFolder, safe + ".log");
    }

    /// <summary>Loads the registry, creating it from apps.example.json on first run.</summary>
    public static Registry Load()
    {
        Directory.CreateDirectory(Folder);
        if (!File.Exists(FilePath))
        {
            string example = Path.Combine(AppContext.BaseDirectory, "apps.example.json");
            if (File.Exists(example)) File.Copy(example, FilePath);
            else new Registry().Save();
        }

        var reg = JsonSerializer.Deserialize<Registry>(File.ReadAllText(FilePath), Json) ?? new Registry();
        reg.Apps.RemoveAll(a => string.IsNullOrWhiteSpace(a.Name));
        reg.Groups.RemoveAll(g => string.IsNullOrWhiteSpace(g.Name));
        if (reg.PollMs < 500) reg.PollMs = 500;
        return reg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
