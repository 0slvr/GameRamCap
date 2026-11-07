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
            SecureStore.WriteAllText(FilePath, JsonSerializer.Serialize(Frozen.Values.ToList()));
        }
        catch { }
    }

    public static bool IsFrozen(string name)
    {
        lock (Lock) return Frozen.Values.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static int Count { get { lock (Lock) return Frozen.Count; } }

    /// <summary>Forgets frozen entries whose process has exited (or whose PID now belongs to another program), so IsFrozen / Count
    /// do not report dead apps and a reused PID is not blocked. A live process whose start time cannot be read is kept.</summary>
    public static int PruneDead()
    {
        int n = 0;
        lock (Lock)
        {
            foreach (var e in Frozen.Values.ToList())
            {
                bool gone;
                try
                {
                    using var p = Process.GetProcessById(e.Pid);
                    long cur = StartOf(p);
                    gone = cur != 0 && cur != e.StartTicks;
                }
                catch { gone = true; }   // no such process
                if (gone) { Frozen.Remove(e.Pid); n++; }
            }
            if (n > 0) Persist();
        }
        return n;
    }

    /// <summary>Freezes every process of the app. System, security and recording processes (Engine.Protected) and
    /// GameRamCap itself are refused. Returns how many were frozen / failed.</summary>
    public static (int ok, int failed) Suspend(string name)
    {
        int ok = 0, failed = 0, self = Environment.ProcessId;
        Process[] list;
        try { list = Process.GetProcessesByName(name); } catch { return (0, 0); }

        // Pass 1: decide what to freeze and write it to suspended.json BEFORE anything is frozen,
        // so a crash in the middle can never leave an app frozen without a record.
        var todo = new List<Entry>();
        foreach (var p in list)
        {
            try
            {
                if (p.Id == self) continue;
                if (Engine.IsProtected(name, p.Id)) continue;             // by name, but a fake "lsass" is not protected
                lock (Lock) { if (Frozen.ContainsKey(p.Id)) continue; }   // already frozen: never freeze twice
                long startTicks = StartOf(p);
                if (startTicks == 0) { failed++; continue; }              // without a start time it could not be resumed safely later
                todo.Add(new Entry { Pid = p.Id, Name = name, StartTicks = startTicks });
            }
            catch { failed++; }
            finally { p.Dispose(); }
        }
        if (todo.Count == 0) return (0, failed);
        lock (Lock)
        {
            foreach (var e in todo) Frozen[e.Pid] = e;
            Persist();
        }

        // Pass 2: freeze. A process that cannot be frozen is taken off the record again.
        var notFrozen = new List<int>();
        foreach (var e in todo)
        {
            var h = Native.OpenProcess(PROCESS_SUSPEND_RESUME, false, e.Pid);
            if (h == IntPtr.Zero) { notFrozen.Add(e.Pid); failed++; continue; }
            try
            {
                if (NtSuspendProcess(h) == 0) ok++;
                else { notFrozen.Add(e.Pid); failed++; }
            }
            finally { Native.CloseHandle(h); }
        }
        if (notFrozen.Count > 0)
            lock (Lock)
            {
                foreach (int pid in notFrozen) Frozen.Remove(pid);
                Persist();
            }
        return (ok, failed);
    }

    /// <summary>0 = nothing to resume (process gone, or the PID now belongs to another program), 1 = resumed,
    /// 2 = the process is alive but could not be resumed (it must stay in the list so it is tried again).</summary>
    static int ResumeOne(Entry e)
    {
        try
        {
            using var p = Process.GetProcessById(e.Pid);
            if (e.StartTicks == 0 || StartOf(p) != e.StartTicks) return 0;   // PID was reused by another program (or the entry is not ours)
            if (!string.Equals(p.ProcessName, e.Name, StringComparison.OrdinalIgnoreCase)) return 0;
            var h = Native.OpenProcess(PROCESS_SUSPEND_RESUME, false, e.Pid);
            if (h == IntPtr.Zero) return 2;
            try { return NtResumeProcess(h) == 0 ? 1 : 2; }
            finally { Native.CloseHandle(h); }
        }
        catch { return 0; }   // process is gone: nothing to resume
    }

    static int ResumeWhere(Func<Entry, bool> pick)
    {
        int n = 0;
        lock (Lock)
        {
            foreach (var e in Frozen.Values.Where(pick).ToList())
            {
                int r = ResumeOne(e);
                if (r == 1) n++;
                if (r == 2) { Logger.Write($"Could not resume {e.Name} (pid {e.Pid}) - it stays on the frozen list and is tried again."); continue; }
                Frozen.Remove(e.Pid);
            }
            Persist();
        }
        return n;
    }

    public static int Resume(string name) =>
        ResumeWhere(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public static int ResumeAll() => ResumeWhere(_ => true);

    /// <summary>After a crash / End Task: resumes whatever the previous run left frozen (also used by the watchdog).
    /// A process that is alive but could not be resumed is kept in the file, never forgotten.</summary>
    public static int RecoverStale()
    {
        int n = 0;
        try
        {
            if (!File.Exists(FilePath)) return 0;
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new();
            lock (Lock)
            {
                foreach (var e in list)
                {
                    int r = ResumeOne(e);
                    if (r == 1) n++;
                    else if (r == 2) Frozen[e.Pid] = e;   // still frozen: keep it on record
                }
                Persist();   // writes the leftovers, or deletes the file when nothing is left
            }
        }
        catch { }
        return n;
    }
}
