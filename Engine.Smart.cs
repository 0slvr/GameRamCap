using System.Diagnostics;

namespace GameRamCap;

// Smart mode. Gentle by design, and it only touches background apps (never the one in focus, never a protected one):
//  * A hard working-set ceiling (SetProcessWorkingSetSizeEx) is put on a background process once RAM is under pressure, at the size it
//    really needs (measured), and lifted as soon as the app gets focus or RAM recovers. Every ceiling is written to CeilingLedger so a crash cannot leave one behind.
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
        public bool CritDone;  // ceilings already lowered for the current "RAM critical" period
        public long BeforeWs, AfterWs, PfBase;
    }

    /// <summary>Lowest ceiling allowed for a big process (see Config.BigProcMB): a share of the size it had when first held. 0 = no extra floor.</summary>
    long BigFloor(long origWs)
    {
        int big = Config.BigProcMB, pct = Config.BigProcKeepPercent;
        if (big <= 0 || pct <= 0 || origWs < big * MB) return 0;
        return origWs * Math.Clamp(pct, 0, 100) / 100;
    }

    sealed class Proc { public int Pid; public long Ws; public double Cpu; public bool Known; }

    readonly Dictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _cpuPrev = new();
    DateTime _lastSmart = DateTime.MinValue;
    string _smartInfo = "Smart: starting";
    const long PageSize = 4096;
    // Ceilings are lifted once free RAM is back this many points above the pressure threshold, for at least RecoverSecs
    // (hysteresis, so they do not flap on/off right at the threshold).
    const double RecoverMargin = 3, RecoverSecs = 15;
    Preset? _pCache;
    /// <summary>Resolved once per tick (the config only changes between ticks), instead of a new object per call.</summary>
    Preset P => _pCache ??= Presets.Resolve(Config);

    /// <summary>Hard ceiling on one background process (once per process). floor = a safe level measured earlier
    /// (pinned: the ceiling is not lowered below it).</summary>
    void HoldProcess(int pid, long ceiling, long ws = 0, long floor = 0, bool pinFloor = false)
    {
        if (_limited.ContainsKey(pid) || _failLogged.Contains(pid)) return;
        if (Native.GetStartTime(pid) is not DateTime start) return;
        string pname = "?";
        if (Config.LogHolds) { try { using var np = Process.GetProcessById(pid); pname = np.ProcessName; } catch { } }   // the name is only for the log line
        if (!Native.TryGetLimits(pid, out var omin, out var omax)) { _failLogged.Add(pid); return; }
        CaptureResident(pid, ws);   // remember what the process was using before it is squeezed
        if (!Native.SetCeiling(pid, ceiling)) { _failLogged.Add(pid); return; }
        if (Config.LogHolds) Logger.Write($"Hold: {pname} pid {pid} capped at {ceiling / MB} MB (was {ws / MB} MB).");
        var st = new Limited { Start = start, OrigMin = omin, OrigMax = omax, Ceiling = ceiling, BaseCeiling = ceiling, OrigWs = Math.Max(ws, ceiling) };
        if (floor > 0) { st.Floor = floor; st.FloorUntil = pinFloor ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(5); }
        _limited[pid] = st;
    }

    /// <summary>Caps the processes of one background app. With a preset HoldMB the target for the app is
    /// HoldMB x its process count. The BIGGEST processes are squeezed first, each only as far as needed, and
    /// squeezing stops as soon as the app total reaches that target. CPU use is not a criterion (RAM and CPU are
    /// unrelated); stutter is detected from page faults in TuneHeld. No process goes below max(HoldFloorMB, HoldKeepPercent % of its size). Processes that appear later or grow back are handled on the next scan.
    /// Without HoldMB (Custom) every process gets the target's own CeilingMB.</summary>
    void HoldGroup(List<Proc> procs, Target t, int level = 1)
    {
        var pr0 = P;
        // Min process: processes smaller than this are left alone.
        var fresh = procs.Where(x => !_limited.ContainsKey(x.Pid) && !_failLogged.Contains(x.Pid) && x.Ws >= pr0.MinProcMB * MB).ToList();
        if (fresh.Count == 0) return;
        long perProc = (long)pr0.HoldMB * MB;
        long min = t.MinCeilingMB * MB, max = Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB;

        if (perProc <= 0 || t.UseOwnLimit)
        {
            foreach (var x in fresh) HoldProcess(x.Pid, Math.Clamp(t.CeilingMB * MB, min, max), x.Ws);
            return;
        }

        // Budget scale (lower = squeeze more) and, when RAM is critical, the critical factor.
        double scale = pr0.BudgetScale * (level == 2 ? pr0.CritFactor : 1.0);
        long target = (long)(perProc * procs.Count * scale);
        long projected = procs.Sum(x => x.Ws);
        long floorAbs = Math.Max(Presets.AbsMinMB, pr0.HoldFloorMB > 0 ? pr0.HoldFloorMB : Config.HoldMinProcessMB) * MB;
        double keep = Math.Clamp(pr0.HoldKeepPct > 0 ? pr0.HoldKeepPct : Config.HoldKeepPercent, 0, 100) / 100.0;

        // biggest first: the fewest processes have to be touched to reach the target
        foreach (var x in fresh.OrderByDescending(x => x.Ws))
        {
            if (projected <= target) break;                       // target reached: leave the rest alone
            if (Config.MeasureNeed)
            {
                // Measured need: the process is trimmed once and its working set a few seconds later is what it really uses.
                // It is capped at that + a margin (done by ProbeNeed itself); no guessed percentages, no learned ratchet.
                if (!_hot.TryGetValue(x.Pid, out var hot)) { ProbeNeed(x.Pid, t.Exe, x.Ws); continue; }
                long needCap = Math.Max(Presets.AbsMinMB * MB, (long)(hot * (Math.Max(1.0, Config.NeedMargin) + WakeBoost(t.Exe))));   // often-used apps get a bigger margin
                if (Config.NeedMaxMB > 0) needCap = Math.Min(needCap, Config.NeedMaxMB * MB);   // absolute ceiling: a busy process measured right after you left it must not set a 800 MB "need"
                if (needCap >= x.Ws) continue;                    // it uses nearly all it has: nothing to gain
                HoldProcess(x.Pid, needCap, x.Ws, needCap, true);
                projected -= x.Ws - needCap;
                continue;
            }
            long floor = Math.Max(Math.Max(floorAbs, (long)(x.Ws * Math.Max(keep, 0.6 * WakeBoost(t.Exe)))), BigFloor(x.Ws));
            long cap = Math.Max(floor, x.Ws - (projected - target));
            if (cap >= x.Ws) continue;                            // already at its floor: nothing to gain
            HoldProcess(x.Pid, cap, x.Ws);
            projected -= x.Ws - cap;
        }
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _hot = new();       // pid -> measured working set after a trim
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _hotProbing = new();

    /// <summary>Measures what a background process really needs: trim it once, wait, read its working set (the pages it pulled
    /// straight back are its hot set), then cap it at that x NeedMargin. Pinned as its floor, so critical mode never goes below it.
    /// Runs in the background; the process is skipped if you focus the app or it plays audio meanwhile.</summary>
    void ProbeNeed(int pid, string name, long ws0)
    {
        if (!_hotProbing.TryAdd(pid, 0)) return;
        var startAt = Native.GetStartTime(pid);
        if (startAt == null) { _hotProbing.TryRemove(pid, out _); return; }
        var cfgAtStart = Config;
        CaptureResident(pid, ws0);
        if (!Native.Trim(pid)) { _hotProbing.TryRemove(pid, out _); _failLogged.Add(pid); return; }
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)(Math.Max(2, Config.NeedProbeSeconds) * 1000));
                if (!Native.GetMemory(pid, out long ws, out _)) return;
                _hot[pid] = ws;
                long cap = Math.Max(Presets.AbsMinMB * MB, (long)(ws * Math.Max(1.0, Config.NeedMargin)));
                if (Config.NeedMaxMB > 0) cap = Math.Min(cap, Config.NeedMaxMB * MB);
                lock (Gate)
                {
                    if (_disposed || !Enabled || !Config.Enabled || !ReferenceEquals(cfgAtStart, Config)) return;   // closing, paused or settings changed meanwhile
                    if (Native.GetStartTime(pid) != startAt) return;                                                  // the PID now belongs to another process
                    if (_exempt.Contains(name) || _limited.ContainsKey(pid) || IsAudioActive(pid)) return;
                    if (cap >= ws0) return;
                    HoldProcess(pid, cap, ws0, cap, true);
                    var g = _groups.TryGetValue(name, out var gg) ? gg : null;
                    if (g != null) g.Holding = true;
                }
            }
            catch { }
            finally { _hotProbing.TryRemove(pid, out _); }
        });
    }

    /// <summary>One "hold" pass for an app: caps new/grown processes (at most once per Cooldown seconds)
    /// and, while RAM is critical, lowers the existing ceilings once by the critical factor.</summary>
    void HoldPass(Group g, List<Proc> procs, Target t, int level, DateTime now)
    {
        var pr = P;
        if ((now - g.LastTrim).TotalSeconds >= Math.Min(600, pr.Cooldown))
        {
            HoldGroup(procs, t, level);
            g.LastTrim = now;
        }
        if (level == 2)
        {
            if (!g.CritDone) { LowerHeld(procs, t, pr); g.CritDone = true; }
        }
        else g.CritDone = false;
    }

    /// <summary>RAM is critical: every ceiling this app already has is multiplied by the level's critical factor
    /// (never below the level's floor). It is raised again if the app stutters (see TuneHeld).</summary>
    void LowerHeld(List<Proc> procs, Target t, Preset pr)
    {
        long floorAbs = Math.Max(Presets.AbsMinMB, pr.HoldFloorMB > 0 ? pr.HoldFloorMB : Config.HoldMinProcessMB) * MB;
        foreach (var x in procs)
        {
            if (!_limited.TryGetValue(x.Pid, out var st) || st.Releasing) continue;
            long floor = Math.Max(floorAbs, BigFloor(st.OrigWs));
            if (st.FloorUntil == DateTime.MaxValue) floor = Math.Max(Math.Min(st.Floor, st.Ceiling), Presets.AbsMinMB * MB);   // measured need: never below it
            long next = Math.Max(floor, (long)(st.Ceiling * pr.CritFactor));
            if (next < st.Ceiling && Native.SetCeiling(x.Pid, next)) st.Ceiling = next;
        }
    }

    /// <summary>Watches a held process for stutter. At its ceiling and page-faulting (with real disk reads) -> raise the
    /// ceiling 25%, never above what the process used before it was held (+20%). Quiet for a while -> relax a raised
    /// ceiling back towards its starting value. It never lowers a ceiling by itself (the measured need sets that).</summary>
    void TuneHeld(List<Proc> procs, Target t)
    {
        var now = DateTime.UtcNow;
        var c = Config;
        const double faultRate = 150;   // page faults per second (at the ceiling) that count as "starting to stutter"
        const double probe = 10;        // quiet seconds before a raised ceiling relaxes one step
        long tmax = Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB;
        foreach (var x in procs)
        {
            if (!_limited.TryGetValue(x.Pid, out var st) || st.Releasing) continue;
            if (!Native.GetMemory(x.Pid, out var ws, out var pf)) continue;
            if (st.LastPfAt == DateTime.MinValue) { st.LastPf = pf; st.LastPfAt = now; continue; }
            double dt = (now - st.LastPfAt).TotalSeconds;
            if (dt < 1.0) continue;
            double rate = Math.Max(0, pf - st.LastPf) / dt;
            st.LastPf = pf; st.LastPfAt = now;

            long hi = Math.Max(st.Ceiling, Math.Min(tmax, (long)(st.OrigWs * 1.2)));
            if (c.BigProcMaxGrowPercent > 0 && BigFloor(st.OrigWs) > 0 && st.BaseCeiling > 0)   // big process: room back, but not all the way up
                hi = Math.Max(st.Ceiling, Math.Min(hi, st.BaseCeiling * c.BigProcMaxGrowPercent / 100));
            bool binding = ws >= st.Ceiling * 0.9;
            long next = st.Ceiling;
            bool measured = c.MeasureNeed && _hot.ContainsKey(x.Pid);
            if (measured)
            {
                // Bounded growth (as in Memory Sentry's per-role maximum): a raise never goes past twice the measured need (at least 64 MB),
                // so one noisy moment can no longer walk a ceiling back up to the size the process had before.
                long capMax = Math.Max(64 * MB, 2 * _hot[x.Pid]);
                if (c.NeedMaxMB > 0) capMax = Math.Min(capMax, 2L * c.NeedMaxMB * MB);   // never more than twice the absolute ceiling, whatever the thrash
                hi = Math.Max(st.Ceiling, Math.Min(hi, capMax));
            }
            if (x.Known) st.CpuEwma = st.CpuEwma <= 0 ? x.Cpu * 100 : st.CpuEwma * 0.9 + x.Cpu * 100 * 0.1;
            // CPU starvation (from Memory Sentry): a process that burns CPU while its resident set is a small share of its ceiling is
            // refaulting instead of working - the "8 MB renderer at 100 % CPU" case. Give it room, in steps.
            bool cpuStarved = measured && st.CpuEwma >= 50 && ws <= st.Ceiling * 0.25 && st.Ceiling < hi && !BurstApps.Contains(t.Exe);

            if (cpuStarved)
            {
                st.QuietSecs = 0;
                next = Math.Min(hi, st.Ceiling + Math.Max(8 * MB, st.Ceiling / 4));
                st.Floor = next; st.FloorUntil = DateTime.MaxValue;
                st.CpuEwma = 0;   // judge the new ceiling on fresh numbers
            }
            else if (binding && rate > faultRate && !BurstApps.Contains(t.Exe))   // burst apps always fault when opened: never learn a bigger size from that
            {
                // Faults served from RAM (soft) cost almost nothing: only give room back when the disk is really being read.
                // Soft faults cost no disk but they cost CPU: 27,000 per second on a 7 MB process is a process refaulting its own pages
                // all day. Faults per resident MB (Memory Sentry's measure) over ThrashFaultsPerMB means starved, whatever the disk does.
                bool severe = c.ThrashFaultsPerMB > 0 && rate / Math.Max(1.0, ws / (double)MB) >= c.ThrashFaultsPerMB;
                if (c.SmartFaultCheck && !severe && !PagingMonitor.IsHard(c.HardFaultPagesPerSec))
                {
                    st.QuietSecs = 0;
                    if (c.LogHolds && (!_softLogAt.TryGetValue(x.Pid, out var sl) || (now - sl).TotalSeconds >= 30))
                    {
                        _softLogAt[x.Pid] = now;
                        Logger.Write($"Soft: {t.Exe} pid {x.Pid} faults {rate:0}/s but the disk read only {PagingMonitor.Last ?? 0:0} pages/s - ceiling kept at {st.Ceiling / MB} MB.");
                    }
                    continue;
                }
                // Starting to stutter: give room back.
                st.QuietSecs = 0;
                next = Math.Min(hi, st.Ceiling + Math.Max(16 * MB, st.Ceiling / 4));
            }
            else if (rate < faultRate * 0.2)
            {
                st.QuietSecs += dt;
                if (st.QuietSecs >= probe)
                {
                    st.QuietSecs = 0;
                    if (st.Ceiling > st.BaseCeiling)
                        next = Math.Max(st.BaseCeiling, st.Ceiling - st.Ceiling / 10);
                }
            }
            else st.QuietSecs = 0;

            if (next != st.Ceiling)
            {
                long old = st.Ceiling;
                if (Native.SetCeiling(x.Pid, next))
                {
                    st.Ceiling = next;
                    if (Config.LogHolds && (!_tuneLogAt.TryGetValue(x.Pid, out var la) || (now - la).TotalSeconds >= 10))
                    {
                        _tuneLogAt[x.Pid] = now;
                        Logger.Write($"Tune: {t.Exe} pid {x.Pid} ceiling {old / MB} -> {next / MB} MB ({(next > old ? "raised" : "lowered")}, faults {rate:0}/s, disk reads {PagingMonitor.Last ?? -1:0} pages/s, ws {ws / MB} MB, started at {st.OrigWs / MB} MB).");
                    }
                }
            }
        }
    }
    readonly Dictionary<int, DateTime> _tuneLogAt = new();
    readonly Dictionary<string, DateTime> _statusAt = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, DateTime> _softLogAt = new();

    void ApplySmart()
    {
        var now = DateTime.UtcNow;
        if (!ReferenceEquals(_cfgSeen, Config))
        {
            _cfgSeen = Config;
            RestoreAll();   // lift any ceilings held under the previous settings
            _synth.Clear(); _neutral.Clear(); _groups.Clear(); _cpuPrev.Clear(); _failLogged.Clear();
            _lastSmart = DateTime.MinValue;
        }
        if ((now - _lastSmart).TotalSeconds < Math.Max(0.5, P.Scan)) return;
        _lastSmart = now;
        RetryPendingReleases();
        RefreshAudio(now);
        if (Config.SmartFaultCheck) PagingMonitor.Sample();

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
                if (pid <= 4 || pid == SelfPid) continue;             // GameRamCap never limits itself
                string name = p.ProcessName;
                if (IsProtected(name, pid) || IsException(name)) continue;
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
        foreach (var pid in _hot.Keys.Where(k => !seen.Contains(k)).ToList()) _hot.TryRemove(pid, out _);
        foreach (var pid in _resident.Keys.Where(k => !seen.Contains(k)).ToList()) _resident.TryRemove(pid, out _);
        foreach (var pid in _wokenAt.Keys.Where(k => !seen.Contains(k)).ToList()) _wokenAt.TryRemove(pid, out _);
        foreach (var pid in _tuneLogAt.Keys.Where(k => !seen.Contains(k)).ToList()) _tuneLogAt.Remove(pid);
        foreach (var pid in _softLogAt.Keys.Where(k => !seen.Contains(k)).ToList()) _softLogAt.Remove(pid);
        // Memory priority: give it back to processes that left the list (exited, or now excluded as a game).
        RestorePriority(_prio.Keys.Union(_threadPrio.Keys).Where(k => !seen.Contains(k)).ToList());
        _prioFail.RemoveWhere(k => !seen.Contains(k));
        foreach (var pid in _limited.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            // Process exited, or is now excluded (game detected): lift the ceiling; forgotten only once that worked.
            if (_limited.TryGetValue(pid, out var gst)) ReleaseAndForget(pid, gst);
        }

        // 2) Decide per app.
        foreach (var (name, allProcs) in byName)
        {
            var t = ResolveTarget(name);
            // Custom: the app's own budget. Three levels: the level's ceiling x the app's process count.
            // Recomputed every scan so it follows the process count (e.g. Chrome opening/closing tabs).
            long b = UsesLevelOnly && !t.UseOwnLimit
                ? (long)(Math.Max(64L, (long)pr.HoldMB * allProcs.Count) * MB * pr.BudgetScale)
                : (long)((t.BudgetMB > 0 ? t.BudgetMB : Math.Max(t.MinCeilingMB, t.MaxCeilingMB)) * MB * pr.BudgetScale);
            if (!_groups.TryGetValue(name, out var g))
                g = _groups[name] = new Group { Budget = b, BaseBudget = b };
            else if (g.BaseBudget != b)
            {
                // keep the feedback-loop raise (Budget/BaseBudget ratio) on top of the new base
                double ratio = g.BaseBudget > 0 ? (double)g.Budget / g.BaseBudget : 1.0;
                g.BaseBudget = b;
                g.Budget = (long)(b * ratio);
            }

            // Only the process that owns an active audio stream (the audio service of Discord/Chrome, the player
            // of Spotify) is left alone; the rest of the app is limited as usual.
            var procs = allProcs;
            if (_audioSeen.Count > 0)
            {
                var audio = allProcs.Where(x => IsAudioActive(x.Pid)).ToList();
                if (audio.Count > 0)
                {
                    foreach (var x in audio)
                    {
                        RestorePriority(new List<int> { x.Pid });
                        long audioCap = Config.AudioCapMB * MB;
                        if (audioCap > 0 && (level > 0 || g.Holding))
                        {
                            // Playing: not squeezed to its need, but not left free either (Spotify's player grows to 800 MB+ when unlimited).
                            if (x.Ws > audioCap) HoldProcess(x.Pid, audioCap, x.Ws, audioCap, true);
                            else if (_limited.TryGetValue(x.Pid, out var ast) && !ast.Releasing && ast.Ceiling < audioCap && Native.SetCeiling(x.Pid, audioCap))
                            {
                                // It was squeezed while idle and started playing: give it room now (TuneHeld skips audio processes).
                                ast.Ceiling = audioCap; ast.Floor = audioCap; ast.FloorUntil = DateTime.MaxValue;
                            }
                        }
                        else ReleasePid(x.Pid);
                    }
                    procs = allProcs.Where(x => !IsAudioActive(x.Pid)).ToList();
                    if (procs.Count == 0) { g.Pending = false; continue; }
                }
            }

            // Diagnostics (LogHolds): every 30 s, why a big app is or is not being held.
            if (Config.LogHolds && (!_statusAt.TryGetValue(name, out var sa) || (now - sa).TotalSeconds >= 30))
            {
                long tot = allProcs.Sum(x => x.Ws);
                if (tot >= 300 * MB)
                {
                    _statusAt[name] = now;
                    int heldN = allProcs.Count(x => _limited.ContainsKey(x.Pid));
                    Logger.Write($"Status: {name} {tot / MB} MB in {allProcs.Count} procs, held {heldN}, exempt={_exempt.Contains(name)}, level={level}, holding={g.Holding}, since focus {(now - g.LastFocused).TotalSeconds:0}s, biggest: "
                        + string.Join(", ", allProcs.OrderByDescending(x => x.Ws).Take(4).Select(x =>
                            $"pid {x.Pid} {x.Ws / MB} MB" + (_limited.TryGetValue(x.Pid, out var ls) ? $" cap {ls.Ceiling / MB}" : " NOT held") + (IsAudioActive(x.Pid) ? " audio" : ""))));
                }
            }

            if (_exempt.Contains(name))
            {
                g.LastFocused = now; g.Pending = false;
                if (_prio.Count > 0 || _threadPrio.Count > 0) RestorePriority(procs.Select(x => x.Pid).Where(q => _prio.ContainsKey(q) || _threadPrio.ContainsKey(q)).ToList());
                // focused: ceiling off at once (the pids are already known: no process enumeration per exempt name)
                if (g.Holding || allProcs.Any(x => _limited.ContainsKey(x.Pid))) { g.Holding = false; g.CritDone = false; ReleasePids(allProcs.Select(x => x.Pid)); }
                continue;
            }

            long total = procs.Sum(x => x.Ws);

            // RAM is plentiful again: lift the ceilings of this app (with hysteresis). If a release fails the app
            // stays "Holding" and it is retried on the next scan.
            if (g.Holding && level == 0 && freePct >= 0 &&
                freePct >= Math.Min(99, pr.Pressure + RecoverMargin) && (now - g.LastTrim).TotalSeconds >= RecoverSecs)
            {
                if (ReleasePids(allProcs.Select(x => x.Pid)))
                {
                    g.Holding = false; g.CritDone = false; g.Pending = false;
                    Logger.Write($"RAM recovered ({freePct:0}% free) - ceilings lifted from {name}.");
                }
            }

            // Keep a background app that was already trimmed from growing back (also new child processes).
            // New ceilings are only added while RAM is under pressure; with level 0 it only tunes what is already held.
            if (g.Holding)
            {
                if (level > 0) HoldPass(g, procs, t, level, now);
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
            if ((now - g.LastFocused).TotalSeconds >= pr.Grace)
            {
                LowerPriority(procs);
                LowerThreads(procs);
            }

            if (level == 0) continue;
            if ((now - g.LastFocused).TotalSeconds < pr.Grace) continue;

            // Hold mode: the intensity is the hard per-process ceiling (pr.HoldMB). Setting it also
            // squeezes the process down to that size at once, so no separate trim is needed.
            if (pr.HoldMB > 0 || Presets.IsCustom(Config))
            {
                g.Holding = true;
                HoldPass(g, procs, t, level, now);
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
            g.Holding = true;
            HoldGroup(procs, t, level);

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
