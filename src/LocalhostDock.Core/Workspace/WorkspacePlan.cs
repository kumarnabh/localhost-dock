using LocalhostDock.Config;

namespace LocalhostDock.Workspace;

/// <summary>Graph helpers for a workspace's dependencies (Kahn's algorithm; member order breaks ties).</summary>
public static class WorkspacePlan
{
    /// <summary>
    /// Start levels: level 0 has no dependencies, level n depends only on earlier levels. Members inside a level
    /// can start in parallel. Throws if there's a cycle (normalization removes cycles on load).
    /// </summary>
    public static List<List<string>> Levels(WorkspaceEntry w)
    {
        var remaining = new HashSet<string>(w.Apps);
        var done = new HashSet<string>();
        var levels = new List<List<string>>();
        while (remaining.Count > 0)
        {
            var level = w.Apps.Where(id => remaining.Contains(id) && w.DependenciesOf(id).All(d => done.Contains(d) || !w.Apps.Contains(d))).ToList();
            if (level.Count == 0) throw new InvalidOperationException($"Workspace “{w.Name}” has a dependency loop.");
            levels.Add(level);
            foreach (var id in level) { remaining.Remove(id); done.Add(id); }
        }
        return levels;
    }

    /// <summary>Stop order: dependents before what they depend on (reverse of the start levels).</summary>
    public static List<string> StopOrder(WorkspaceEntry w) =>
        Levels(w).AsEnumerable().Reverse().SelectMany(level => level.AsEnumerable().Reverse()).ToList();

    /// <summary>The members involved in dependency loops, or null if there are none.</summary>
    public static List<string>? FindCycle(WorkspaceEntry w)
    {
        var remaining = new HashSet<string>(w.Apps);
        bool progress = true;
        while (progress && remaining.Count > 0)
        {
            progress = false;
            foreach (var id in remaining.ToList())
            {
                if (w.DependenciesOf(id).Any(remaining.Contains)) continue;
                remaining.Remove(id);
                progress = true;
            }
        }
        return remaining.Count == 0 ? null : w.Apps.Where(remaining.Contains).ToList();
    }

    /// <summary>Everything that (transitively) depends on <paramref name="appId"/>.</summary>
    public static HashSet<string> DependentsOf(WorkspaceEntry w, string appId)
    {
        var result = new HashSet<string>();
        var queue = new Queue<string>(new[] { appId });
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var id in w.Apps.Where(a => w.DependenciesOf(a).Contains(current) && result.Add(a))) queue.Enqueue(id);
        }
        return result;
    }

    /// <summary>
    /// A workspace made of <paramref name="appIds"/> that keeps the dependencies the user's workspaces already
    /// declare between them (used to restore a session in a sensible order).
    /// </summary>
    public static WorkspaceEntry Combine(string name, IReadOnlyList<string> appIds, IEnumerable<WorkspaceEntry> workspaces)
    {
        var ws = new WorkspaceEntry { Id = "session", Name = name, Apps = appIds.ToList(), OnFailure = "continue" };
        foreach (var w in workspaces)
            foreach (var (id, deps) in w.DependsOn.Where(kv => appIds.Contains(kv.Key)))
            {
                if (!ws.DependsOn.TryGetValue(id, out var list)) ws.DependsOn[id] = list = new();
                foreach (var d in deps.Where(d => appIds.Contains(d) && !list.Contains(d))) list.Add(d);
            }
        if (FindCycle(ws) is { } cycle) foreach (var id in cycle) ws.DependsOn.Remove(id);
        return ws;
    }
}
