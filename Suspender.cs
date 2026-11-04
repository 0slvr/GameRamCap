using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameRamCap;

/// <summary>Freezes (suspends) and resumes whole apps. A frozen app uses no CPU and keeps its RAM and its state,
/// nothing is lost (unlike End task). Everything frozen here is written to suspended.json so a crash or End Task
/// of GameRamCap cannot leave an app frozen for good: the next start (or the watchdog) resumes them.</summary>
internal static class Suspender
{
    const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")]
    static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")]
    static extern int NtResumeProcess(IntPtr process);

    public sealed class Entry
    {
        public int Pid { get; set; }
        public string Name { get; set; } = "";
        public long StartTicks { get; set; }
    }

    static readonly object Lock = new();
    static readonly Dictionary<int, Entry> Frozen = new();

    static string FilePath => Path.Combine(ConfigStore.Dir, "suspended.json");

    static long StartOf(Process p) { try { return p.StartTime.Ticks; } catch { return 0; } }

    static void Persist()
    {
        try
        {
            if (Frozen.Count == 0) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
            Directory.CreateDirectory(ConfigStore.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Frozen.Values.ToList()));
        }
        catch { }
    }

    public static bool IsFrozen(string name)
    {
        lock (Lock) return Frozen.Values.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static int Count { get { lock (Lock) return Frozen.Count; } }

    /// <summary>Freezes every process of the app. System, security and recording processes (Engine.Protected) and
    /// GameRamCap itself are refused. Returns how many were frozen / failed.</summary>
    public static (int ok, int failed) Suspend(string name)
    {
        if (Engine.Protected.Contains(name)) return (0, 0);
        int ok = 0, failed = 0, self = Environment.ProcessId;
        Process[] list;
        try { list = Process.GetProcessesByName(name); } catch { return (0, 0); }
        foreach (var p in list)
        {
            try
            {
                if (p.Id == self) continue;
                lock (Lock) { if (Frozen.ContainsKey(p.Id)) continue; }   // already frozen: never freeze twice
                var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, p.Id);
                if (h == IntPtr.Zero) { failed++; continue; }
                try
                {
                    if (NtSuspendProcess(h) == 0)
                    {
                        lock (Lock) Frozen[p.Id] = new Entry { Pid = p.Id, Name = name, StartTicks = StartOf(p) };
                        ok++;
                    }
                    else failed++;
                }
                finally { CloseHandle(h); }
            }
            catch { failed++; }
            finally { p.Dispose(); }
        }
        lock (Lock) Persist();
        return (ok, failed);
    }

    static bool ResumeOne(Entry e)
    {
        try
        {
            using var p = Process.GetProcessById(e.Pid);
            if (e.StartTicks != 0 && StartOf(p) != e.StartTicks) return false;   // PID was reused by another program
            var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, e.Pid);
            if (h == IntPtr.Zero) return false;
            try { return NtResumeProcess(h) == 0; }
            finally { CloseHandle(h); }
        }
        catch { return false; }   // process is gone: nothing to resume
    }

    static int ResumeWhere(Func<Entry, bool> pick)
    {
        int n = 0;
        lock (Lock)
        {
            foreach (var e in Frozen.Values.Where(pick).ToList())
            {
                if (ResumeOne(e)) n++;
                Frozen.Remove(e.Pid);
            }
            Persist();
        }
        return n;
    }

    public static int Resume(string name) =>
        ResumeWhere(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public static int ResumeAll() => ResumeWhere(_ => true);

    /// <summary>After a crash / End Task: resumes whatever the previous run left frozen (also used by the watchdog).</summary>
    public static int RecoverStale()
    {
        int n = 0;
        try
        {
            if (!File.Exists(FilePath)) return 0;
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new();
            foreach (var e in list)
                if (ResumeOne(e)) n++;
            File.Delete(FilePath);
        }
        catch { }
        return n;
    }
}
