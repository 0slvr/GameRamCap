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

    void ApplyAndAdapt()
    {
        var alive = new HashSet<int>();

        foreach (var t in Config.Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Exe) || Protected.Contains(t.Exe)) continue;

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

    public void RestoreAll()
    {
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
