using System.Diagnostics;

namespace GameRamCap;

internal sealed class Engine : IDisposable
{
    const long MB = 1024 * 1024;

    // Never touched, even if listed as a target.
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "explorer", "audiodg", "MsMpEng", "SecurityHealthService", "GameRamCap",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "vgc", "vgtray",
    };

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

    readonly Dictionary<int, Limited> _limited = new();
    DateTime _gamingSince = DateTime.MinValue;

    public Config Config { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string Status { get; private set; } = "Idle";

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

    public void Tick()
    {
        bool gaming = Enabled && Config.Enabled && IsGaming();
        var now = DateTime.UtcNow;

        if (gaming) { if (_gamingSince == DateTime.MinValue) _gamingSince = now; }
        else _gamingSince = DateTime.MinValue;

        bool active = gaming && (now - _gamingSince).TotalSeconds >= Config.DelaySeconds;
        if (!active)
        {
            RestoreAll();
            Status = !Enabled || !Config.Enabled ? "Disabled" : gaming ? "Game detected, waiting..." : "Idle";
            return;
        }

        ApplyAndAdapt();
        Status = $"Limiting {_limited.Count} process(es)";
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

                    if (!Native.TryGetLimits(pid, out var omin, out var omax)) continue;
                    long ceiling = Math.Clamp(t.CeilingMB, t.MinCeilingMB, Math.Max(t.MinCeilingMB, t.MaxCeilingMB)) * MB;
                    if (!Native.SetCeiling(pid, ceiling)) continue;

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

    // Same idea as Memory Sentry's policy, written from scratch:
    // busy for 6s while at the ceiling -> +8 MB; idle for 10s with headroom -> -4 MB; 8s cooldown.
    void Adapt(Process p, Target t, Limited st)
    {
        var now = DateTime.UtcNow;
        double dt = (now - st.LastSample).TotalSeconds;
        if (dt < 0.5) return;

        TimeSpan cpu;
        long ws;
        try { p.Refresh(); cpu = p.TotalProcessorTime; ws = p.WorkingSet64; }
        catch { return; }

        double usage = (cpu - st.LastCpu).TotalSeconds / dt; // fraction of one logical core
        st.LastCpu = cpu;
        st.LastSample = now;

        bool atCeiling = ws >= st.Ceiling * 0.95;
        if (usage >= 0.10 && atCeiling) { st.HighSecs += dt; st.LowSecs = 0; }
        else if (usage <= 0.03 && st.Ceiling - ws >= 6 * MB) { st.LowSecs += dt; st.HighSecs = 0; }
        else { st.HighSecs = 0; st.LowSecs = 0; }

        if ((now - st.LastAdjust).TotalSeconds < 8) return;

        long min = t.MinCeilingMB * MB, max = Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB;
        long next = st.Ceiling;
        if (st.HighSecs >= 6) next = Math.Min(st.Ceiling + 8 * MB, max);
        else if (st.LowSecs >= 10) next = Math.Max(st.Ceiling - 4 * MB, min);

        if (next != st.Ceiling && Native.SetCeiling(p.Id, next))
        {
            st.Ceiling = next;
            st.LastAdjust = now;
            st.HighSecs = st.LowSecs = 0;
        }
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

    bool IsGaming()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        string name;
        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
        catch { return false; }

        if (Config.Games.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (!Config.AutoDetectFullscreen) return false;
        if (Protected.Contains(name)) return false;
        if (Config.FullscreenIgnore.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        if (Config.Targets.Any(t => string.Equals(t.Exe, name, StringComparison.OrdinalIgnoreCase))) return false;

        return IsFullscreen(hwnd);
    }

    static bool IsFullscreen(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var b = Screen.FromHandle(hwnd).Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    static T[] Safe<T>(Func<T[]> f) { try { return f(); } catch { return Array.Empty<T>(); } }
    static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

    public void Dispose() => RestoreAll();
}
