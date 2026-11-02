using System.Diagnostics;

namespace GameRamCap;

// Smart mode. No hard caps at all, so an app can never be starved or crash because of us:
//  * Nothing happens while Windows has enough free RAM.
//  * Under pressure, background apps are soft-trimmed (EmptyWorkingSet): pages go to the standby list and
//    the app takes back what it really needs instantly, without disk access.
//  * The budget is per APP (all its processes together), not per process.
//  * The app in focus (and one you just left) is never touched; busy processes are skipped.
//  * Feedback loop: if an app immediately pulls its pages back, its budget grows and the cooldown doubles.
//    If it stays quiet, the budget slowly returns to the configured value.
internal sealed partial class Engine
{
    sealed class Group
    {
        public long Budget, BaseBudget;
        public int Backoff;
        public DateTime LastTrim = DateTime.MinValue;
        public DateTime LastFocused = DateTime.MinValue;
        public bool Pending;
        public bool Holding;   // background app is kept under a hard ceiling until it gets focus
        public long BeforeWs, AfterWs, PfBase;
    }

    sealed class Proc { public int Pid; public long Ws; public double Cpu; public bool Known; }

    readonly Dictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _cpuPrev = new();
    DateTime _lastSmart = DateTime.MinValue;
    string _smartInfo = "Smart: starting";
    const long PageSize = 4096;
    Preset P => Presets.Resolve(Config);

    /// <summary>Hard ceiling on one background process (once per process).</summary>
    void HoldProcess(int pid, long ceiling)
    {
        if (_limited.ContainsKey(pid) || _failLogged.Contains(pid)) return;
        DateTime start;
        try { using var p = Process.GetProcessById(pid); start = p.StartTime; } catch { return; }
        if (!Native.TryGetLimits(pid, out var omin, out var omax)) { _failLogged.Add(pid); return; }
        if (!Native.SetCeiling(pid, ceiling)) { _failLogged.Add(pid); return; }
        _limited[pid] = new Limited { Start = start, OrigMin = omin, OrigMax = omax, Ceiling = ceiling };
    }

    /// <summary>Caps a whole app. With a preset HoldMB the limit is for the app as a whole: it is shared between
    /// its processes in proportion to what each one uses naturally (8 MB minimum each). A process that appears
    /// later gets an even share. Without HoldMB (Custom) every process gets the target's own CeilingMB.</summary>
    void HoldGroup(List<Proc> procs, Target t)
    {
        var fresh = procs.Where(x => !_limited.ContainsKey(x.Pid) && !_failLogged.Contains(x.Pid)).ToList();
        if (fresh.Count == 0) return;
        long app = P.HoldMB * MB;
        bool first = !procs.Any(x => _limited.ContainsKey(x.Pid));
        long sumWs = Math.Max(1, fresh.Sum(x => x.Ws));
        long min = t.MinCeilingMB * MB, max = Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB;
        foreach (var x in fresh)
        {
            long cap = app <= 0 ? Math.Clamp(t.CeilingMB * MB, min, max)
                     : first ? Math.Max(8 * MB, app * x.Ws / sumWs)
                     : Math.Max(8 * MB, app / procs.Count);
            HoldProcess(x.Pid, cap);
        }
    }

