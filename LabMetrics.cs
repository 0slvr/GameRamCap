using System.Diagnostics;
using System.Globalization;

namespace GameRamCap;

internal sealed record LabSys(double AvailMB, double ModifiedMB, double StandbyMB, double CommitMB,
                              double CommitLimitMB, double PagefilePct, double ReadsPerSec, double InputPerSec);

/// <summary>System counters used as success metrics. NOTE: Page Faults/sec is deliberately NOT used (mixes soft + hard).
/// Hard faults = Memory\Page Reads/sec and Memory\Pages Input/sec (system) and ProcRow.HardFaults (per process).</summary>
internal sealed class LabMetrics
{
    readonly PerformanceCounter? _avail, _mod, _sb1, _sb2, _sb3, _commit, _limit, _pf, _reads, _input;
    readonly Dictionary<string, PerformanceCounter> _gpuProc = new();
    public string Missing { get; } = "";

    static PerformanceCounter? C(string cat, string name, string inst, ref string missing)
    {
        try { return string.IsNullOrEmpty(inst) ? new PerformanceCounter(cat, name, true) : new PerformanceCounter(cat, name, inst, true); }
        catch { missing += $"{cat}\\{name}; "; return null; }
    }

    public LabMetrics()
    {
        string m = "";
        _avail = C("Memory", "Available MBytes", "", ref m);
        _mod = C("Memory", "Modified Page List Bytes", "", ref m);
        _sb1 = C("Memory", "Standby Cache Normal Priority Bytes", "", ref m);
        _sb2 = C("Memory", "Standby Cache Reserve Bytes", "", ref m);
        _sb3 = C("Memory", "Standby Cache Core Bytes", "", ref m);
        _commit = C("Memory", "Committed Bytes", "", ref m);
        _limit = C("Memory", "Commit Limit", "", ref m);
        _pf = C("Paging File", "% Usage", "_Total", ref m);
        _reads = C("Memory", "Page Reads/sec", "", ref m);
        _input = C("Memory", "Pages Input/sec", "", ref m);
        Missing = m;
        Read();   // rate counters need a first sample
    }

    static double V(PerformanceCounter? c) { try { return c == null ? -1 : c.NextValue(); } catch { return -1; } }
    static double Mb(double bytes) => bytes < 0 ? -1 : bytes / 1048576.0;

    public LabSys Read()
    {
        double sb = V(_sb1) + V(_sb2) + V(_sb3);
        return new LabSys(V(_avail), Mb(V(_mod)), Mb(sb), Mb(V(_commit)), Mb(V(_limit)), V(_pf), V(_reads), V(_input));
    }

    /// <summary>Dedicated VRAM per process (MB), from the same counters Task Manager uses
    /// ("GPU Process Memory" \ "Dedicated Usage"). Empty when the counters do not exist.</summary>
    public Dictionary<int, double> GpuByPid()
    {
        var res = new Dictionary<int, double>();
        try
        {
            var names = new PerformanceCounterCategory("GPU Process Memory").GetInstanceNames();
            var set = new HashSet<string>(names);
            foreach (var k in _gpuProc.Keys.Where(k => !set.Contains(k)).ToList()) { _gpuProc[k].Dispose(); _gpuProc.Remove(k); }
            foreach (var inst in names)
            {
                if (!inst.StartsWith("pid_", StringComparison.Ordinal)) continue;
                int us = inst.IndexOf('_', 4);
                if (us < 0 || !int.TryParse(inst.AsSpan(4, us - 4), out int pid)) continue;
                if (!_gpuProc.TryGetValue(inst, out PerformanceCounter? c) || c == null)
                {
                    try { c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true); }
                    catch { continue; }
                    _gpuProc[inst] = c;
                }
                double v;
                try { v = c.NextValue(); } catch { continue; }
                res[pid] = res.GetValueOrDefault(pid) + v / 1048576.0;
            }
        }
        catch { }
        return res;
    }

    /// <summary>Total dedicated VRAM in use on all adapters (MB), or -1.</summary>
    public double GpuAdapterMB()
    {
        try
        {
            double sum = 0; bool any = false;
            var cat = new PerformanceCounterCategory("GPU Adapter Memory");
            foreach (var inst in cat.GetInstanceNames())
            {
                using var c = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, true);
                sum += c.NextValue(); any = true;
            }
            return any ? sum / 1048576.0 : -1;
        }
        catch { return -1; }
    }
}

/// <summary>Append-only CSV under %APPDATA%\GameRamCap\lab\ . First columns are always time, run, exp.</summary>
internal sealed class LabCsv
{
    readonly string _path, _header;
    readonly object _gate = new();

    public LabCsv(string file, string columns)
    {
        string dir = Path.Combine(ConfigStore.Dir, "lab");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, file);
        _header = "time,run,exp," + columns;
    }

    public static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    public static string Dir => Path.Combine(ConfigStore.Dir, "lab");

    public void Row(string run, string exp, params object?[] cols)
    {
        try
        {
            lock (_gate)
            {
                bool fresh = !File.Exists(_path) || new FileInfo(_path).Length == 0;
                var parts = new List<string> { DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), run, exp };
                foreach (var c in cols)
                    parts.Add(c switch
                    {
                        null => "",
                        double d => N(d),
                        float f => N(f),
                        bool b => b ? "1" : "0",
                        _ => Convert.ToString(c, CultureInfo.InvariantCulture)?.Replace(',', ';').Replace('\n', ' ') ?? "",
                    });
                File.AppendAllText(_path, (fresh ? _header + Environment.NewLine : "") + string.Join(",", parts) + Environment.NewLine);
            }
        }
        catch { }
    }
}
