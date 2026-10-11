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

    [JsonIgnore]
    public IEnumerable<int> AllPorts => (Port is int p ? new[] { p } : Array.Empty<int>()).Concat(ExtraPorts).Distinct();

    [JsonIgnore]
    public string? EffectiveUrl => !string.IsNullOrWhiteSpace(Url) ? Url : Port is int p ? $"http://localhost:{p}" : null;
}

public sealed class Registry
{
    public List<AppEntry> Apps { get; set; } = new();

    /// <summary>Ports the "discovered" list should never offer (you clicked Ignore).</summary>
    public List<int> IgnoredPorts { get; set; } = new();

    /// <summary>Status poll interval in milliseconds.</summary>
    public int PollMs { get; set; } = 2000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Folder =>
        Environment.GetEnvironmentVariable("LOCALHOST_DOCK_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalhostDock");

    public static string FilePath => Path.Combine(Folder, "apps.json");

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
