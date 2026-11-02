namespace LocalhostDock.Core;

public static class DockSearch
{
    /// <summary>Every search word must occur in the searchable fields; colons work naturally for ports.</summary>
    public static bool Matches(string query, params string?[] fields) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => fields.Any(field => field?.Contains(word, StringComparison.OrdinalIgnoreCase) == true));
}
