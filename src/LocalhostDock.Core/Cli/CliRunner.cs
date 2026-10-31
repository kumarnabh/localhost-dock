using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Net;
using LocalhostDock.Workspace;

namespace LocalhostDock.Cli;

/// <summary>
/// The <c>ldock</c> command line: read-only, machine-readable status for scripts and AI coding agents.
/// It never starts, stops or changes anything, and never writes apps.json. Output schemas are versioned
/// ("localhost-dock/status@1", "localhost-dock/manifest@1"); see docs/CLI.md.
/// </summary>
public static partial class CliRunner
{
    public const int Ok = 0, Usage = 2, Failure = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        try
        {
            switch (command)
            {
                case "status": return await StatusAsync(output, json, checkHealth: !args.Contains("--no-health", StringComparer.OrdinalIgnoreCase));
                case "who": return Who(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")), output, error, json);
                case "manifest": return Manifest(output);
                case "workspaces": return Workspaces(output, json);
                case "version": case "--version": output.WriteLine(Version()); return Ok;
                case "help": case "--help": case "-h": case "/?": Help(output); return Ok;
                default:
                    error.WriteLine($"Unknown command “{command}”.");
                    Help(error);
                    return Usage;
            }
        }
        catch (FileNotFoundException ex) { error.WriteLine(ex.Message); return Failure; }
        catch (JsonException ex) { error.WriteLine($"apps.json can't be read ({ex.Message}). Open Localhost Dock to repair it."); return Failure; }
        catch (InvalidOperationException ex) { error.WriteLine(ex.Message); return Failure; }
    }

    // ---------------------------------------------------------------- status

    public sealed record HealthJson(bool? Healthy, int? Status, double? LatencyMs, string? Error, int ConsecutiveFailures, double Availability);
    public sealed record ProcessJson(int Pid, string Name, string Address, int Port, Ownership Ownership);
    public sealed record AppJson(string Id, string Name, AppState State, int? Port, IReadOnlyList<int> ExtraPorts, string? Url,
        IReadOnlyList<ProcessJson> Processes, double? UptimeSec, long MemoryBytes, HealthJson? Health, double? LastStartSec,
        string? LastError, int? LastExitCode, bool? LastExitExpected, bool StartUnverified);
    public sealed record WorkspaceJson(string Id, string Name, int Ready, int Total, IReadOnlyList<string> Apps, IReadOnlyList<string> NotReady);
    public sealed record StatusJson(string Schema, DateTime GeneratedAt, IReadOnlyList<AppJson> Apps, IReadOnlyList<WorkspaceJson> Workspaces);

    public static async Task<StatusJson> BuildStatusAsync(bool checkHealth)
    {
        var registry = Registry.Load(readOnly: true);
        using var controller = new AppController(registry);
        var first = controller.Poll();
        if (checkHealth)
        {
            // One fresh health check per running app that has one (localhost only, each with its own timeout).
            await Task.WhenAll(first.Apps.Where(s => s.State is AppState.Up or AppState.Degraded && s.App.Health is not null)
                .Select(s => controller.Health.CheckNowAsync(s.App, CancellationToken.None)));
        }
        var snap = checkHealth ? controller.Poll() : first;

        var apps = snap.Apps.Select(s => new AppJson(
            s.App.Id, s.App.Name, s.State, s.App.Port, s.App.ExtraPorts, s.App.EffectiveUrl,
            s.Listeners.Select(l => new ProcessJson(l.Listener.Pid, l.Identity?.Name ?? "?", l.Listener.Address, l.Listener.Port, l.Ownership)).ToList(),
            s.UpSince is { } since ? Math.Round((DateTime.Now - since).TotalSeconds) : null,
            s.MemoryBytes,
            s.Health is { } h ? new HealthJson(h.Healthy, h.Last?.Status, h.Last is null ? null : Math.Round(h.Last.LatencyMs, 1), h.Last?.Error, h.ConsecutiveFailures, Math.Round(h.Availability, 3)) : null,
            s.LastStartDuration?.TotalSeconds is double d ? Math.Round(d, 1) : null,
            s.LastError, s.LastExit?.Code, s.LastExit?.Expected, s.App.StartUnverified)).ToList();

        var workspaces = snap.Workspaces.Select(w => new WorkspaceJson(w.Workspace.Id, w.Workspace.Name, w.Up, w.Members.Count,
            w.Members.Select(m => m.App.Name).ToList(),
            w.Members.Where(m => m.State != AppState.Up).Select(m => m.App.Name).ToList())).ToList();

        return new StatusJson("localhost-dock/status@1", DateTime.UtcNow, apps, workspaces);
    }

