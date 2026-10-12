using System.Runtime.CompilerServices;
using Xunit;

// Several tests point LOCALHOST_DOCK_HOME at temp folders. Run tests one at a time so they can't race on it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LocalhostDock.Tests;

/// <summary>
/// Makes it impossible for the test run to read or write the real %APPDATA%\LocalhostDock registry:
/// before any test runs, LOCALHOST_DOCK_HOME points at a throwaway folder, and tests that use their own
/// folder restore it to this sandbox (never to "unset").
/// </summary>
internal static class TestSandbox
{
    public static readonly string Home = Path.Combine(Path.GetTempPath(), "ldock-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable("LOCALHOST_DOCK_HOME", Home);
    }

    /// <summary>Points the registry at <paramref name="dir"/> until disposed, then back at the sandbox.</summary>
    public static IDisposable Use(string dir)
    {
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("LOCALHOST_DOCK_HOME", dir);
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("LOCALHOST_DOCK_HOME", Home);
    }
}
