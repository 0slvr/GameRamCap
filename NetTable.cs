using System.Runtime.InteropServices;

namespace GameRamCap;

/// <summary>Open network connections (IPv4 TCP and UDP) and which process owns each one.
/// This shows WHO is connected to WHERE; Windows gives no per-process traffic speed without ETW tracing.</summary>
internal static class NetTable
{
    public sealed record Conn(string Proto, string Local, string Remote, string State, int Pid);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;
    const uint ERROR_INSUFFICIENT_BUFFER = 122;

    static readonly string[] States =
    {
        "", "Closed", "Listening", "SYN sent", "SYN received", "Established", "FIN wait 1", "FIN wait 2",
        "Close wait", "Closing", "Last ACK", "Time wait", "Deleted",
    };

    static string Ip(int a) => new System.Net.IPAddress((long)(uint)a).ToString();
    static int Port(int p) => ((p & 0xFF) << 8) | ((p >> 8) & 0xFF);   // stored in network byte order

    /// <summary>Allocates and fills a table. The caller frees the pointer. Returns IntPtr.Zero on failure.</summary>
    static IntPtr Load(bool tcp)
    {
        int size = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            uint rc = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0)
                          : GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (rc != ERROR_INSUFFICIENT_BUFFER && rc != 0) return IntPtr.Zero;
            size += 4096;   // the table can grow between the two calls
            var buf = Marshal.AllocHGlobal(size);
            rc = tcp ? GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0)
                     : GetExtendedUdpTable(buf, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (rc == 0) return buf;
            Marshal.FreeHGlobal(buf);
            if (rc != ERROR_INSUFFICIENT_BUFFER) return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>All connections owned by one of the given process ids.</summary>
    public static List<Conn> For(HashSet<int> pids)
    {
        var list = new List<Conn>();
        var tcp = Load(true);
        if (tcp != IntPtr.Zero)
        {
            try
            {
                int n = Marshal.ReadInt32(tcp);
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 24;   // state, localAddr, localPort, remoteAddr, remotePort, owningPid
                    int pid = Marshal.ReadInt32(tcp, o + 20);
                    if (!pids.Contains(pid)) continue;
                    int st = Marshal.ReadInt32(tcp, o);
                    bool listening = st == 2;
                    list.Add(new Conn("TCP",
                        Ip(Marshal.ReadInt32(tcp, o + 4)) + ":" + Port(Marshal.ReadInt32(tcp, o + 8)),
                        listening ? "" : Ip(Marshal.ReadInt32(tcp, o + 12)) + ":" + Port(Marshal.ReadInt32(tcp, o + 16)),
                        st >= 0 && st < States.Length ? States[st] : st.ToString(), pid));
                }
            }
            finally { Marshal.FreeHGlobal(tcp); }
        }
        var udp = Load(false);
        if (udp != IntPtr.Zero)
        {
            try
            {
                int n = Marshal.ReadInt32(udp);
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 12;   // localAddr, localPort, owningPid
                    int pid = Marshal.ReadInt32(udp, o + 8);
                    if (!pids.Contains(pid)) continue;
                    list.Add(new Conn("UDP", Ip(Marshal.ReadInt32(udp, o)) + ":" + Port(Marshal.ReadInt32(udp, o + 4)), "", "", pid));
                }
            }
            finally { Marshal.FreeHGlobal(udp); }
        }
        return list;
    }
}
