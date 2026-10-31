using System.Text.RegularExpressions;
using LocalhostDock.Net;

namespace LocalhostDock.Core;

/// <summary>Helps resolve port conflicts: suggests free ports and rewrites explicit port flags in start commands.</summary>
public static partial class PortAdvisor
{
    /// <summary>
    /// A port near <paramref name="near"/> that nothing listens on, Windows doesn't reserve, no registered app uses,
    /// and that isn't privileged (&lt; 1024) or ephemeral (≥ 49152). Searches upwards first.
    /// </summary>
    public static int? SuggestFree(int near, IEnumerable<int> registeredPorts, Func<int, bool>? isListening = null, Func<int, bool>? isReserved = null)
    {
        var taken = registeredPorts.ToHashSet();
        var listening = isListening is null ? PortTable.GetListeners().Select(l => l.Port).ToHashSet() : null;
        isListening ??= p => listening!.Contains(p);
        isReserved ??= p => ExcludedPorts.Find(p) is not null;

        foreach (int candidate in Candidates(near))
        {
            if (candidate is < 1024 or >= 49152) continue;
            if (taken.Contains(candidate) || isListening(candidate) || isReserved(candidate)) continue;
            return candidate;
        }
        return null;
    }

    private static IEnumerable<int> Candidates(int near)
    {
        for (int i = 1; i <= 200; i++) yield return near + i;
        for (int i = 1; i <= 200; i++) yield return near - i;
    }

    /// <summary>
    /// Replaces an explicit port in a command line ("-p 3000", "--port 3000", "--port=3000", "PORT=3000",
    /// "localhost:3000"). Returns null when the command has no such flag (e.g. it's a launcher file whose port is
    /// set inside the script; Localhost Dock never edits your scripts).
    /// </summary>
    public static string? RewriteStartPort(string? start, int oldPort, int newPort)
    {
        if (string.IsNullOrWhiteSpace(start) || File.Exists(start.Trim().Trim('"'))) return null;
        string old = oldPort.ToString();
        var re = new Regex($@"(?<pre>(?:^|\s)(?:-p|--port|-Port)[=\s]+|\bPORT=|(?:localhost|127\.0\.0\.1|0\.0\.0\.0):){old}\b", RegexOptions.IgnoreCase);
        if (!re.IsMatch(start)) return null;
        return re.Replace(start, m => m.Groups["pre"].Value + newPort);
    }
}
