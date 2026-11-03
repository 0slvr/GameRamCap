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
    }


    /// <summary>Clears hard ceilings a previous crashed run may have left on target apps.</summary>
    public void ClearStaleCeilings()
    {
        foreach (var t in Config.Targets)
        {
            foreach (var p in Safe(() => Process.GetProcessesByName(t.Exe)))
            {
                try
                {
                    if (Native.TryGetLimits(p.Id, out var min, out var max))
                        Native.Release(p.Id, min, max);
                }
                finally { p.Dispose(); }
            }
        }
    }

    /// <summary>Lifts every hard working-set cap on every process (used by the watchdog after an End Task,
    /// and safe to call any time: it only touches processes that really have a hard cap).</summary>
    public static int ReleaseAllHardCeilings()
    {
        int n = 0, self = Environment.ProcessId;
        foreach (var p in Safe(() => Process.GetProcesses()))
        {
            try
            {
                if (p.Id <= 4 || p.Id == self) continue;
                if (Native.HasHardCeiling(p.Id, out var min, out var max) && Native.Release(p.Id, min, max)) n++;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return n;
    }

    /// <summary>Everything mode: also clears leftover hard caps on any non-target process.</summary>
    public void ClearStaleCeilingsEverything()
    {
        if (!Config.LimitEverything) return;
        foreach (var p in Safe(() => Process.GetProcesses()))
        {
            try
            {
                if (p.Id <= 4 || Protected.Contains(p.ProcessName)) continue;
                if (Native.HasHardCeiling(p.Id, out var min, out var max)) Native.Release(p.Id, min, max);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    void ApplyAndAdapt()
    {
        var alive = new HashSet<int>();

        foreach (var t in Config.Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Exe) || Protected.Contains(t.Exe)) continue;
            if (IsException(t.Exe)) continue;
            if (_exempt.Contains(t.Exe)) { ReleaseByName(t.Exe); continue; } // focused: no limit

            foreach (var p in Safe(() => Process.GetProcessesByName(t.Exe)))
            {
                try
                {
                    int pid = p.Id;
                    DateTime start = Safe(() => p.StartTime, DateTime.MinValue);
                    alive.Add(pid);

                    if (_limited.TryGetValue(pid, out var st) && st.Start == start)
                    {
                        if (t.Adaptive) Adapt(p, t, st);
                        continue;
                    }

                    if (!Native.TryGetLimits(pid, out var omin, out var omax))
                    {
                        if (_failLogged.Add(pid)) Logger.Write($"Cannot open {t.Exe} (pid {pid}) - access denied. Try running GameRamCap as Administrator.");
                        continue;
                    }
                    long ceiling = Math.Clamp(t.CeilingMB, t.MinCeilingMB, Math.Max(t.MinCeilingMB, t.MaxCeilingMB)) * MB;
                    if (_learned.TryGetValue(t.Exe, out var lc))
                        ceiling = Math.Clamp(lc, t.MinCeilingMB * MB, Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB);
                    long wsNow = Safe(() => p.WorkingSet64, 0L);
                    if (!Native.SetCeiling(pid, ceiling))
                    {
                        if (_failLogged.Add(pid)) Logger.Write($"Failed to set a ceiling on {t.Exe} (pid {pid}).");
                        continue;
                    }
                    Logger.Write($"Limited {t.Exe} (pid {pid}) to {ceiling / MB} MB (was using {wsNow / MB} MB).");

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

        // Forget processes that exited (their limits vanish with them).
        foreach (var pid in _limited.Keys.Where(k => !alive.Contains(k)).ToList())
            _limited.Remove(pid);
    }

    /// <summary>Lifts the limit from every running process with this name (used when it gets focus).</summary>
    void ReleaseByName(string name)
    {
        foreach (var p in Safe(() => Process.GetProcessesByName(name)))
        {
            try
            {
                if (_limited.TryGetValue(p.Id, out var st))
                {
                    if (Safe(() => p.StartTime, DateTime.MinValue) == st.Start)
                        Native.Release(p.Id, st.OrigMin, st.OrigMax);
                    _limited.Remove(p.Id);
                }
            }
            finally { p.Dispose(); }
        }
    }

    public void RestoreAll()
    {
        if (_labStarted) LabRestoreAll("restore-all");
        RestoreAllPriorities();
        if (_limited.Count == 0) return;
        foreach (var (pid, st) in _limited)
        {
            // Skip reused PIDs: only restore if it is still the same process.
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.StartTime != st.Start) continue;
                Native.Release(pid, st.OrigMin, st.OrigMax);
            }
            catch { /* process is gone, nothing to restore */ }
        }
        _limited.Clear();
    }
}
