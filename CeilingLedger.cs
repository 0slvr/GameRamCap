using System.Diagnostics;
using System.Text.Json;

namespace GameRamCap;

/// <summary>Remembers which processes GameRamCap itself put a hard RAM ceiling on (pid + start time), in a file inside the
/// protected data folder. After a crash / End Task the watchdog or the next start lifts exactly these ceilings and nothing else,
/// so caps set by other programs are left alone.</summary>
internal static class CeilingLedger
{
    public sealed class Entry
    {
        public int Pid { get; set; }
        public long StartTicks { get; set; }
    }

    static readonly object Lock = new();
    static readonly object WriteLock = new();   // one writer at a time, and the snapshot is taken inside it: an older snapshot can never overwrite a newer one
    static readonly Dictionary<int, long> Mine = new();
    static System.Threading.Timer? _timer;
    static bool _pending;
    static DateTime _lastPrune = DateTime.MinValue;

    static string FilePath => Path.Combine(ConfigStore.Dir, "ceilings.json");

    static long StartOf(int pid)
    {
        return Native.GetStartTime(pid)?.Ticks ?? 0;
    }

    /// <summary>Called after a ceiling was set. Cheap when the process is already known.</summary>
    public static void Add(int pid)
    {
        lock (Lock) { if (Mine.ContainsKey(pid)) return; }
        long st = StartOf(pid);
        if (st == 0) return;
        lock (Lock) { Mine[pid] = st; Schedule(); }
    }

    /// <summary>Called after a ceiling was lifted.</summary>
    public static void Remove(int pid)
    {
        lock (Lock) { if (Mine.Remove(pid)) Schedule(); }
    }

    // The file is written at most once per 150 ms, however many processes change.
    static void Schedule()
    {
        if (_pending) return;
        _pending = true;
        if (_timer == null) _timer = new System.Threading.Timer(_ => Flush(), null, 150, System.Threading.Timeout.Infinite);
        else _timer.Change(150, System.Threading.Timeout.Infinite);
    }

    public static void Flush()
    {
        try
        {
            lock (WriteLock)
            {
                List<Entry> list;
                lock (Lock)
                {
                    _pending = false;
                    if ((DateTime.UtcNow - _lastPrune).TotalSeconds >= 30)   // forgetting gone processes costs one OpenProcess each: not on every write
                    {
                        _lastPrune = DateTime.UtcNow;
                        foreach (var k in Mine.Where(kv => StartOf(kv.Key) != kv.Value).Select(kv => kv.Key).ToList()) Mine.Remove(k);
                    }
                    list = Mine.Select(kv => new Entry { Pid = kv.Key, StartTicks = kv.Value }).ToList();
                }
                if (list.Count == 0) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
                SecureStore.WriteAllText(FilePath, JsonSerializer.Serialize(list));
            }
        }
        catch { }
    }

    /// <summary>Lifts every ceiling this program set (this run and the previous one). Returns how many were lifted.</summary>
    public static int ReleaseAll()
    {
        var all = new Dictionary<int, long>();
        lock (Lock) { foreach (var kv in Mine) all[kv.Key] = kv.Value; }
        try
        {
            if (File.Exists(FilePath))
                foreach (var e in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new List<Entry>())
                    all[e.Pid] = e.StartTicks;
        }
        catch { }

        int n = 0, self = Environment.ProcessId;
        foreach (var kv in all)
        {
            int pid = kv.Key;
            if (pid <= 4 || pid == self) continue;
            try
            {
                if (kv.Value == 0 || StartOf(pid) != kv.Value) continue;   // gone, or the PID now belongs to another program
                if (Native.HasHardCeiling(pid, out var min, out var max) && Native.Release(pid, min, max)) n++;
            }
            catch { }
        }
        Flush();
        return n;
    }
}
