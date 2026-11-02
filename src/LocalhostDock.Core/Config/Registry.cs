using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalhostDock.Net;
using LocalhostDock.Integration;

namespace LocalhostDock.Config;

/// <summary>One app or dev server in the registry.</summary>
public sealed class AppEntry
{
    /// <summary>Immutable identity. Groups and runtime state refer to apps by Id, so renaming is safe.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Primary port. The status dot is green when this app's process is listening on it.</summary>
    public int? Port { get; set; }

    /// <summary>Other ports that belong to the app (e.g. an API behind a web front end). Stop frees these too.</summary>
    public List<int> ExtraPorts { get; set; } = new();

    /// <summary>What Open launches. Defaults to http://localhost:{port}.</summary>
    public string? Url { get; set; }

    /// <summary>Start command. Usually the path to an existing .cmd/.bat launcher; any command line works.</summary>
    public string? Start { get; set; }

    /// <summary>Optional stop command (e.g. your Stop-*.cmd). Runs before anything is terminated.</summary>
    public string? Stop { get; set; }

    /// <summary>Working directory for Start/Stop. Defaults to the launcher's folder.</summary>
    public string? Cwd { get; set; }

    /// <summary>Window style for the launched console: "minimized" (default), "normal" or "hidden".</summary>
    public string Window { get; set; } = "minimized";

    /// <summary>Record the start command's output to a log file Localhost Dock can tail (runs without a window).</summary>
    public bool CaptureLogs { get; set; }

    /// <summary>A log file your app already writes, shown by View logs.</summary>
    public string? LogFile { get; set; }

    /// <summary>"graceful" (default: ask, then force), "graceful-only" (never force-kill) or "force".</summary>
    public string StopPolicy { get; set; } = "graceful";

    /// <summary>Seconds to wait for a graceful exit before forcing (default 8).</summary>
    public int GracefulTimeoutSec { get; set; } = 8;

    /// <summary>Seconds to wait for the port to come up after Start (default 90).</summary>
    public int StartTimeoutSec { get; set; } = 90;

    /// <summary>
    /// The start command was reconstructed (e.g. from a running process) and hasn't yet been seen to work.
    /// Cleared automatically after the first successful start.
    /// </summary>
    public bool StartUnverified { get; set; }

    /// <summary>
    /// HTTP health check: a path ("/healthz", checked on http://localhost:{port}) or a full URL on this machine
    /// (localhost / 127.x / ::1 only). A 2xx or 3xx answer is healthy. When set, "ready" means healthy, not just listening.
    /// </summary>
    public string? Health { get; set; }

    /// <summary>Seconds between health checks while the app is up (default 10, 2–3600).</summary>
    public int HealthIntervalSec { get; set; } = 10;

    /// <summary>Health check timeout in milliseconds (default 3000, 200–60000).</summary>
    public int HealthTimeoutMs { get; set; } = 3000;

    /// <summary>Open the URL in the browser once a Start from the dock becomes ready.</summary>
    public bool OpenOnReady { get; set; }

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
            if (!string.IsNullOrWhiteSpace(Cwd) && Directory.Exists(Cwd)) return Path.GetFullPath(Cwd);
            return !string.IsNullOrWhiteSpace(Start) ? Core.Launcher.Build(Start, Cwd).DefaultCwd : null;
        }
    }

    [JsonIgnore]
    public string? EffectiveLogFile =>
        !string.IsNullOrWhiteSpace(LogFile) ? LogFile : CaptureLogs ? Registry.CapturedLogPath(this) : null;

    [JsonIgnore]
    public Net.StopPolicy Policy => StopPolicy?.ToLowerInvariant() switch
    {
        "graceful-only" => Net.StopPolicy.GracefulOnly,
        "force" => Net.StopPolicy.Force,
        _ => Net.StopPolicy.Graceful,
    };

    public AppEntry Clone() => new()
    {
        Id = Id, Name = Name, Port = Port, ExtraPorts = ExtraPorts.ToList(), Url = Url, Start = Start, Stop = Stop,
        Cwd = Cwd, Window = Window, CaptureLogs = CaptureLogs, LogFile = LogFile, StopPolicy = StopPolicy,
        GracefulTimeoutSec = GracefulTimeoutSec, StartTimeoutSec = StartTimeoutSec, StartUnverified = StartUnverified,
        Health = Health, HealthIntervalSec = HealthIntervalSec, HealthTimeoutMs = HealthTimeoutMs, OpenOnReady = OpenOnReady,
    };
}

