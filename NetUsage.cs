using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace GameRamCap;

/// <summary>Network traffic per process (bytes/s, upload + download) from the Windows kernel network ETW provider
/// (Microsoft-Windows-Kernel-Network: TCP and UDP, IPv4 and IPv6). Needs administrator rights, which the app already has.
/// The trace only runs while the window is open: it starts on the first Read() and stops in Close().</summary>
internal static class NetUsage
{
    const string SessionName = "GameRamCapNet";
    static readonly Guid Provider = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern uint StartTraceW(out ulong handle, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern uint ControlTraceW(ulong handle, string? name, IntPtr props, uint code);
    [DllImport("advapi32.dll")]
    static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern ulong OpenTraceW(ref LOGFILE logfile);
    [DllImport("advapi32.dll")]
    static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")]
    static extern uint CloseTrace(ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void EventCb(IntPtr eventRecord);

    [StructLayout(LayoutKind.Sequential)]
    struct LOGFILE   // EVENT_TRACE_LOGFILEW (x64)
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 88)] public byte[] CurrentEvent;      // EVENT_TRACE
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 280)] public byte[] LogfileHeader;    // TRACE_LOGFILE_HEADER
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    static readonly object Lock = new();
    static readonly ConcurrentDictionary<int, long> Totals = new();
    static readonly EventCb Callback = OnEvent;   // kept alive for the whole run
    static ulong _session, _trace;
    static Thread? _thread;
    static bool _failed;
    static Dictionary<int, long> _prev = new();
    static DateTime _prevAt = DateTime.MinValue;
    static Dictionary<int, double>? _cache;

    static void OnEvent(IntPtr rec)
    {
        try
        {
            ushort id = (ushort)Marshal.ReadInt16(rec, 40);
            // 10/11 TCP v4 send/recv, 26/27 TCP v6, 42/43 UDP v4, 58/59 UDP v6. Payload starts with PID, then size (both uint32).
            if (id != 10 && id != 11 && id != 26 && id != 27 && id != 42 && id != 43 && id != 58 && id != 59) return;
            if (Marshal.ReadInt16(rec, 86) < 8) return;
            IntPtr data = Marshal.ReadIntPtr(rec, 96);
            if (data == IntPtr.Zero) return;
            int pid = Marshal.ReadInt32(data, 0);
            uint size = (uint)Marshal.ReadInt32(data, 4);
            Totals.AddOrUpdate(pid, size, (_, v) => v + size);
        }
        catch { }
    }

    static IntPtr NewProps(out int total)
    {
        int nameBytes = (SessionName.Length + 1) * 2;
        total = 128 + nameBytes + 8;
        var p = Marshal.AllocHGlobal(total);
        for (int i = 0; i < total; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, total);              // WNODE_HEADER.BufferSize
        Marshal.WriteInt32(p, 40, 1);                 // ClientContext = QPC
        Marshal.WriteInt32(p, 44, 0x20000);           // WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 64, 0x100);             // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 120, 0);                // LogFileNameOffset (none)
        Marshal.WriteInt32(p, 124, 128);              // LoggerNameOffset
        return p;
    }

    static void StopByName()
    {
        var p = NewProps(out _);
        try { ControlTraceW(0, SessionName, p, 1); } catch { }   // EVENT_TRACE_CONTROL_STOP
        Marshal.FreeHGlobal(p);
    }

    static bool Start()
    {
        if (IntPtr.Size != 8) return false;
        try
        {
            StopByName();   // a session left behind by a crashed run
            var p = NewProps(out _);
            uint rc;
            try { rc = StartTraceW(out _session, SessionName, p); }
            finally { Marshal.FreeHGlobal(p); }
            if (rc != 0) { Logger.Write($"Net column: could not start the network trace (error {rc}). Run GameRamCap as administrator."); return false; }

            var g = Provider;
            rc = EnableTraceEx2(_session, ref g, 1, 4, 0x30, 0, 0, IntPtr.Zero);   // enable, IPv4 + IPv6 keywords
            if (rc != 0) { Logger.Write($"Net column: could not enable the network provider (error {rc})."); StopByName(); return false; }

            var lf = new LOGFILE
            {
                LoggerName = Marshal.StringToHGlobalUni(SessionName),
                ProcessTraceMode = 0x100 | 0x10000000,   // real time + event record callback
                CurrentEvent = new byte[88],
                LogfileHeader = new byte[280],
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(Callback),
            };
            _trace = OpenTraceW(ref lf);
            if (_trace == 0 || _trace == ulong.MaxValue) { Logger.Write("Net column: could not open the network trace."); StopByName(); return false; }
            ulong t = _trace;
            _thread = new Thread(() => { try { ProcessTrace(new[] { t }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } })
            { IsBackground = true, Name = "GameRamCap net trace" };
            _thread.Start();
            return true;
        }
        catch (Exception ex) { Logger.Write("Net column: " + ex.Message); try { StopByName(); } catch { } return false; }
    }

    /// <summary>pid -> bytes per second (up + down). null until two samples exist, or when the trace is not available.</summary>
    public static Dictionary<int, double>? Read()
    {
        lock (Lock)
        {
            if (_failed) return null;
            var now = DateTime.UtcNow;
            if (_thread == null)
            {
                if (!Start()) { _failed = true; return null; }
                _prev = Totals.ToDictionary(k => k.Key, k => k.Value);
                _prevAt = now;
                return null;
            }
            double secs = (now - _prevAt).TotalSeconds;
            if (secs < 0.9) return _cache;   // a quick manual Refresh must not give noisy numbers
            var cur = Totals.ToDictionary(k => k.Key, k => k.Value);
            var res = new Dictionary<int, double>();
            foreach (var kv in cur)
            {
                long d = kv.Value - _prev.GetValueOrDefault(kv.Key);
                if (d > 0) res[kv.Key] = d / secs;
            }
            _prev = cur;
            _prevAt = now;
            return _cache = res;
        }
    }

    public static void Close()
    {
        lock (Lock)
        {
            if (_thread == null && !_failed) return;
            try { if (_trace != 0) CloseTrace(_trace); } catch { }
            try { StopByName(); } catch { }
            _thread = null; _trace = 0; _session = 0; _failed = false;
            _cache = null; _prev = new(); _prevAt = DateTime.MinValue;
            Totals.Clear();
        }
    }
}
