using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LocalhostDock.Net;
using Microsoft.Win32.SafeHandles;

namespace LocalhostDock.Core;

/// <summary>A process the dock started, pinned by identity so it can be verified and tracked later.</summary>
public sealed class LaunchedProcess : IDisposable
{
    private readonly SafeProcessHandle _handle;

    public ProcessIdentity Identity { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    internal LaunchedProcess(ProcessIdentity identity, SafeProcessHandle handle)
    {
        Identity = identity;
        _handle = handle;
    }

    /// <summary>Exit code once the launcher process has exited; null while it runs (or if unknown).</summary>
    public int? ExitCode =>
        !_handle.IsInvalid && !_handle.IsClosed && Native.GetExitCodeProcess(_handle, out uint code) && code != 259 ? unchecked((int)code) : null;

    public bool HasExited => ExitCode is not null;

    public void Dispose() => _handle.Dispose();
}

/// <summary>Builds command lines and starts processes, with or without capturing output to a log file.</summary>
public static class Launcher
{
    private const long RotateAtBytes = 5 * 1024 * 1024;
    private const int KeepRotated = 3;

    /// <summary>
    /// The executable and argument string for a start/stop command. A bare path to an existing .cmd/.bat/.ps1/.exe
    /// runs as that file (so existing launchers behave exactly as when double-clicked); anything else runs through
    /// cmd.exe. Uses cmd's /s rule (strip only the outermost quotes) so quoting is predictable.
    /// </summary>
    public static (string File, string Args, string? DefaultCwd) Build(string command, string? cwd = null)
    {
        string trimmed = command.Trim();
        string unquoted = trimmed.Trim('"');
        if (!Path.IsPathFullyQualified(unquoted) && !string.IsNullOrWhiteSpace(cwd) && File.Exists(Path.Combine(cwd, unquoted)))
            unquoted = Path.Combine(cwd, unquoted);
        string cmd = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe";

        if (File.Exists(unquoted))
        {
            string full = Path.GetFullPath(unquoted);
            string? dir = Path.GetDirectoryName(full);
            return Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".ps1" => ("powershell.exe", $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{full}\"", dir),
                ".exe" => (full, "", dir),
                _ => (cmd, $"/d /s /c \"\"{full}\"\"", dir),
            };
        }
        string? entry = null;
        string tail = "";
        int end = trimmed.StartsWith('"') ? trimmed.IndexOf('"', 1) : trimmed.IndexOf(' ');
        if (end > 0)
        {
            entry = trimmed.StartsWith('"') ? trimmed[1..end] : trimmed[..end];
            tail = trimmed[(end + 1)..].TrimStart();
            if (!Path.IsPathFullyQualified(entry) && cwd is not null) entry = Path.Combine(cwd, entry);
        }
        if (entry is not null && File.Exists(entry))
        {
            string full = Path.GetFullPath(entry);
            string? dir = Path.GetDirectoryName(full);
            return Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".ps1" => ("powershell.exe", $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{full}\" {tail}", dir),
                ".exe" => (full, tail, dir),
                _ => (cmd, $"/d /s /c \"\"{full}\" {tail}\"", dir),
            };
        }
        return (cmd, $"/d /s /c \"{trimmed}\"", null);
    }

    /// <summary>Starts a command in its own console window (minimized/normal/hidden).</summary>
    public static LaunchedProcess? StartWithWindow(string command, string? cwd, string window)
    {
        var (file, args, defaultCwd) = Build(command, cwd);
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = true,
            WorkingDirectory = ResolveCwd(cwd, defaultCwd),
            WindowStyle = window.ToLowerInvariant() switch
            {
                "hidden" => ProcessWindowStyle.Hidden,
                "normal" => ProcessWindowStyle.Normal,
                _ => ProcessWindowStyle.Minimized,
            },
        };
        using var p = Process.Start(psi);
        return p is null ? null : Pin(p);
    }

    /// <summary>
    /// Starts a command with stdout and stderr appended straight to <paramref name="logFile"/> (inherited file
    /// handles, no shell redirection) and stdin from NUL, without a window. The app keeps logging even if the
    /// dock exits, and a launcher's "pause" reads end-of-file instead of hanging.
    /// </summary>
    public static LaunchedProcess StartWithLog(string command, string? cwd, string logFile, string header)
    {
        var (file, args, defaultCwd) = Build(command, cwd);
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        Rotate(logFile);
        File.AppendAllText(logFile, header);

        var sa = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        using var log = Native.CreateFile(logFile, Native.FILE_APPEND_DATA, Native.FILE_SHARE_ALL, ref sa, Native.OPEN_ALWAYS, 0x80, IntPtr.Zero);
        if (log.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Couldn't open the log file {logFile}");
        using var nul = Native.CreateFile("NUL", Native.GENERIC_READ, Native.FILE_SHARE_ALL, ref sa, Native.OPEN_EXISTING, 0, IntPtr.Zero);

        var si = new Native.STARTUPINFO
        {
            cb = Marshal.SizeOf<Native.STARTUPINFO>(),
            dwFlags = Native.STARTF_USESTDHANDLES | Native.STARTF_USESHOWWINDOW,
            wShowWindow = 0, // SW_HIDE
            hStdInput = nul.DangerousGetHandle(),
            hStdOutput = log.DangerousGetHandle(),
            hStdError = log.DangerousGetHandle(),
        };
        var commandLine = new StringBuilder(Quote(file) + (args.Length > 0 ? " " + args : ""));
        if (!Native.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, Native.CREATE_NO_WINDOW,
                IntPtr.Zero, ResolveCwd(cwd, defaultCwd), ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Couldn't start “{command}”");

        try
        {
            // We still hold hProcess, so the PID can't have been reused yet: the identity is exact.
            var identity = ProcessIdentity.Capture(pi.dwProcessId) ?? new ProcessIdentity(pi.dwProcessId, 0, file, Path.GetFileName(file));
            return new LaunchedProcess(identity, new SafeProcessHandle(pi.hProcess, ownsHandle: true));
        }
        finally
        {
            Native.CloseHandle(pi.hThread);
        }
    }

    /// <summary>Runs a stop command and waits for it; returns its exit code (null if it didn't finish in time).</summary>
    public static async Task<int?> RunAndWaitAsync(string command, string? cwd, TimeSpan timeout)
    {
        var (file, args, defaultCwd) = Build(command, cwd);
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = true, WorkingDirectory = ResolveCwd(cwd, defaultCwd), WindowStyle = ProcessWindowStyle.Minimized,
        });
        if (p is null) return null;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode;
        }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>Keeps up to 3 rotated logs (name.1.log … name.3.log) once the current one passes 5 MB.</summary>
    public static void Rotate(string logFile, long limit = RotateAtBytes)
    {
        try
        {
            var info = new FileInfo(logFile);
            if (!info.Exists || info.Length < limit) return;
            string stem = Path.Combine(info.DirectoryName!, Path.GetFileNameWithoutExtension(logFile));
            File.Delete($"{stem}.{KeepRotated}.log");
            for (int i = KeepRotated - 1; i >= 1; i--)
                if (File.Exists($"{stem}.{i}.log")) File.Move($"{stem}.{i}.log", $"{stem}.{i + 1}.log");
            File.Move(logFile, $"{stem}.1.log");
        }
        catch (IOException) { /* still held open by an old run: keep appending */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Takes our own handle to a just-started process. <paramref name="p"/> still holds its handle, so the PID
    /// can't have been reused even if the launcher already exited: the identity is exact.
    /// </summary>
    private static LaunchedProcess? Pin(Process p)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.SYNCHRONIZE, false, p.Id);
        if (handle.IsInvalid || !Native.GetProcessTimes(handle, out long created, out _, out _, out _))
        {
            handle.Dispose();
            return null;
        }
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        string? image = Native.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        return new LaunchedProcess(new ProcessIdentity(p.Id, created, image, image is null ? "?" : Path.GetFileName(image)), handle);
    }

    private static string ResolveCwd(string? cwd, string? defaultCwd)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException($"The working directory “{cwd}” doesn't exist.");
            return cwd;
        }
        return defaultCwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string Quote(string s) => s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;
}