/// <summary>
/// A workspace: a named set of apps with a dependency graph. Start brings apps up dependencies-first (apps whose
/// dependencies are ready start in parallel, up to <see cref="MaxParallel"/>); Stop goes dependents-first.
/// </summary>
public sealed class WorkspaceEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Member app ids (display order). Names are accepted when hand-editing and converted on load.</summary>
    public List<string> Apps { get; set; } = new();

    /// <summary>app id → ids of the members it needs to be ready first. Missing = no dependencies.</summary>
    public Dictionary<string, List<string>> DependsOn { get; set; } = new();

    /// <summary>"stop" (default): when a member fails, start nothing more. "continue": start everything whose dependencies are ready.</summary>
    public string OnFailure { get; set; } = "stop";

    /// <summary>How many members may start at the same time (default 4, 1–16).</summary>
    public int MaxParallel { get; set; } = 4;

    public List<string> DependenciesOf(string appId) => DependsOn.TryGetValue(appId, out var d) ? d : new();

    public WorkspaceEntry Clone() => new()
    {
        Id = Id, Name = Name, Apps = Apps.ToList(), OnFailure = OnFailure, MaxParallel = MaxParallel,
        DependsOn = DependsOn.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
    };

    /// <summary>A workspace whose members start one after another in list order (how v0.3 groups behaved).</summary>
    public static WorkspaceEntry Chain(string id, string name, List<string> apps, string onFailure)
    {
        var ws = new WorkspaceEntry { Id = id, Name = name, Apps = apps.ToList(), OnFailure = onFailure };
        for (int i = 1; i < apps.Count; i++) ws.DependsOn[apps[i]] = new() { apps[i - 1] };
        return ws;
    }
}

public sealed class RegistryConflictException(string message) : Exception(message);

public sealed class Registry
{
    public const int CurrentSchema = 3;

    /// <summary>Schema version of the file. Missing = 1 (v0.1/v0.2 files), migrated on load.</summary>
    public int SchemaVersion { get; set; } = CurrentSchema;

    public List<AppEntry> Apps { get; set; } = new();

    public List<WorkspaceEntry> Workspaces { get; set; } = new();

    /// <summary>Opt-in, authenticated, read-only loopback status for Aura. Port 0 selects a free port.</summary>
    public AuraOptions Aura { get; set; } = new();

    /// <summary>Schema 1–2 "groups" (ordered lists). Read only to migrate them into workspaces; never written.</summary>
    [JsonPropertyName("groups")]
    public List<LegacyGroup>? LegacyGroups { get; set; }

