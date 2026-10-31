using System.Text;

namespace LocalhostDock.Net;

/// <summary>
/// Who a process *is*, not just its number: PID + creation time + image path. Windows reuses PIDs, so a PID on
/// its own can point at a different program a minute later. Every kill revalidates against a captured identity.
/// </summary>
public sealed record ProcessIdentity(int Pid, long CreationTicks, string? ImagePath, string Name)
{
    public DateTime CreatedUtc => DateTime.FromFileTimeUtc(CreationTicks);

    /// <summary>Reads a live process's identity, or null if it has exited or can't be opened at all.</summary>
    public static ProcessIdentity? Capture(int pid)
    {
        if (pid <= 0) return null;
        using var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return null;
        if (!Native.GetProcessTimes(h, out long created, out _, out _, out _)) return null;
        // A process that has exited but whose handle is still held somewhere has a non-STILL_ACTIVE exit code.
        if (Native.GetExitCodeProcess(h, out uint code) && code != 259 /* STILL_ACTIVE */) return null;

        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        string? image = Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        string name = image is not null ? Path.GetFileName(image) : ProcessTree.NameOf(pid) ?? "?";
        return new ProcessIdentity(pid, created, image, name);
    }

    /// <summary>True if the same process (not a recycled PID) is still running.</summary>
    public bool IsAlive() => Capture(Pid) is { } now && SameAs(now);

    public bool SameAs(ProcessIdentity other) =>
        other.Pid == Pid && other.CreationTicks == CreationTicks &&
        (ImagePath is null || other.ImagePath is null || string.Equals(ImagePath, other.ImagePath, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Name} (pid {Pid})";
}

/// <summary>A point-in-time view of parent/child relationships, safe against PID reuse.</summary>
public static class ProcessTree
{
    /// <summary>pid → parent pid for every process, from a Toolhelp snapshot.</summary>
    public static Dictionary<int, (int Parent, string Exe)> Snapshot()
    {
        var map = new Dictionary<int, (int, string)>();
        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new Native.PROCESSENTRY32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.PROCESSENTRY32>() };
            if (!Native.Process32FirstW(snap, ref e)) return map;
            do map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile);
            while (Native.Process32NextW(snap, ref e));
        }
        finally { Native.CloseHandle(snap); }
        return map;
    }

    public static string? NameOf(int pid) => Snapshot().TryGetValue(pid, out var e) ? e.Exe : null;

    /// <summary>
    /// The root plus all descendants, children before parents (kill order). A child only counts if it was
    /// created after its parent, so a recycled parent PID can't adopt unrelated processes.
    /// </summary>
    public static List<ProcessIdentity> Descendants(ProcessIdentity root, Dictionary<int, (int Parent, string Exe)>? snapshot = null)
    {
        snapshot ??= Snapshot();
        var children = snapshot.GroupBy(kv => kv.Value.Parent).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        var result = new List<ProcessIdentity>();
        var visited = new HashSet<int>();

        void Walk(ProcessIdentity parent, int depth)
        {
            if (depth > 32 || !visited.Add(parent.Pid)) return;
            if (children.TryGetValue(parent.Pid, out var kids))
                foreach (int kid in kids)
                {
                    if (kid == parent.Pid) continue;
                    var id = ProcessIdentity.Capture(kid);
                    if (id is null || id.CreationTicks < parent.CreationTicks) continue; // PID-reuse guard
                    Walk(id, depth + 1);
                }
            result.Add(parent);
        }

        Walk(root, 0);
        return result;
    }

    /// <summary>True if <paramref name="candidate"/> is <paramref name="ancestor"/> or descends from it (verified by creation order).</summary>
    public static bool IsDescendantOf(ProcessIdentity candidate, ProcessIdentity ancestor, Dictionary<int, (int Parent, string Exe)>? snapshot = null)
    {
        if (candidate.SameAs(ancestor)) return true;
        snapshot ??= Snapshot();
        var current = candidate;
        for (int i = 0; i < 32; i++)
        {
            if (!snapshot.TryGetValue(current.Pid, out var e) || e.Parent <= 0 || e.Parent == current.Pid) return false;
            var parent = ProcessIdentity.Capture(e.Parent);
            if (parent is null || parent.CreationTicks > current.CreationTicks) return false; // parent exited / PID reused
            if (parent.SameAs(ancestor)) return true;
            current = parent;
        }
        return false;
    }

    private static readonly Lazy<HashSet<int>> SelfAndAncestorsCache = new(ComputeSelfAndAncestors);

    /// <summary>This process and its ancestors at startup (never to be killed: e.g. the shell that launched the dock).</summary>
    public static HashSet<int> SelfAndAncestors() => SelfAndAncestorsCache.Value;

    private static HashSet<int> ComputeSelfAndAncestors()
    {
        var set = new HashSet<int> { Environment.ProcessId };
        var snap = Snapshot();
        int pid = Environment.ProcessId;
        for (int i = 0; i < 32 && snap.TryGetValue(pid, out var e) && e.Parent > 0 && set.Add(e.Parent); i++) pid = e.Parent;
        return set;
    }
}
