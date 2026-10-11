using System.Net;
using System.Runtime.InteropServices;

namespace LocalhostDock.Net;

/// <summary>A TCP socket in the LISTEN state and the process that owns it.</summary>
public sealed record Listener(int Port, int Pid, string Address);

/// <summary>Reads the OS TCP listener table (IPv4 + IPv6) via GetExtendedTcpTable.</summary>
public static class PortTable
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    public static List<Listener> GetListeners()
    {
        var result = new List<Listener>();
        Read(AF_INET, result);
        Read(AF_INET6, result);
        return result
            .GroupBy(l => (l.Port, l.Pid))
            .Select(g => g.First())
            .OrderBy(l => l.Port)
            .ToList();
    }

    private static void Read(int family, List<Listener> into)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, family, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                return;

            int count = Marshal.ReadInt32(buffer);
            IntPtr row = buffer + 4;

            if (family == AF_INET)
            {
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                const int rowSize = 24;
                for (int i = 0; i < count; i++, row += rowSize)
                {
                    uint addr = (uint)Marshal.ReadInt32(row, 4);
                    int port = NetworkPort(Marshal.ReadInt32(row, 8));
                    int pid = Marshal.ReadInt32(row, 20);
                    into.Add(new Listener(port, pid, new IPAddress(addr).ToString()));
                }
            }
            else
            {
                // MIB_TCP6ROW_OWNER_PID: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid
                const int rowSize = 56;
                for (int i = 0; i < count; i++, row += rowSize)
                {
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, 16);
                    int port = NetworkPort(Marshal.ReadInt32(row, 20));
                    int pid = Marshal.ReadInt32(row, 52);
                    into.Add(new Listener(port, pid, "[" + new IPAddress(bytes) + "]"));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int NetworkPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
}