    public sealed class LegacyGroup
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> Apps { get; set; } = new();
        public string OnFailure { get; set; } = "stop";
    }

    /// <summary>Ports the "discovered" list should never offer (you clicked Ignore).</summary>
    public List<int> IgnoredPorts { get; set; } = new();

    /// <summary>Status poll interval in milliseconds.</summary>
    public int PollMs { get; set; } = 2000;

    /// <summary>Global shortcut that opens the dock, e.g. "Ctrl+Alt+L". Empty to disable.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+L";

    /// <summary>Tray notifications: "all" (crashes + started), "crashes", or "off".</summary>
    public string Notifications { get; set; } = "all";

    /// <summary>
    /// After a reboot or crash, apps that were running: "ask" (default: offer to start them again), "auto" (start
    /// them, skipping unverified start commands) or "off".
    /// </summary>
    public string RestoreSession { get; set; } = "ask";

    /// <summary>Folders remembered from "Scan folder for apps".</summary>
    public List<string> ScanFolders { get; set; } = new();

    /// <summary>All reads and writes of this registry from different threads take this lock.</summary>
    [JsonIgnore] public object SyncRoot { get; } = new();

    /// <summary>Problems fixed while loading (shown to the user once).</summary>
    [JsonIgnore] public List<string> LoadWarnings { get; } = new();

    // Hash of the file as we last read/wrote it; a different hash on disk means someone else edited it.
    [JsonIgnore] private string? _diskHash;
    [JsonIgnore] private bool _readOnly;

    public AppEntry? Find(string idOrName) =>
        Apps.FirstOrDefault(a => a.Id == idOrName) ??
        Apps.FirstOrDefault(a => a.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public AppEntry? ById(string id) => Apps.FirstOrDefault(a => a.Id == id);

    /// <summary>Returns a name not yet used by any app ("Web", "Web 2", …).</summary>
    public string UniqueName(string name, AppEntry? except = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? "App" : name.Trim();
        string candidate = name;
        for (int i = 2; Apps.Any(a => a != except && a.Id != except?.Id && a.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{name} {i}";
        return candidate;
    }

    public void Add(AppEntry app)
    {
        if (string.IsNullOrEmpty(app.Id) || Apps.Any(a => a.Id == app.Id)) app.Id = NewId();
        app.Name = UniqueName(app.Name, app);
        Apps.Add(app);
    }

    /// <summary>Replaces the entry with the same Id (edit dialog result).</summary>
    public void Replace(AppEntry updated)
    {
        int i = Apps.FindIndex(a => a.Id == updated.Id);
        if (i >= 0) Apps[i] = updated;
    }

    public void Remove(AppEntry app)
    {
        Apps.RemoveAll(a => a.Id == app.Id);
        foreach (var w in Workspaces)
        {
            w.Apps.RemoveAll(id => id == app.Id);
            w.DependsOn.Remove(app.Id);
            foreach (var deps in w.DependsOn.Values) deps.RemoveAll(id => id == app.Id);
        }
    }

    public WorkspaceEntry? WorkspaceById(string id) => Workspaces.FirstOrDefault(w => w.Id == id);

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];

    // ================================================================ normalization & migration

    /// <summary>
    /// Repairs anything a hand edit (or an older version) could leave behind: null lists, bad ports, duplicate
    /// names/ids, unknown enum values, group members that don't exist. Returns what it changed.
    /// </summary>
    public List<string> Normalize()
    {
        var notes = new List<string>();
        Apps ??= new();
        Workspaces ??= new();
        IgnoredPorts ??= new();
        ScanFolders ??= new();
        Aura ??= new();
        if (Aura.Port is < 0 or > 65535)
        {
            Aura.Enabled = false;
            Aura.Port = 0;
            notes.Add("Aura status was disabled because its port is invalid (0–65535).");
        }

        int removed = Apps.RemoveAll(a => a is null || string.IsNullOrWhiteSpace(a.Name));
        if (removed > 0) notes.Add($"Removed {removed} app entr{(removed == 1 ? "y" : "ies")} without a name.");

        var ids = new HashSet<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Apps)
        {
            a.Name = a.Name.Trim();
            a.ExtraPorts ??= new();
            if (string.IsNullOrWhiteSpace(a.Id) || !ids.Add(a.Id))
            {
                a.Id = NewId();
                ids.Add(a.Id);
            }
            if (!names.Add(a.Name))
            {
                string old = a.Name;
                a.Name = UniqueName(a.Name, a);
                names.Add(a.Name);
                notes.Add($"Two apps were called “{old}”; renamed one to “{a.Name}”.");
            }
            if (a.Port is int p && p is < 1 or > 65535)
            {
                notes.Add($"{a.Name}: port {p} isn't valid (1–65535) and was cleared.");
                a.Port = null;
            }
            int bad = a.ExtraPorts.RemoveAll(x => x is < 1 or > 65535);
            if (bad > 0) notes.Add($"{a.Name}: removed {bad} invalid extra port(s).");
            a.ExtraPorts = a.ExtraPorts.Distinct().Where(x => x != a.Port).ToList();
            if (a.Window is not ("minimized" or "normal" or "hidden")) a.Window = "minimized";
            if (a.StopPolicy is not ("graceful" or "graceful-only" or "force")) a.StopPolicy = "graceful";
            a.GracefulTimeoutSec = Math.Clamp(a.GracefulTimeoutSec <= 0 ? 8 : a.GracefulTimeoutSec, 1, 300);
            a.StartTimeoutSec = Math.Clamp(a.StartTimeoutSec <= 0 ? 90 : a.StartTimeoutSec, 5, 1800);
            a.HealthIntervalSec = Math.Clamp(a.HealthIntervalSec <= 0 ? 10 : a.HealthIntervalSec, 2, 3600);
            a.HealthTimeoutMs = Math.Clamp(a.HealthTimeoutMs <= 0 ? 3000 : a.HealthTimeoutMs, 200, 60000);
            if (string.IsNullOrWhiteSpace(a.Health)) a.Health = null;
            else if (Health.HealthUrl.Resolve(a) is null)
            {
                notes.Add($"{a.Name}: health check “{a.Health}” isn't a path or a URL on this machine (localhost, 127.x, ::1), so it was turned off.");
                a.Health = null;
            }
        }

        foreach (var clash in Apps.Where(a => a.Port is not null).GroupBy(a => a.Port).Where(g => g.Count() > 1))
            notes.Add($"Port :{clash.Key} is used by {string.Join(" and ", clash.Select(a => a.Name))}; only one can run at a time.");

        // Schema 1–2 groups become workspaces whose members depend on the previous one (same start order as before).
        if (LegacyGroups is { Count: > 0 })
            foreach (var g in LegacyGroups.Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Name)))
            {
                var members = (g.Apps ?? new()).Select(m => Find(m)?.Id ?? m).ToList();
                Workspaces.Add(WorkspaceEntry.Chain(string.IsNullOrWhiteSpace(g.Id) ? NewId() : g.Id, g.Name, members, g.OnFailure ?? "stop"));
            }
        LegacyGroups = null;

        var wsIds = new HashSet<string>();
        var wsNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Workspaces.RemoveAll(w => w is null || string.IsNullOrWhiteSpace(w.Name));
        foreach (var w in Workspaces)
        {
            w.Apps ??= new();
            w.DependsOn ??= new();
            w.Name = w.Name.Trim();
            if (string.IsNullOrWhiteSpace(w.Id) || !wsIds.Add(w.Id)) { w.Id = NewId(); wsIds.Add(w.Id); }
            if (!wsNames.Add(w.Name)) { w.Name += $" ({w.Id[..4]})"; wsNames.Add(w.Name); }
            if (w.OnFailure is not ("stop" or "continue")) w.OnFailure = "stop";
            w.MaxParallel = Math.Clamp(w.MaxParallel <= 0 ? 4 : w.MaxParallel, 1, 16);

            var resolved = new List<string>();
            foreach (var member in w.Apps)
            {
                var app = Find(member);
                if (app is null) notes.Add($"Workspace “{w.Name}”: no app called “{member}”; removed it.");
                else if (!resolved.Contains(app.Id)) resolved.Add(app.Id);
            }
            w.Apps = resolved;

            // Dependencies: resolve names → ids, keep only members, no self-dependencies.
            var deps = new Dictionary<string, List<string>>();
            foreach (var (key, values) in w.DependsOn)
            {
                if (Find(key)?.Id is not { } id || !resolved.Contains(id)) continue;
                var list = (values ?? new()).Select(v => Find(v)?.Id).Where(v => v is not null && v != id && resolved.Contains(v!)).Select(v => v!).Distinct().ToList();
                if (list.Count > 0) deps[id] = list;
            }
            w.DependsOn = deps;

            if (Workspace.WorkspacePlan.FindCycle(w) is { } cycle)
            {
                foreach (var id in cycle) w.DependsOn.Remove(id);
                notes.Add($"Workspace “{w.Name}”: the dependencies between {string.Join(", ", cycle.Select(id => ById(id)?.Name ?? id))} formed a loop, so they were removed.");
            }
        }

        IgnoredPorts = IgnoredPorts.Where(p => p is >= 1 and <= 65535).Distinct().ToList();
        if (Notifications is not ("all" or "crashes" or "off")) Notifications = "all";
        if (RestoreSession is not ("ask" or "auto" or "off")) RestoreSession = "ask";
        if (PollMs < 500) PollMs = 500;
        Hotkey ??= "";
        return notes;
    }

    // ================================================================ files

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A local config file meant for humans: write "Ctrl+Alt+L", not "Ctrl+Alt+L".
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Folder =>
        Environment.GetEnvironmentVariable("LOCALHOST_DOCK_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalhostDock");

    public static string FilePath => Path.Combine(Folder, "apps.json");
    public static string BackupFolder => Path.Combine(Folder, "backups");
    public static string LogsFolder => Path.Combine(Folder, "logs");

    /// <summary>Log path for captured output: sanitized name plus a short id, so "a/b" and "a?b" never collide.</summary>
    public static string CapturedLogPath(AppEntry app)
    {
        string safe = string.Concat(app.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length > 60) safe = safe[..60];
        string id = string.IsNullOrEmpty(app.Id) ? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(app.Name)))[..8] : app.Id;
        return Path.Combine(LogsFolder, $"{safe}.{id}.log");
    }

    /// <summary>
    /// Loads the registry: creates it from apps.example.json on first run, recovers from a corrupt file using the
    /// newest good backup, migrates older schemas and normalizes the data. Problems are listed in LoadWarnings.
    /// With <paramref name="readOnly"/> (used by the CLI) nothing on disk is created, migrated, backed up or repaired.
    /// </summary>
    public static Registry Load(bool readOnly = false)
    {
        if (readOnly)
        {
            if (!File.Exists(FilePath)) throw new FileNotFoundException($"No registry yet at {FilePath}. Start Localhost Dock once to create it.");
            string current = File.ReadAllText(FilePath);
            var ro = Parse(current); // throws JsonException on a corrupt file: never "repair" from the CLI
            if (ro.SchemaVersion > CurrentSchema)
                throw new InvalidOperationException($"apps.json was written by a newer Localhost Dock (schema {ro.SchemaVersion}).");
            ro.LoadWarnings.AddRange(ro.Normalize());
            ro.SchemaVersion = CurrentSchema;
            ro._diskHash = Hash(current);
            ro._readOnly = true;
            return ro;
        }

        Directory.CreateDirectory(Folder);
        if (!File.Exists(FilePath))
        {
            string example = Path.Combine(AppContext.BaseDirectory, "apps.example.json");
            if (File.Exists(example)) File.Copy(example, FilePath);
            else File.WriteAllText(FilePath, JsonSerializer.Serialize(new Registry(), Json));
        }

        var warnings = new List<string>();
        string text = File.ReadAllText(FilePath);
        Registry reg;
        try
        {
            reg = Parse(text);
        }
        catch (JsonException ex)
        {
            string corrupt = Path.Combine(Folder, $"apps.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(FilePath, corrupt, overwrite: true);
            warnings.Add($"apps.json couldn't be read ({ex.Message.Split('.')[0]}). It was kept as {Path.GetFileName(corrupt)}.");

            reg = RestoreNewestBackup(warnings) ?? new Registry();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(reg, Json));
            text = File.ReadAllText(FilePath);
        }

        bool migrated = false;
        if (reg.SchemaVersion < CurrentSchema)
        {
            Backup(text, $"pre-v{CurrentSchema}");
            warnings.Add($"Upgraded apps.json from schema {reg.SchemaVersion} to {CurrentSchema} (the original was backed up first).");
            reg.SchemaVersion = CurrentSchema;
            migrated = true;
        }
        else if (reg.SchemaVersion > CurrentSchema)
        {
            throw new InvalidOperationException(
                $"apps.json was written by a newer Localhost Dock (schema {reg.SchemaVersion}). Update the app, or restore a backup from {BackupFolder}.");
        }

        warnings.AddRange(reg.Normalize());
        if (migrated)
        {
            // Write the migrated form once (ids assigned, groups by id) so the next load doesn't migrate again.
            text = JsonSerializer.Serialize(reg, Json);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, FilePath, overwrite: true);
        }
        reg._diskHash = Hash(text);
        reg.LoadWarnings.AddRange(warnings);
        return reg;
    }

    internal static Registry Parse(string text)
    {
        // Files from v0.1/v0.2 have no schemaVersion: treat that as 1.
        using (var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("the top level isn't an object");
            bool hasVersion = doc.RootElement.TryGetProperty("schemaVersion", out _);
            var reg = JsonSerializer.Deserialize<Registry>(text, Json) ?? throw new JsonException("empty");
            if (!hasVersion) reg.SchemaVersion = 1;
            return reg;
        }
    }

    private static Registry? RestoreNewestBackup(List<string> warnings)
    {
        if (!Directory.Exists(BackupFolder)) return null;
        foreach (var file in Directory.GetFiles(BackupFolder, "apps-*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var reg = Parse(File.ReadAllText(file));
                warnings.Add($"Restored your apps from the backup {Path.GetFileName(file)}.");
                return reg;
            }
            catch (JsonException) { /* try an older one */ }
        }
        warnings.Add("No readable backup was found, so the dock starts empty.");
        return null;
    }

    /// <summary>
    /// Saves atomically, keeping the previous version as a backup. Throws <see cref="RegistryConflictException"/>
    /// if apps.json was changed by someone else since it was loaded (so their edit is never overwritten).
    /// </summary>
    public void Save()
    {
        if (_readOnly) throw new InvalidOperationException("This registry was loaded read-only (CLI); it can't be saved.");
        Directory.CreateDirectory(Folder);
        string json = JsonSerializer.Serialize(this, Json);
        if (File.Exists(FilePath))
        {
            string current = File.ReadAllText(FilePath);
            string currentHash = Hash(current);
            if (_diskHash is not null && currentHash != _diskHash)
                throw new RegistryConflictException("apps.json was changed outside Localhost Dock since it was loaded.");
            if (current == json) return;
            Backup(current, null);
        }
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
        _diskHash = Hash(json);
    }

    /// <summary>True if the file on disk differs from what this registry last read or wrote.</summary>
    public bool ChangedOnDisk()
    {
        try { return File.Exists(FilePath) && Hash(File.ReadAllText(FilePath)) != _diskHash; }
        catch (IOException) { return false; }
    }

    private static void Backup(string content, string? tag)
    {
        try
        {
            Directory.CreateDirectory(BackupFolder);
            string name = $"apps-{DateTime.Now:yyyyMMdd-HHmmss-fff}{(tag is null ? "" : "-" + tag)}.json";
            File.WriteAllText(Path.Combine(BackupFolder, name), content);
            foreach (var old in Directory.GetFiles(BackupFolder, "apps-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(20))
                File.Delete(old);
        }
        catch (IOException) { /* a missing backup must never block saving */ }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
