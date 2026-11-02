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

    /// <summary>Hard ceiling on one background process (once per process). floor = a safe level learned earlier
    /// (auto-squeeze will not probe below it for the first minutes).</summary>
    void HoldProcess(int pid, long ceiling, long ws = 0, long floor = 0)
    {
        if (_limited.ContainsKey(pid) || _failLogged.Contains(pid)) return;
        DateTime start;
        try { using var p = Process.GetProcessById(pid); start = p.StartTime; } catch { return; }
        if (!Native.TryGetLimits(pid, out var omin, out var omax)) { _failLogged.Add(pid); return; }
        if (!Native.SetCeiling(pid, ceiling)) { _failLogged.Add(pid); return; }
        var st = new Limited { Start = start, OrigMin = omin, OrigMax = omax, Ceiling = ceiling, BaseCeiling = ceiling, OrigWs = Math.Max(ws, ceiling) };
        if (floor > 0) { st.Floor = floor; st.FloorUntil = DateTime.UtcNow.AddMinutes(5); }
        _limited[pid] = st;
    }

    /// <summary>Caps the processes of one background app. With a preset HoldMB the target for the app is
    /// HoldMB x its process count. The BIGGEST processes are squeezed first, each only as far as needed, and
    /// squeezing stops as soon as the app total reaches that target. CPU use is not a criterion (RAM and CPU are
    /// unrelated); stutter is detected from page faults in TuneHeld. No process goes below max(HoldFloorMB, HoldKeepPercent % of its size, the safe level learned for
    /// this app). Processes that appear later or grow back are handled on the next scan.
    /// Without HoldMB (Custom) every process gets the target's own CeilingMB.</summary>
    void HoldGroup(List<Proc> procs, Target t)
    {
        var fresh = procs.Where(x => !_limited.ContainsKey(x.Pid) && !_failLogged.Contains(x.Pid)).ToList();
        if (fresh.Count == 0) return;
        var pr0 = P;
        long perProc = (long)pr0.HoldMB * MB;
        long min = t.MinCeilingMB * MB, max = Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB;

        if (perProc <= 0)
        {
            foreach (var x in fresh) HoldProcess(x.Pid, Math.Clamp(t.CeilingMB * MB, min, max), x.Ws);
            return;
        }

        long target = perProc * procs.Count;
        long projected = procs.Sum(x => x.Ws);
        long floorAbs = Math.Max(8, pr0.HoldFloorMB > 0 ? pr0.HoldFloorMB : Config.HoldMinProcessMB) * MB;
        double keep = Math.Clamp(pr0.HoldKeepPct > 0 ? pr0.HoldKeepPct : Config.HoldKeepPercent, 0, 100) / 100.0;
        long learned = Config.LearnFloors ? LearnedStore.Get(t.Exe) * MB : 0;

        // biggest first: the fewest processes have to be touched to reach the target
        foreach (var x in fresh.OrderByDescending(x => x.Ws))
        {
            if (projected <= target) break;                       // target reached: leave the rest alone
            long safe = learned > 0 ? Math.Min(learned, x.Ws) : 0;
            long floor = Math.Max(Math.Max(floorAbs, (long)(x.Ws * keep)), safe);
            long cap = Math.Max(floor, x.Ws - (projected - target));
            if (cap >= x.Ws) continue;                            // already at its floor: nothing to gain
            HoldProcess(x.Pid, cap, x.Ws, safe);
            projected -= x.Ws - cap;
        }
    }

    /// <summary>Finds the lowest RAM a held process can live on without stutter.
    /// Quiet (no page faults) for AutoProbeSeconds -> lower the ceiling one step (10%, or straight to its real
    /// use + 25% when the ceiling is not even binding). At its ceiling and page-faulting -> raise 25% and remember
    /// that level as a floor for 5 minutes, then probe lower again. Never above what the process used before
    /// it was held (+20%), never below AutoMinProcessMB. With AutoSqueeze off it only grows and relaxes back.</summary>
    void TuneHeld(List<Proc> procs, Target t)
    {
        var now = DateTime.UtcNow;
        var c = Config;
        bool auto = c.AutoSqueeze;
        var pz = P;
        long absMin = Math.Max(8, pz.HoldFloorMB > 0 ? Math.Min(c.AutoMinProcessMB, pz.HoldFloorMB) : c.AutoMinProcessMB) * MB;   // low levels may squeeze below the global minimum
        double faultRate = Math.Max(10, c.AutoFaultRate);
        double probe = Math.Max(3, c.AutoProbeSeconds);
        long tmax = Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB;
        foreach (var x in procs)
        {
            if (!_limited.TryGetValue(x.Pid, out var st)) continue;
            if (!Native.GetMemory(x.Pid, out var ws, out var pf)) continue;
            if (st.LastPfAt == DateTime.MinValue) { st.LastPf = pf; st.LastPfAt = now; continue; }
            double dt = (now - st.LastPfAt).TotalSeconds;
            if (dt < 1.0) continue;
            double rate = Math.Max(0, pf - st.LastPf) / dt;
            st.LastPf = pf; st.LastPfAt = now;

            long hi = Math.Max(st.Ceiling, Math.Min(tmax, (long)(st.OrigWs * 1.2)));
            bool binding = ws >= st.Ceiling * 0.9;
            long next = st.Ceiling;

            if (binding && rate > faultRate)
            {
                // Starting to stutter: give room back and remember this level as safe for a while.
                st.QuietSecs = 0;
                next = Math.Min(hi, st.Ceiling + Math.Max(16 * MB, st.Ceiling / 4));
                if (auto)
                {
                    st.Floor = next; st.FloorUntil = now.AddMinutes(5);
                    if (c.LearnFloors) LearnedStore.Raise(t.Exe, (int)(next / MB));
                }
            }
            else if (rate < faultRate * 0.2)
            {
                st.QuietSecs += dt;
                if (st.QuietSecs >= probe)
                {
                    st.QuietSecs = 0;
                    if (auto)
                    {
                        long lowest = Math.Max(absMin, now < st.FloorUntil ? st.Floor : 0);
                        next = binding ? st.Ceiling - st.Ceiling / 10 : (long)(ws * 1.25);
                        next = Math.Max(lowest, Math.Min(next, st.Ceiling));
                    }
                    else if (st.Ceiling > st.BaseCeiling)
                        next = Math.Max(st.BaseCeiling, st.Ceiling - st.Ceiling / 10);
                }
            }
            else st.QuietSecs = 0;

            if (next != st.Ceiling && Native.SetCeiling(x.Pid, next)) st.Ceiling = next;
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
        RefreshAudio(now);
        LearnedStore.Flush(false);

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
        // Memory priority: give it back to processes that left the list (exited, or now excluded as a game).
        RestorePriority(_prio.Keys.Where(k => !seen.Contains(k)).ToList());
        _prioFail.RemoveWhere(k => !seen.Contains(k));
        if (!Config.LowerMemoryPriority) RestoreAllPriorities();
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
        foreach (var (name, allProcs) in byName)
        {
            var t = ResolveTarget(name);
            if (!_groups.TryGetValue(name, out var g))
            {
                long b = (long)((t.BudgetMB > 0 ? t.BudgetMB : Math.Max(t.MinCeilingMB, t.MaxCeilingMB)) * MB * pr.BudgetScale);
                g = _groups[name] = new Group { Budget = b, BaseBudget = b };
            }

            // Only the process that owns an active audio stream (the audio service of Discord/Chrome, the player
            // of Spotify) is left alone; the rest of the app is limited as usual.
            var procs = allProcs;
            if (_audioSeen.Count > 0)
            {
                var audio = allProcs.Where(x => IsAudioActive(x.Pid)).ToList();
                if (audio.Count > 0)
                {
                    foreach (var x in audio) { ReleasePid(x.Pid); RestorePriority(new List<int> { x.Pid }); }
                    procs = allProcs.Where(x => !IsAudioActive(x.Pid)).ToList();
                    if (procs.Count == 0) { g.Pending = false; continue; }
                }
            }

            if (_exempt.Contains(name))
            {
                g.LastFocused = now; g.Pending = false;
                if (_prio.Count > 0) RestorePriority(procs.Select(x => x.Pid).Where(_prio.ContainsKey).ToList());
                if (g.Holding || _limited.Count > 0) { g.Holding = false; ReleaseByName(name); }   // focused: ceiling off at once
                continue;
            }

            long total = procs.Sum(x => x.Ws);

            // Keep a background app that was already trimmed from growing back (also new child processes).
            if (g.Holding && Config.HoldBackground)
            {
                HoldGroup(procs, t);
                TuneHeld(procs, t);
            }

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

            // Low memory priority for background apps: no cap, Windows just takes their pages first when RAM is short.
            if (Config.LowerMemoryPriority && (now - g.LastFocused).TotalSeconds >= pr.Grace) LowerPriority(procs);

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