    private static async Task<int> StatusAsync(TextWriter output, bool json, bool checkHealth)
    {
        var status = await BuildStatusAsync(checkHealth);
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(status, Json));
            return Ok;
        }
        foreach (var a in status.Apps)
        {
            string state = JsonNamingPolicy.KebabCaseLower.ConvertName(a.State.ToString());
            string health = a.Health is { } h ? h.Healthy == true ? $" · healthy {h.LatencyMs} ms" : h.Healthy == false ? $" · unhealthy ({h.Status?.ToString() ?? h.Error})" : "" : "";
            output.WriteLine($"{a.Name,-28} {(a.Port is int p ? ":" + p : ""),-7} {state,-10}{health}{(a.LastError is { } e ? " · " + e : "")}");
        }
        foreach (var w in status.Workspaces)
            output.WriteLine($"[workspace] {w.Name,-16} {w.Ready}/{w.Total} up" + (w.NotReady.Count > 0 ? $" · waiting: {string.Join(", ", w.NotReady)}" : ""));
        return Ok;
    }

    // ---------------------------------------------------------------- who

    public sealed record HolderJson(int Port, int Pid, string Name, string Address, string? CommandLine, string? WorkingDirectory, string? Protected, string? Restriction);

    private static int Who(string? portText, TextWriter output, TextWriter error, bool json)
    {
        if (!int.TryParse(portText?.TrimStart(':'), out int port) || port is < 1 or > 65535)
        {
            error.WriteLine("Usage: ldock who <port> [--json]");
            return Usage;
        }
        var holders = PortTable.GetListeners().Where(l => l.Port == port).Select(l =>
        {
            var d = ProcessInfo.Get(l.Pid, fresh: true);
            string? why = d?.Identity is { } id ? ProcessGuard.WhyProtected(id) : ProcessGuard.WhyProtected(l.Pid, d?.Name ?? "?");
            return new HolderJson(port, l.Pid, d?.Name ?? "?", l.Address, Redact(d?.CommandLine), ProcessCwd.TryGet(l.Pid), why, d?.Restriction);
        }).ToList();
        var reserved = ExcludedPorts.Find(port);

        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "localhost-dock/who@1",
                port,
                free = holders.Count == 0 && reserved is null,
                reservedByWindows = reserved is { } r ? $"{r.Start}-{r.End}" : null,
                holders,
            }, Json));
            return Ok;
        }
        if (holders.Count == 0)
            output.WriteLine(reserved is { } r ? $":{port} is free but reserved by Windows ({r.Start}–{r.End}); nothing can bind it." : $":{port} is free.");
        foreach (var h in holders)
            output.WriteLine($":{port} {h.Address} ← {h.Name} (pid {h.Pid}){(h.WorkingDirectory is { } cwd ? " in " + cwd : "")}{(h.Protected is not null ? " [protected]" : "")}\n    {h.CommandLine ?? h.Restriction}");
        return Ok;
    }

    // ---------------------------------------------------------------- manifest

    public sealed record ManifestApp(string Id, string Name, int? Port, IReadOnlyList<int> ExtraPorts, string? Url, string? Start, string? Stop,
        string? Cwd, string? Health, string StopPolicy, int StartTimeoutSec);
    public sealed record ManifestWorkspace(string Id, string Name, IReadOnlyList<string> Apps, IReadOnlyDictionary<string, List<string>> DependsOn,
        IReadOnlyList<IReadOnlyList<string>> StartOrder, string OnFailure, int MaxParallel);
    public sealed record ManifestJson(string Schema, DateTime GeneratedAt, IReadOnlyList<ManifestApp> Apps, IReadOnlyList<ManifestWorkspace> Workspaces);

    public static ManifestJson BuildManifest()
    {
        var reg = Registry.Load(readOnly: true);
        var apps = reg.Apps.Select(a => new ManifestApp(a.Id, a.Name, a.Port, a.ExtraPorts, a.EffectiveUrl, Redact(a.Start), Redact(a.Stop),
            a.Cwd, a.Health, a.StopPolicy, a.StartTimeoutSec)).ToList();
        var workspaces = reg.Workspaces.Select(w => new ManifestWorkspace(w.Id, w.Name, w.Apps, w.DependsOn,
            WorkspacePlan.Levels(w), w.OnFailure, w.MaxParallel)).ToList();
        return new ManifestJson("localhost-dock/manifest@1", DateTime.UtcNow, apps, workspaces);
    }

    private static int Manifest(TextWriter output)
    {
        output.WriteLine(JsonSerializer.Serialize(BuildManifest(), Json));
        return Ok;
    }

    private static int Workspaces(TextWriter output, bool json)
    {
        var m = BuildManifest();
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new { schema = "localhost-dock/workspaces@1", workspaces = m.Workspaces }, Json));
            return Ok;
        }
        var names = m.Apps.ToDictionary(a => a.Id, a => a.Name);
        foreach (var w in m.Workspaces)
        {
            output.WriteLine(w.Name);
            for (int i = 0; i < w.StartOrder.Count; i++)
                output.WriteLine($"  step {i + 1}: {string.Join(", ", w.StartOrder[i].Select(id => names.GetValueOrDefault(id, id)))}");
        }
        return Ok;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Hides values of secret-looking variables/flags (API_KEY=…, --token …, password=…) in command lines.</summary>
    public static string? Redact(string? command) =>
        command is null ? null : SecretFlag().Replace(SecretAssignment().Replace(command, "${k}=***"), "${k} ***");

    [GeneratedRegex(@"(?<k>\b[\w.-]*(?:key|token|secret|password|passwd|pwd|credential)s?[\w.-]*)=(?:""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?<k>--?[\w-]*(?:key|token|secret|password|passwd|pwd)[\w-]*)\s+(?!-)(?:""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretFlag();

    public static string Version() =>
        typeof(CliRunner).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?";

    private static void Help(TextWriter w) => w.WriteLine($"""
        ldock {Version()}: read-only status for Localhost Dock (never starts, stops or changes anything)

          ldock status [--json] [--no-health]   every app: state, ownership, processes, health, uptime
          ldock who <port> [--json]             what's listening on a port (or whether Windows reserves it)
          ldock workspaces [--json]             workspaces and their start order
          ldock manifest                        versioned JSON manifest of apps and workspaces (secrets redacted)
          ldock version

        Exit codes: 0 ok, 1 error, 2 usage. JSON schemas: docs/CLI.md
        """);
}
