namespace LocalhostDock.Integration;

/// <summary>Quoted absolute executable only: Windows sign-in starts Dock, never a shell or any project.</summary>
internal static class WindowsStartup
{
    public static string Command(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') || executable.Any(char.IsControl))
            throw new ArgumentException("Startup requires an absolute executable path.", nameof(executable));
        return $"\"{Path.GetFullPath(executable)}\"";
    }

    public static bool Matches(object? value, string executable) => value is string command &&
        string.Equals(command, Command(executable), StringComparison.OrdinalIgnoreCase);
}
