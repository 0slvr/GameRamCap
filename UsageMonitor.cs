using System.Diagnostics;

namespace GameRamCap;

/// <summary>Watches RAM and CPU of every app (one point per 5 s, the last 60 minutes are kept in memory).
/// Feeds the History window and the alerts (big RAM / possible memory leak). History is NOT saved to disk: it starts empty each time GameRamCap starts.</summary>
internal static class UsageMonitor
{
    public sealed record Point(DateTime At, long Ram, double Cpu);
    public sealed record Summary(string Name, long Ram, long Peak, long Delta10, double CpuAvg, double CpuPeak, int Minutes);

    const int MaxPoints = 720;               // 60 min at one point per 5 s
    const double IntervalSeconds = 5;
    static readonly object Lock = new();
    static readonly Dictionary<string, List<Point>> History = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<int, long> _prevCpu = new();
    static DateTime _prevAt = DateTime.MinValue;

    /// <summary>Called from the engine tick; does the work at most once every 5 seconds.</summary>
    public static void Update(Config cfg)
    {
        var now = DateTime.UtcNow;
        bool rates = _prevAt != DateTime.MinValue;
        double secs = (now - _prevAt).TotalSeconds;
        if (rates && secs < IntervalSeconds) return;

        double cores = Math.Max(1, Environment.ProcessorCount);
        var curCpu = new Dictionary<int, long>();
        var ram = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var cpuD = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string name;
                try { name = p.ProcessName; } catch { continue; }
                long ws = 0;
                try { ws = p.WorkingSet64; } catch { }
                ram[name] = ram.GetValueOrDefault(name) + ws;
                if (Native.GetCpuIo(p.Id, out var cpu, out _))
                {
                    curCpu[p.Id] = cpu;
                    if (rates && _prevCpu.TryGetValue(p.Id, out var pc))
                    {
                        long d = Math.Max(0, cpu - pc);
                        cpuD[name] = cpuD.GetValueOrDefault(name) + d;
                    }
                }
            }
            finally { p.Dispose(); }
        }

        lock (Lock)
        {
            foreach (var kv in ram)
            {
                double cpuPct = rates ? Math.Min(100.0, cpuD.GetValueOrDefault(kv.Key) / (secs * 1e7) / cores * 100.0) : 0;
                if (!History.TryGetValue(kv.Key, out var list)) History[kv.Key] = list = new List<Point>();
                list.Add(new Point(now, kv.Value, cpuPct));
                if (list.Count > MaxPoints) list.RemoveRange(0, list.Count - MaxPoints);
            }
            // forget apps that have been gone for 10 minutes
            foreach (var key in History.Keys.ToList())
            {
                var l = History[key];
                if (!ram.ContainsKey(key) && l.Count > 0 && (now - l[^1].At).TotalMinutes > 10) History.Remove(key);
            }
        }
        _prevCpu = curCpu;
        _prevAt = now;
    }

    /// <summary>One line per app for the History window.</summary>
    public static List<Summary> Snapshot()
    {
        var result = new List<Summary>();
        lock (Lock)
        {
            foreach (var kv in History)
            {
                var pts = kv.Value;
                if (pts.Count == 0) continue;
                int back = Math.Min(pts.Count - 1, 120);   // 10 minutes ago, or the oldest point we have
                long peak = 0; double sum = 0, cpuPeak = 0;
                foreach (var pt in pts) { peak = Math.Max(peak, pt.Ram); sum += pt.Cpu; cpuPeak = Math.Max(cpuPeak, pt.Cpu); }
                int minutes = (int)Math.Round((pts[^1].At - pts[0].At).TotalMinutes);
                result.Add(new Summary(kv.Key, pts[^1].Ram, peak, pts[^1].Ram - pts[pts.Count - 1 - back].Ram, sum / pts.Count, cpuPeak, minutes));
            }
        }
        return result;
    }
}
