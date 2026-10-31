using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalhostDock.Net;

/// <summary>
/// Reads another process's current working directory (from its PEB). Used as ownership evidence:
/// "npm run dev" in C:\dev\web leaves a node.exe whose command line may not mention C:\dev\web, but whose
/// working directory does. Read-only; returns null whenever Windows doesn't allow it.
/// </summary>
public static class ProcessCwd
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle h, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(SafeProcessHandle h, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

    public static string? TryGet(int pid)
    {
        if (!Environment.Is64BitProcess) return null; // offsets below are for 64-bit PEBs
        try
        {
            using var h = Native.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h.IsInvalid) return null;

            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

            // PEB.ProcessParameters at +0x20; RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath at +0x38.
            IntPtr parameters = ReadPointer(h, pbi.PebBaseAddress + 0x20);
            if (parameters == IntPtr.Zero) return null;
            var us = Read(h, parameters + 0x38, 16);
            if (us is null) return null;
            int length = BitConverter.ToUInt16(us, 0);
            IntPtr buffer = (IntPtr)BitConverter.ToInt64(us, 8);
            if (length is 0 or > 32767 * 2 || buffer == IntPtr.Zero) return null;
            var chars = Read(h, buffer, length);
            return chars is null ? null : Encoding.Unicode.GetString(chars).TrimEnd('\\');
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr ReadPointer(SafeProcessHandle h, IntPtr address) =>
        Read(h, address, 8) is { } b ? (IntPtr)BitConverter.ToInt64(b, 0) : IntPtr.Zero;

    private static byte[]? Read(SafeProcessHandle h, IntPtr address, int size)
    {
        var buffer = new byte[size];
        return ReadProcessMemory(h, address, buffer, size, out var read) && (int)read == size ? buffer : null;
    }
}
