using System.Text.RegularExpressions;

namespace LocalhostDock.Net;

/// <summary>Match path boundaries and entry scripts, never arbitrary command-line substrings or options.</summary>
internal static partial class ProcessOwnership
{
    public static bool MatchesProject(string? image, string? command, IEnumerable<string> folders)
    {
        var arguments = Arguments().Matches(command ?? "").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
        string? script = arguments.Length > 1 && Path.GetFileNameWithoutExtension(image)?.ToLowerInvariant() is
            "node" or "python" or "pythonw" or "bun" or "deno" or "ruby" or "php" ? arguments[1] : null;
        return folders.Any(folder => Inside(image, folder) || Inside(script, folder));
    }

    private static bool Inside(string? path, string folder)
    {
        if (path is null || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(folder).TrimEnd('\\', '/');
            if (root.Length < 3) return false;
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    [GeneratedRegex("\"([^\"]*)\"|(\\S+)")]
    private static partial Regex Arguments();
}
