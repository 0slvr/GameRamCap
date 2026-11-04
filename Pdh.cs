using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameRamCap;

/// <summary>Minimal wrapper over the Windows Performance Data Helper (pdh.dll), so no extra NuGet package is needed.
/// Counter paths are added with the ENGLISH names (PdhAddEnglishCounter), so they work on every Windows language.</summary>
internal sealed class PdhQuery : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double Value; }

    const uint PDH_FMT_DOUBLE = 0x200;
    const uint PDH_MORE_DATA = 0x800007D2;

    IntPtr _query;

    public static PdhQuery? TryOpen()
    {
        try { return PdhOpenQueryW(null, IntPtr.Zero, out var q) == 0 ? new PdhQuery { _query = q } : null; }
        catch { return null; }
    }

    /// <summary>Adds a counter (English path, wildcards allowed). Returns IntPtr.Zero when the counter does not exist.</summary>
    public IntPtr Add(string path)
    {
        try { return PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    public bool Collect() { try { return PdhCollectQueryData(_query) == 0; } catch { return false; } }

    public double? Value(IntPtr counter)
    {
        try
        {
            if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var v) != 0) return null;
            return v.CStatus == 0 || v.CStatus == 1 ? v.Value : null;
        }
        catch { return null; }
    }

    /// <summary>All instances of a wildcard counter: (instance name, value). Empty when nothing is available yet.</summary>
    public List<(string Name, double Value)> Array(IntPtr counter)
    {
        var res = new List<(string, double)>();
        if (IntPtr.Size != 8) return res;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint size = 0, count = 0;
                if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero) != PDH_MORE_DATA) return res;
                var buf = Marshal.AllocHGlobal((int)size + 1024);
                try
                {
                    size += 1024;
                    if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, buf) != 0) continue;   // grew meanwhile: retry
                    for (int i = 0; i < count; i++)
                    {
                        int o = i * 24;   // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name ptr, status (+pad), double
                        string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, o)) ?? "";
                        int status = Marshal.ReadInt32(buf, o + 8);
                        if (status != 0 && status != 1) continue;
                        res.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buf, o + 16))));
                    }
                    return res;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch { }
        return res;
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        try { PdhCloseQuery(_query); } catch { }
        _query = IntPtr.Zero;
    }
}

/// <summary>GPU usage per process from the "GPU Engine" performance counters (the same source Task Manager uses).
/// Per process: the busiest engine type (3D, Copy, VideoDecode...), like Task Manager. Needs two samples to give numbers.</summary>
internal static class GpuUsage
{
    static readonly object Lock = new();
    static readonly Regex Rx = new(@"pid_(\d+)_.*?engtype_(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static PdhQuery? _q;
    static IntPtr _c;
    static bool _failed;
    static DateTime _last = DateTime.MinValue;
    static Dictionary<int, double>? _cache;

    /// <summary>pid -> GPU %. null until the first full sample exists (or when this PC has no GPU Engine counters).</summary>
    public static Dictionary<int, double>? Read()
    {
        lock (Lock)
        {
            if (_failed) return null;
            var now = DateTime.UtcNow;
            if (_q == null)
            {
                _q = PdhQuery.TryOpen();
                _c = _q?.Add(@"\GPU Engine(*)\Utilization Percentage") ?? IntPtr.Zero;
                if (_q == null || _c == IntPtr.Zero) { _q?.Dispose(); _q = null; _failed = true; Logger.Write("GPU column: the GPU Engine counters are not available on this PC."); return null; }
                _q.Collect();
                _last = now;
                return null;
            }
            if ((now - _last).TotalMilliseconds < 900) return _cache;   // a quick manual Refresh must not give noisy numbers
            if (!_q.Collect()) return _cache;
            _last = now;
            var perType = new Dictionary<(int, string), double>();
            foreach (var (name, val) in _q.Array(_c))
            {
                var m = Rx.Match(name);
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out int pid)) continue;
                var key = (pid, m.Groups[2].Value);
                perType[key] = perType.GetValueOrDefault(key) + val;
            }
            var res = new Dictionary<int, double>();
            foreach (var kv in perType)
                res[kv.Key.Item1] = Math.Max(res.GetValueOrDefault(kv.Key.Item1), kv.Value);
            return _cache = res;
        }
    }

    public static void Close()
    {
        lock (Lock) { _q?.Dispose(); _q = null; _cache = null; _failed = false; _last = DateTime.MinValue; }
    }
}
