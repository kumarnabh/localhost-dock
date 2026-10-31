using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LocalhostDock.Net;

/// <summary>
/// TCP port ranges Windows reserves (Hyper-V, WSL, Docker…). Nothing can listen on them — binding fails with
/// EACCES — which is otherwise very confusing when "nothing is on the port" yet your dev server won't start.
/// </summary>
public static partial class ExcludedPorts
{
    private static (DateTime At, List<(int Start, int End)> Ranges)? _cache;

    public static (int Start, int End)? Find(int port) =>
        Get().Cast<(int Start, int End)?>().FirstOrDefault(r => port >= r!.Value.Start && port <= r.Value.End);

    public static List<(int Start, int End)> Get()
    {
        if (_cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.Ranges;
        var ranges = Parse(Netsh("ipv4")).Concat(Parse(Netsh("ipv6"))).Distinct().ToList();
        _cache = (DateTime.UtcNow, ranges);
        return ranges;
    }

    public static IEnumerable<(int Start, int End)> Parse(string netshOutput)
    {
        foreach (Match m in RangeLine().Matches(netshOutput))
            yield return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    private static string Netsh(string family)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", $"int {family} show excludedportrange protocol=tcp")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch
        {
            return "";
        }
    }

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex RangeLine();
}
