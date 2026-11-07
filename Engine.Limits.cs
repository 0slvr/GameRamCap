using System.Diagnostics;

namespace GameRamCap;

internal sealed partial class Engine
{
    sealed class Limited
    {
        public DateTime Start;
        public nuint OrigMin, OrigMax;
        public long Ceiling;
        public TimeSpan LastCpu;
        public DateTime LastSample = DateTime.UtcNow;
        public DateTime LastAdjust = DateTime.MinValue;
        public double HighSecs, LowSecs;
        public long BaseCeiling, LastPf;      // Smart hold: starting ceiling, last page-fault count
        public DateTime LastPfAt = DateTime.MinValue;
        public double QuietSecs;
        public long OrigWs, Floor;            // size when first held; lowest ceiling known to be safe
        public DateTime FloorUntil = DateTime.MinValue;
        public double CpuEwma;                // smoothed CPU use (% of one core), for the starvation check
        public bool Releasing;                // Release failed: keep the entry and retry, never forget a live cap
    }

    /// <summary>Lifts the ceiling of one process. True = nothing is left to lift (released, process gone, or the
    /// PID now belongs to another process). False = the process is alive and still capped: the caller must KEEP it
    /// in _limited so the release is retried.</summary>
    bool TryRelease(int pid, Limited st)
    {
        Process p;
        try { p = Process.GetProcessById(pid); } catch { return true; }   // gone
        using (p)
        {
            try { if (st.Start != DateTime.MinValue && p.StartTime != st.Start) return true; }   // reused PID: not ours (MinValue = start time was unreadable: cannot tell)
            catch { /* cannot tell: try the release anyway */ }
        }
        return Native.Release(pid, st.OrigMin, st.OrigMax);
    }

    /// <summary>Releases and forgets one entry only if the release really worked; otherwise marks it for retry.</summary>
    bool ReleaseAndForget(int pid, Limited st)
    {
        if (TryRelease(pid, st)) { _limited.Remove(pid); return true; }
        if (!st.Releasing) Logger.Write($"Could not lift the ceiling of pid {pid} - will keep retrying.");
        st.Releasing = true;
        return false;
    }

    /// <summary>Releases (and forgets) the given processes. True only if every one of them is free now.</summary>
    bool ReleasePids(IEnumerable<int> pids)
    {
        bool ok = true;
        foreach (var pid in pids.ToList())
            if (_limited.TryGetValue(pid, out var st) && !ReleaseAndForget(pid, st)) ok = false;
        return ok;
    }

    /// <summary>Retries every release that failed earlier.</summary>
    void RetryPendingReleases()
    {
        foreach (var (pid, st) in _limited.Where(kv => kv.Value.Releasing).ToList())
            ReleaseAndForget(pid, st);
    }


    /// <summary>Clears hard ceilings a previous crashed run left behind. Only the ones GameRamCap itself set (see CeilingLedger).</summary>
    public void ClearStaleCeilings()
    {
        int n = CeilingLedger.ReleaseAll();
        if (n > 0) Logger.Write($"Lifted {n} ceiling(s) left by the previous run.");
    }

    /// <summary>Kept for the callers: the ledger already covers every process, Everything mode included.</summary>
    public void ClearStaleCeilingsEverything() { }

    /// <summary>Lifts every ceiling GameRamCap set (used by the watchdog after an End Task). Ceilings set by other programs are left alone.</summary>
    public static int ReleaseAllHardCeilings() => CeilingLedger.ReleaseAll();

    /// <summary>Hard "per app" scope: the app's ceiling is the total, so each of its <paramref name="count"/> processes
    /// gets an equal share (never below 16 MB). Shares are fixed; they are not adapted.</summary>
    static long Share(long appCeiling, int count) => count <= 1 ? appCeiling : Math.Max(16 * MB, appCeiling / count);