    void ApplySmart()
    {
        var now = DateTime.UtcNow;
        if (!ReferenceEquals(_cfgSeen, Config))
        {
            _cfgSeen = Config;
            RestoreAll();   // lift any ceilings held under the previous settings
            _synth.Clear(); _groups.Clear(); _cpuPrev.Clear(); _failLogged.Clear();
            _lastSmart = DateTime.MinValue;
        }
        if ((now - _lastSmart).TotalSeconds < Math.Max(0.5, P.Scan)) return;
        _lastSmart = now;

        double freePct = Native.FreeRamPercent();
        var pr = P;
        int level = freePct < 0 ? 0
                  : freePct < pr.Critical ? 2
                  : freePct < pr.Pressure ? 1 : 0;
        bool always = level == 0 && pr.Always;
        if (always) level = 1;
        _smartInfo = always ? $"{pr.Name}: RAM free {freePct:0}% - keeping background apps in budget"
                   : level == 0 ? $"{pr.Name}: RAM free {freePct:0}% - nothing touched"
                   : level == 1 ? $"{pr.Name}: RAM free {freePct:0}% - trimming idle background apps"
                   :              $"{pr.Name}: RAM low ({freePct:0}%) - trimming harder";

        // 1) Collect processes, grouped by app name.
        var byName = new Dictionary<string, List<Proc>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();
        foreach (var p in Safe(() => Process.GetProcesses()))
        {
            try
            {
                int pid = p.Id;
                if (pid <= 4) continue;
                string name = p.ProcessName;
                if (Protected.Contains(name) || IsException(name)) continue;
                if (Config.Games.Contains(name, StringComparer.OrdinalIgnoreCase)) continue; // never trim your games
                if (_gameNames.Contains(name)) continue;                                      // nor detected fullscreen games
                if (!Config.LimitEverything &&
                    !Config.Targets.Any(t => string.Equals(t.Exe, name, StringComparison.OrdinalIgnoreCase))) continue;

                seen.Add(pid);
                if (_failLogged.Contains(pid)) continue;

                long ws = Safe(() => p.WorkingSet64, 0L);
                var cpu = Safe(() => p.TotalProcessorTime, TimeSpan.Zero);
                double busy = 0; bool known = false;
                if (_cpuPrev.TryGetValue(pid, out var prev) && cpu >= prev.Cpu)
                {
                    double secs = (now - prev.At).TotalSeconds;
                    if (secs > 0.2) { busy = (cpu - prev.Cpu).TotalSeconds / secs; known = true; }
                }
                _cpuPrev[pid] = (cpu, now);

                if (!byName.TryGetValue(name, out var list)) byName[name] = list = new List<Proc>();
                list.Add(new Proc { Pid = pid, Ws = ws, Cpu = busy, Known = known });
            }
            finally { p.Dispose(); }
        }
        foreach (var pid in _cpuPrev.Keys.Where(k => !seen.Contains(k)).ToList()) _cpuPrev.Remove(pid);
        foreach (var pid in _limited.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            // Process exited, or is now excluded (game detected): lift the ceiling if it is still the same process.
            try
            {
                using var gone = Process.GetProcessById(pid);
                if (gone.StartTime == _limited[pid].Start) Native.Release(pid, _limited[pid].OrigMin, _limited[pid].OrigMax);
            }
            catch { }
            _limited.Remove(pid);
        }

        // 2) Decide per app.
        foreach (var (name, procs) in byName)
        {
            var t = ResolveTarget(name);
            if (!_groups.TryGetValue(name, out var g))
            {
                long b = (long)((t.BudgetMB > 0 ? t.BudgetMB : Math.Max(t.MinCeilingMB, t.MaxCeilingMB)) * MB * pr.BudgetScale);
                g = _groups[name] = new Group { Budget = b, BaseBudget = b };
            }

            if (_exempt.Contains(name))
            {
                g.LastFocused = now; g.Pending = false;
                if (g.Holding || _limited.Count > 0) { g.Holding = false; ReleaseByName(name); }   // focused: ceiling off at once
                continue;
            }

            long total = procs.Sum(x => x.Ws);

            // Keep a background app that was already trimmed from growing back (also new child processes).
            if (g.Holding && Config.HoldBackground)
                HoldGroup(procs, t);

            // Feedback on the last trim: did the app fight back?
            if (g.Pending && (now - g.LastTrim).TotalSeconds >= 8)
            {
                g.Pending = false;
                long pf = 0;
                foreach (var x in procs) if (Native.GetMemory(x.Pid, out _, out var f)) pf += f;
                double refaulted = Math.Max(0, pf - g.PfBase) * (double)PageSize;
                long freed = Math.Max(1, g.BeforeWs - g.AfterWs);
                bool fighting = refaulted > freed * 0.5 || total > g.BeforeWs * 0.9;
                if (fighting)
                {
                    g.Backoff = Math.Min(g.Backoff + 1, 5);
                    g.Budget = Math.Min(g.Budget + g.Budget / 4 + 32 * MB, g.BaseBudget * 4);
                    Logger.Write($"{name} took its pages back - budget raised to {g.Budget / MB} MB, next trim later.");
                }
                else
                {
                    if (g.Backoff > 0) g.Backoff--;
                    g.Budget = Math.Max(g.BaseBudget, g.Budget - g.Budget / 10);
                }
            }

            if (level == 0) continue;
            if ((now - g.LastFocused).TotalSeconds < pr.Grace) continue;

            // Hold mode: the intensity is the hard per-process ceiling (pr.HoldMB). Setting it also
            // squeezes the process down to that size at once, so no separate trim is needed.
            if (Config.HoldBackground && pr.HoldMB > 0)
            {
                g.Holding = true;
                HoldGroup(procs, t);
                continue;
            }
            double cooldown = Math.Min(600, pr.Cooldown * Math.Pow(2, g.Backoff));
            if ((now - g.LastTrim).TotalSeconds < cooldown) continue;

            long target = level == 2 ? Math.Max((long)(t.MinCeilingMB * MB * Math.Min(1.0, pr.BudgetScale)), (long)(g.Budget * pr.CritFactor)) : g.Budget;
            if (total <= target) continue;

            // Trim the biggest idle processes first, only as many as needed to reach the budget.
            double busyLimit = level == 2 ? pr.BusyCrit : pr.Busy;
            long projected = total; int trimmed = 0;
            foreach (var v in procs.Where(x => x.Known && x.Ws >= pr.MinProcMB * MB && x.Cpu < busyLimit).OrderByDescending(x => x.Ws))
            {
                if (projected <= target) break;
                if (!Native.Trim(v.Pid)) { _failLogged.Add(v.Pid); continue; }
                trimmed++;
                projected -= (long)(v.Ws * 0.85);
            }
            if (trimmed == 0) continue;

            if (Config.HoldBackground)
            {
                g.Holding = true;
                HoldGroup(procs, t);
            }

            long after = 0, pfBase = 0;
            foreach (var x in procs)
                if (Native.GetMemory(x.Pid, out var w, out var f)) { after += w; pfBase += f; }
                else after += x.Ws;

            g.LastTrim = now; g.Pending = true;
            g.BeforeWs = total; g.AfterWs = after; g.PfBase = pfBase;
            if (total - after >= 50 * MB)
                Logger.Write($"Trimmed {name}: {total / MB} -> {after / MB} MB across {procs.Count} process(es) (free RAM {freePct:0}%).");
        }
    }
}
