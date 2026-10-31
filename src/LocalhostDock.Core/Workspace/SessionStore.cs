using System.Text.Json;
using LocalhostDock.Config;
using LocalhostDock.Core;

namespace LocalhostDock.Workspace;

/// <summary>What was running, so it can be offered back after a reboot or crash.</summary>
public sealed record SessionFile(int Version, DateTime SavedUtc, bool CleanExit, List<string> Running);

/// <summary>
/// Remembers which apps were up (session.json next to apps.json). Restoring never stops anything and only
/// offers apps that are down now and have a start command; the user confirms (unless restoreSession is "auto").
/// </summary>
public static class SessionStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string FilePath => Path.Combine(Registry.Folder, "session.json");

    public static SessionFile? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(FilePath), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // a broken session file only means nothing is offered
        }
    }

    public static void Save(IEnumerable<string> runningAppIds, bool cleanExit)
    {
        try
        {
            Directory.CreateDirectory(Registry.Folder);
            var file = new SessionFile(1, DateTime.UtcNow, cleanExit, runningAppIds.Distinct().ToList());
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Apps from the last session that still exist, have a start command and aren't running now.</summary>
    public static List<AppEntry> Candidates(SessionFile? session, IReadOnlyList<AppStatus> now)
    {
        if (session is null) return new();
        return session.Running
            .Select(id => now.FirstOrDefault(s => s.App.Id == id))
            .Where(s => s is not null && s.State == AppState.Down && !string.IsNullOrWhiteSpace(s.App.Start))
            .Select(s => s!.App)
            .ToList();
    }
}