    void ApplyAndAdapt()
    {
        RetryPendingReleases();
        var alive = new HashSet<int>();

        foreach (var t in Config.Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Exe) || Protected.Contains(t.Exe)) continue;
            if (IsException(t.Exe)) continue;
            if (_exempt.Contains(t.Exe)) { ReleaseByName(t.Exe); continue; } // focused: no limit

            var instances = Safe(() => Process.GetProcessesByName(t.Exe));
            bool perApp = Config.HardPerApp();
            foreach (var p in instances)
            {
                try
                {
                    int pid = p.Id;
                    if (pid == SelfPid) continue;                      // never limit GameRamCap itself
                    DateTime start = Safe(() => p.StartTime, DateTime.MinValue);
                    alive.Add(pid);

                    if (_limited.TryGetValue(pid, out var st) && st.Start == start)
                    {
                        if (st.Releasing) continue;
                        if (t.Adaptive && !perApp) Adapt(p, t, st);
                        continue;
                    }

                    if (!Native.TryGetLimits(pid, out var omin, out var omax))
                    {
                        if (_failLogged.Add(pid)) Logger.Write($"Cannot open {t.Exe} (pid {pid}) - access denied. Try running GameRamCap as Administrator.");
                        continue;
                    }
                    long ceiling = Math.Clamp(t.CeilingMB, t.MinCeilingMB, Math.Max(t.MinCeilingMB, t.MaxCeilingMB)) * MB;
                    if (t.Adaptive && _learned.TryGetValue(t.Exe, out var lc) && lc.Base == t.CeilingMB)   // not after the user edited the Limit, not when Adaptive is off
                        ceiling = Math.Clamp(lc.Ceiling, t.MinCeilingMB * MB, Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB);
                    if (perApp) ceiling = Share(ceiling, instances.Length);
                    long wsNow = Safe(() => p.WorkingSet64, 0L);
                    if (!Native.SetCeiling(pid, ceiling))
                    {
                        if (_failLogged.Add(pid)) Logger.Write($"Failed to set a ceiling on {t.Exe} (pid {pid}).");
                        continue;
                    }
                    Logger.Write($"Limited {t.Exe} (pid {pid}) to {ceiling / MB} MB (was using {wsNow / MB} MB).");
                    var lp = new List<Proc> { new Proc { Pid = pid } };
                    LowerPriority(lp);
                    LowerThreads(lp);

                    _limited[pid] = new Limited
                    {
                        Start = start,
                        OrigMin = omin,
                        OrigMax = omax,
                        Ceiling = ceiling,
                        LastCpu = Safe(() => p.TotalProcessorTime, TimeSpan.Zero),
                    };
                }
                finally { p.Dispose(); }
            }
        }

        // Entries whose process is no longer handled here: released (a process that exited counts as released). A live process
        // is never forgotten while it still carries our ceiling: if the release fails it stays and is retried.
        foreach (var pid in _limited.Keys.Where(k => !alive.Contains(k)).ToList())
            if (_limited.TryGetValue(pid, out var gone)) ReleaseAndForget(pid, gone);
        RestorePriority(_prio.Keys.Union(_threadPrio.Keys).Where(k => !alive.Contains(k)).ToList());
    }

    /// <summary>Lifts the limit from every running process with this name (used when it gets focus).</summary>
    bool ReleaseByName(string name)
    {
        var pids = new List<int>();
        foreach (var p in Safe(() => Process.GetProcessesByName(name)))
        {
            try { pids.Add(p.Id); }
            finally { p.Dispose(); }
        }
        RestorePriority(pids);   // the app is wanted again: normal memory priority
        return ReleasePids(pids);
    }

    public void RestoreAll()
    {
        RestoreAllPriorities();
        if (_limited.Count == 0) return;
        // Only entries whose release worked (or whose process is gone) are forgotten; the rest stay and are retried.
        foreach (var (pid, st) in _limited.ToList())
            ReleaseAndForget(pid, st);
    }
}
