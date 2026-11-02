using LocalhostDock.Config;

namespace LocalhostDock.Workspace;

/// <summary>Explicit local preset import. Adds missing entries; never overwrites existing project configuration.</summary>
public static class WorkspacePresets
{
    public static Registry Read(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidOperationException("The preset file is too large (maximum 1 MB).");
        var source = Registry.Parse(File.ReadAllText(path));
        if (source.SchemaVersion > Registry.CurrentSchema) throw new InvalidOperationException("This preset requires a newer Dock.");
        var warnings = source.Normalize();
        if (warnings.Count > 0) throw new InvalidOperationException("Fix this preset before importing: " + string.Join(" ", warnings));
        if (source.Workspaces.Count == 0) throw new InvalidOperationException("The preset has no workspaces.");
        return source;
    }

    public static int Import(Registry target, Registry source, IEnumerable<string> workspaceIds)
    {
        int count = 0;
        foreach (var workspace in source.Workspaces.Where(w => workspaceIds.Contains(w.Id)))
        {
            if (target.Workspaces.Any(w => w.Name.Equals(workspace.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var map = new Dictionary<string, string>();
            foreach (var id in workspace.Apps)
            {
                var candidate = source.ById(id) ?? throw new InvalidOperationException("A preset member is missing.");
                var existing = target.Apps.FirstOrDefault(a => a.Port == candidate.Port &&
                    string.Equals(a.Start, candidate.Start, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(a.Cwd, candidate.Cwd, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) { map[id] = existing.Id; continue; }
                var copy = candidate.Clone();
                copy.Id = Registry.NewId();
                // Never import arbitrary stop scripts. Dock's ownership-checked stop handles these presets.
                copy.Stop = null;
                copy.StartUnverified = true;
                target.Add(copy);
                map[id] = copy.Id;
            }
            var added = workspace.Clone();
            added.Id = Registry.NewId();
            added.Apps = workspace.Apps.Select(id => map[id]).ToList();
            added.DependsOn = workspace.DependsOn.ToDictionary(k => map[k.Key], v => v.Value.Select(id => map[id]).ToList());
            target.Workspaces.Add(added);
            count++;
        }
        return count;
    }
}
