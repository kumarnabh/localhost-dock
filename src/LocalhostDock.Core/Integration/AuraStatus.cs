using System.Text.Json;
using System.Text.Json.Serialization;
using LocalhostDock.Core;

namespace LocalhostDock.Integration;

public sealed class AuraOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; }
}

/// <summary>An allowlist contract: no commands, URLs, paths, logs, environment or raw errors cross this boundary.</summary>
public sealed record AuraService(string Id, string Name, AppState State, bool Ready, int? Port,
    IReadOnlyList<AuraListener> Listeners, double? UptimeSec, long MemoryBytes, bool? Healthy,
    int? HttpStatus, double? LatencyMs, string? Diagnostic, int? ExitCode, bool RecoveryRequired);
public sealed record AuraListener(int Port, int Pid, Ownership Ownership);
public sealed record AuraWorkspace(string Id, string Name, IReadOnlyList<string> Apps,
    IReadOnlyDictionary<string, List<string>> DependsOn, int Ready, int Total);
public sealed record AuraStatus(string Schema, DateTime GeneratedAt, IReadOnlyList<AuraService> Services,
    IReadOnlyList<AuraWorkspace> Workspaces)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static AuraStatus From(Snapshot snapshot) => new("localhost-dock/aura-status@1", snapshot.CapturedAtUtc ?? DateTime.UtcNow,
        snapshot.Apps.Select(s => new AuraService(s.App.Id, s.App.Name, s.State, s.Ready, s.App.Port,
            s.Listeners.Select(l => new AuraListener(l.Listener.Port, l.Listener.Pid, l.Ownership)).ToArray(),
            s.UpSince is { } since ? Math.Max(0, (DateTime.Now - since).TotalSeconds) : null,
            s.MemoryBytes, s.Health?.Healthy, s.Health?.Last?.Status, s.Health?.Last?.LatencyMs,
            Diagnostic(s), s.LastExit?.Code, s.State == AppState.Down && s.RecoverableProcesses > 0)).ToArray(),
        snapshot.Workspaces.Select(w => new AuraWorkspace(w.Workspace.Id, w.Workspace.Name,
            w.Workspace.Apps, w.Workspace.DependsOn, w.Up, w.Members.Count)).ToArray());

    private static string? Diagnostic(AppStatus s) => s.State switch
    {
        AppState.Conflict => "port-conflict",
        AppState.Degraded => "health-check-failed",
        AppState.Starting => "waiting-for-readiness",
        AppState.Stopping => "waiting-for-stop",
        AppState.NotConfigured => "port-not-configured",
        AppState.Down when s.RecoverableProcesses > 0 => "surviving-processes-use-dock-stop",
        _ when s.PrimaryOwnership == Ownership.Unverified => "ownership-unverified",
        _ when s.LastError is not null => "operation-failed-see-dock",
        AppState.Down when s.LastExit is { Expected: false } => "process-exited",
        _ => null,
    };
}
