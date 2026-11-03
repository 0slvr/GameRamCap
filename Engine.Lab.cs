using System.Diagnostics;
using System.Text.Json;

namespace GameRamCap;

// =====================================================================================================
//  LAB: one experiment at a time (Config.Experiment). While a lab run is active the normal Smart/Hard
//  logic is NOT executed, so every result belongs to exactly one change.
//
//  Rules built in:
//   * Experiments touch an app ONLY while it is not focused (SetWinEventHook foreground event + polling fallback)
//     and only after UnfocusDelaySeconds. Focus back = everything is restored at once, from the hook callback.
//   * The focused app, detected fullscreen games, Config.Games, Protected (system / anti-cheat / OBS...) are never touched.
//     No handle is ever opened to them: the process list comes from NtQuerySystemInformation (no handles).
//   * Audio / mic / capture protection is automatic (WASAPI on all devices + notifications + ConsentStore).
//   * Every episode (action -> restore) is logged to %APPDATA%\GameRamCap\lab\episodes.csv.
// =====================================================================================================
internal sealed partial class Engine
{
    static readonly string[] LabKnown = { "E0", "E1", "E2s", "E2h", "E5", "E6", "E8" };

    sealed class LabApp
    {
        public string Name = "";
        public List<ProcRow> Rows = new();
        public bool Focused, Visible, WasVisible, VisLogged;
        public DateTime UnfocusedSince = DateTime.MinValue, ActUnfocusedSince = DateTime.MinValue;
        public bool Acted;
        public DateTime ActedAt, LastMaint = DateTime.MinValue, LastTrim = DateTime.MinValue;
        public long WsBefore, WsAfter; public double AvailBefore, AvailAfter; public bool AfterSampled;
        public int ProtPids;
        public int Level = 1, SafeMax = 4;
        public bool E8Skipped, ThrLogged;
        public int ThrOk, ThrFail, ThrSkip, ThrErr;
        public Dictionary<int, (long Create, uint Orig, uint Cur)> Prio = new();
        public Dictionary<int, (long Create, nuint Min, nuint Max)> Ws = new();
        public Dictionary<int, (long Create, List<(int Tid, uint Orig)> Threads)> Thr = new();
        public Dictionary<int, long> Suspended = new();
        public HashSet<string> Fail = new();
    }

    sealed class LabEpisode
    {
        public string Run = "", Exp = "", App = "", Reason = "";
        public double UnfocusedS, FocusToRestoreMs, RestoreMs, RespMs = -1, AvailDelta;
        public bool WasVisible;
        public int ProtPids, Level;
        public long WsBefore, WsAfter;
        public Dictionary<int, uint> HfBase = new();
        public DateTime Due;
        public volatile bool RespDone;
    }

    // ------------------------------------------------------------------ state
    bool _labStarted;
    string _labExp = "", _labRun = "";
    readonly Dictionary<string, LabApp> _lab = new(StringComparer.OrdinalIgnoreCase);
    readonly List<LabEpisode> _labPending = new();
    List<ProcRow> _labRows = new();
    Dictionary<int, ProcRow> _labByPid = new();
    readonly Dictionary<int, (long Ticks, DateTime At)> _labCpuPrev = new();
    DateTime _labSnap = DateTime.MinValue, _labCapAt = DateTime.MinValue, _labAppsAt = DateTime.MinValue,
             _labGpuAt = DateTime.MinValue, _labCtlAt = DateTime.MinValue, _labQuietSince = DateTime.MinValue;
    LabMetrics? _labMet; LabSys? _labSys;
    LabCsv? _labCsvSys, _labCsvApps, _labCsvEp;
    IntPtr _labHook = IntPtr.Zero; LabNative.WinEventProc? _labProc;
    int _fgPid; IntPtr _fgHwnd; string _fgName = "";
    HashSet<int> _labAudioActive = new(), _labAudioAny = new(), _labProt = new();
    readonly Dictionary<int, DateTime> _labAudioLast = new();
    readonly HashSet<string> _labAudioLogged = new(), _labCapLogged = new(), _labMismatchLogged = new();
    HashSet<string> _labCap = new(StringComparer.OrdinalIgnoreCase), _labMic = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _labVisible = new(StringComparer.OrdinalIgnoreCase);
    HashSet<int> _labCaptureNow = new();
    Dictionary<int, double> _labGpu = new(); double _labGpuAdapter = -1;
    readonly Dictionary<int, uint> _labAppsHf = new(), _labHfPrev = new();
    double _labReadsSum; int _labReadsN;
    Dictionary<string, int> _labSafe = new(StringComparer.OrdinalIgnoreCase);

    bool LabOn => Enabled && Config.Enabled && !string.IsNullOrWhiteSpace(Config.Experiment)
                  && !string.Equals(Config.Experiment.Trim(), "Off", StringComparison.OrdinalIgnoreCase);
    bool VisibleForeground => string.Equals(Config.VisibleMode, "Foreground", StringComparison.OrdinalIgnoreCase);

    static string LabCanon(string s)
    {
        s = (s ?? "").Trim();
        foreach (var k in LabKnown) if (string.Equals(k, s, StringComparison.OrdinalIgnoreCase)) return k;
        return "E0";
    }

    // ------------------------------------------------------------------ start / stop
    void LabStart(string exp)
    {
        _labExp = exp;
        _labRun = $"{exp}-{DateTime.Now:yyyyMMdd-HHmmss}";
        _labCsvSys ??= new LabCsv("system.csv", "fg,avail_mb,modified_mb,standby_mb,commit_mb,commit_limit_mb,commit_pct,pagefile_pct,page_reads_s,pages_input_s,gpu_adapter_ded_mb,reduced_apps");
        _labCsvApps ??= new LabCsv("apps.csv", "app,procs,ws_mb,private_mb,hardfaults_s,protected_hardfaults_s,audio_active_pids,audio_any_pids,capture_in_use,focused,visible,reduced,level,gpu_ded_mb");
        _labCsvEp ??= new LabCsv("episodes.csv", "app,reason,unfocused_s,delay_cfg_s,visible_mode,was_visible,protected_pids,ws_before_mb,ws_after_mb,freed_mb,avail_delta_mb,focus_to_restore_ms,restore_ms,ui_responsive_ms,hardfaults_3s,level");
        _labMet ??= new LabMetrics();
        _lab.Clear(); _labPending.Clear(); _labAudioLast.Clear(); _labAudioLogged.Clear(); _labCapLogged.Clear(); _labMismatchLogged.Clear();
        _labAppsHf.Clear(); _labHfPrev.Clear(); _labCtlAt = DateTime.MinValue; _labReadsSum = 0; _labReadsN = 0;
        _fgPid = 0;
        LabAudio.EnableNotifications = Config.LabAudioNotifications;
        if (exp == "E6") LabLoadSafe();
        if (_labHook == IntPtr.Zero)
        {
            _labProc = LabOnWinEvent;   // keep the delegate alive
            _labHook = LabNative.SetWinEventHook(0x0003 /* EVENT_SYSTEM_FOREGROUND */, 0x0003, IntPtr.Zero, _labProc, 0, 0, 0 /* OUTOFCONTEXT */);
            Logger.Write(_labHook == IntPtr.Zero ? "LAB: SetWinEventHook FAILED - using polling only." : "LAB: SetWinEventHook(EVENT_SYSTEM_FOREGROUND) installed.");
        }
        _labStarted = true;
        Logger.Write($"LAB START {exp} run={_labRun} delay={Config.UnfocusDelaySeconds}s visibleMode={Config.VisibleMode} scope={(Config.LabEverything ? "everything" : "Targets")} " +
                     $"prio={Config.LabPriorityLevel} protPrio={Config.LabProtectedPriority} folder={LabCsv.Dir}");
        if (!string.IsNullOrEmpty(_labMet.Missing)) Logger.Write("LAB: counters not available: " + _labMet.Missing);
    }

    void LabShutdown()
    {
        if (!_labStarted) return;
        LabRestoreAll("shutdown");
        LabFinishEpisodes(DateTime.UtcNow, true);
        if (_labHook != IntPtr.Zero) { LabNative.UnhookWinEvent(_labHook); _labHook = IntPtr.Zero; }
        _labStarted = false;
        Logger.Write("LAB stopped - everything restored.");
    }

    void LabRestoreAll(string reason)
    {
        foreach (var a in _lab.Values.ToList()) if (a.Acted) LabRestoreApp(a, reason, 0);
    }

    /// <summary>Resumes processes that a previous crashed run left suspended (E8). Also called by the watchdog.</summary>
    public static int LabResumeLeftovers()
    {
        int n = 0;
        try
        {
            string f = Path.Combine(ConfigStore.Dir, "lab", "suspended.txt");
            if (!File.Exists(f)) return 0;
            var rows = LabNative.Snapshot().GroupBy(r => r.Pid).ToDictionary(g => g.Key, g => g.First());
            foreach (var line in File.ReadAllLines(f))
            {
                var p = line.Split('|');
                if (p.Length == 2 && int.TryParse(p[0], out int pid) && long.TryParse(p[1], out long ct)
                    && rows.TryGetValue(pid, out var row) && row.CreateTime == ct && LabNative.Resume(pid)) n++;
            }
            File.Delete(f);
            if (n > 0) Logger.Write($"LAB: resumed {n} process(es) left suspended by a previous run.");
        }
        catch { }
        return n;
    }

    // ------------------------------------------------------------------ main tick
    void LabTick()
    {
        var now = DateTime.UtcNow;
        string want = LabCanon(Config.Experiment);
        if (!_labStarted) LabStart(want);
        else if (want != _labExp) { LabRestoreAll("experiment-switched"); LabFinishEpisodes(now, true); LabStart(want); }

        if (_labRows.Count == 0 || (now - _labSnap).TotalSeconds >= 1.0)
        {
            _labSnap = now;
            LabSnapshot(now);
            LabAudioRefresh(now);
            LabVisibleRefresh();
            LabSample(now);
        }

        // Fallback when the hook missed an event.
        var hw = Native.GetForegroundWindow();
        if (hw != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(hw, out uint fp);
            if (fp != 0 && (int)fp != _fgPid) LabFocusChanged((int)fp, hw, Stopwatch.GetTimestamp());
        }

        LabUpdateApps(now);
        LabFinishEpisodes(now, false);
        if (_labExp == "E6") LabController(now);

        int reduced = _lab.Values.Count(x => x.Acted);
        Status = $"LAB {_labExp}: {reduced}/{_lab.Count} apps reduced" + (_fgName.Length > 0 ? $" - {_fgName} in focus" : "");
    }

    // ------------------------------------------------------------------ snapshot / grouping
    bool LabInScope(string name) =>
        !Protected.Contains(name) && !IsException(name)
        && !Config.Games.Contains(name, StringComparer.OrdinalIgnoreCase) && !_gameNames.Contains(name)
        && (Config.LabEverything || Config.Targets.Any(t => string.Equals(t.Exe, name, StringComparison.OrdinalIgnoreCase)));

    void LabSnapshot(DateTime now)
    {
        var rows = LabNative.Snapshot();
        var by = new Dictionary<int, ProcRow>(rows.Count);
        foreach (var r in rows)
        {
            by[r.Pid] = r;
            if (_labCpuPrev.TryGetValue(r.Pid, out var pv) && r.CpuTicks >= pv.Ticks)
            {
                double secs = (now - pv.At).TotalSeconds;
                if (secs > 0.2) r.Cpu = (r.CpuTicks - pv.Ticks) / 1e7 / secs;
            }
            _labCpuPrev[r.Pid] = (r.CpuTicks, now);
        }
        foreach (var k in _labCpuPrev.Keys.Where(k => !by.ContainsKey(k)).ToList()) _labCpuPrev.Remove(k);
        _labRows = rows; _labByPid = by;

        foreach (var a in _lab.Values) a.Rows = new List<ProcRow>();
        foreach (var r in rows)
        {
            if (r.Pid <= 4 || r.Name.Length == 0 || !LabInScope(r.Name)) continue;
            if (!_lab.TryGetValue(r.Name, out var a)) _lab[r.Name] = a = new LabApp { Name = r.Name };
            a.Rows.Add(r);
        }
        foreach (var k in _lab.Where(kv => kv.Value.Rows.Count == 0).Select(kv => kv.Key).ToList()) _lab.Remove(k);
    }

    static string SafeName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return ""; }
    }

    bool LabIsChild(ProcRow r) =>
        _labByPid.TryGetValue(r.ParentPid, out var pr) && string.Equals(pr.Name, r.Name, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ audio / mic / capture protection
    void LabAudioRefresh(DateTime now)
    {
        var res = LabAudio.Poll();
        _labAudioActive = res.Active; _labAudioAny = res.Any; _labCaptureNow = res.Capture;
        var grace = TimeSpan.FromSeconds(Math.Max(0, Config.AudioProtectGraceSeconds));
        foreach (int pid in res.Active)
        {
            _labAudioLast[pid] = now;
            string nm = _labByPid.TryGetValue(pid, out var rw) ? rw.Name : "pid" + pid;
            bool mic = res.Capture.Contains(pid);
            if (_labAudioLogged.Add($"{nm}#{pid}#{(mic ? "mic" : "play")}"))
            {
                string parent = rw != null && _labByPid.TryGetValue(rw.ParentPid, out var pp) ? $", child of {pp.Name} (pid {pp.Pid})" : "";
                Logger.Write($"LAB audio: {nm} (pid {pid}{parent}) has an ACTIVE {(mic ? "MICROPHONE (capture)" : "playback")} stream -> protected. " +
                             $"[endpoints scanned: {res.Endpoints}, notifications registered: {LabAudio.NotifyRegistered}{(LabAudio.NotifyError.Length > 0 ? ", notify error: " + LabAudio.NotifyError : "")}]");
            }
        }
        foreach (var k in _labAudioLast.Where(kv => now - kv.Value > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList()) _labAudioLast.Remove(k);

        // protected pid set = active (+grace) pids and their same-name ancestors (Discord/Chrome: audio child -> parent chain)
        var prot = new HashSet<int>();
        foreach (var (pid, t) in _labAudioLast)
        {
            if (now - t > grace && !res.Active.Contains(pid)) continue;
            int cur = pid;
            for (int guard = 0; guard < 8 && _labByPid.TryGetValue(cur, out var row); guard++)
            {
                prot.Add(cur);
                if (!_labByPid.TryGetValue(row.ParentPid, out var par) || !string.Equals(par.Name, row.Name, StringComparison.OrdinalIgnoreCase)) break;
                cur = par.Pid;
            }
        }
        _labProt = prot;

        if ((now - _labCapAt).TotalSeconds >= 5)
        {
            _labCapAt = now;
            _labMic = LabAudio.ConsentInUse("microphone");
            var cap = new HashSet<string>(_labMic, StringComparer.OrdinalIgnoreCase);
            cap.UnionWith(LabAudio.ConsentInUse("webcam"));
            cap.UnionWith(LabAudio.ConsentInUse("graphicsCaptureProgrammatic"));
            cap.UnionWith(LabAudio.ConsentInUse("graphicsCaptureWithoutBorder"));
            cap.RemoveWhere(n => n.StartsWith("pkg:", StringComparison.Ordinal));
            _labCap = cap;
            foreach (var n in cap)
                if (_labCapLogged.Add(n)) Logger.Write($"LAB capture: {n} uses mic / camera / screen capture (Windows privacy consent store) -> whole app protected.");
            foreach (var n in _labMic.Where(x => !x.StartsWith("pkg:", StringComparison.Ordinal)))
            {
                var mine = _labRows.Where(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase)).ToList();
                if (mine.Count > 0 && !mine.Any(r => _labCaptureNow.Contains(r.Pid)) && _labMismatchLogged.Add(n))
                    Logger.Write($"LAB MISMATCH: consent store says {n} uses the microphone, but WASAPI shows no ACTIVE capture session for its processes. " +
                                 "Name-level protection keeps the whole app safe anyway.");
            }
        }
    }

    bool LabPidProtected(int pid, DateTime now) =>
        _labProt.Contains(pid) || (LabAudio.Created.TryGetValue(pid, out var c) && (now - c).TotalSeconds <= 15);

    // ------------------------------------------------------------------ visibility
    void LabVisibleRefresh()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, pid) in LabNative.VisibleWindows(_fgHwnd))
            if (_labByPid.TryGetValue(pid, out var row)) set.Add(row.Name);
        _labVisible = set;
    }

    // ------------------------------------------------------------------ focus
    void LabOnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        long ts = Stopwatch.GetTimestamp();
        try
        {
            if (hwnd == IntPtr.Zero || !_labStarted) return;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            LabFocusChanged((int)pid, hwnd, ts);
        }
        catch { }
    }

    /// <summary>Called from the WinEvent hook (instant) and from the tick fallback. Restores apps that got focus right away.</summary>
    void LabFocusChanged(int pid, IntPtr hwnd, long ts)
    {
        _fgPid = pid; _fgHwnd = hwnd;
        string name = _labByPid.TryGetValue(pid, out var row) ? row.Name : SafeName(pid);
        _fgName = name;
        if (name != _focusName) { _focusName = name; Logger.Write($"Focus: {name}"); }
        var now = DateTime.UtcNow;
        TrackFullscreenGame(name, now);

        foreach (var a in _lab.Values.ToList())
        {
            bool foc = name.Length > 0 && IsFocused(ResolveTarget(a.Name), name);
            a.Focused = foc;
            if (foc)
            {
                a.UnfocusedSince = DateTime.MinValue;
                if (a.Acted) LabRestoreApp(a, "focus", ts);
            }
            else if (a.UnfocusedSince == DateTime.MinValue) a.UnfocusedSince = now;
        }
    }

    // ------------------------------------------------------------------ per-app decisions
    void LabUpdateApps(DateTime now)
    {
        bool fgKnown = _fgName.Length > 0;
        foreach (var a in _lab.Values.ToList())
        {
            a.Focused = fgKnown && IsFocused(ResolveTarget(a.Name), _fgName);
            a.Visible = !a.Focused && _labVisible.Contains(a.Name);
            bool eff = a.Focused || (a.Visible && VisibleForeground);
            if (eff)
            {
                a.UnfocusedSince = DateTime.MinValue;
                if (a.Acted) LabRestoreApp(a, a.Focused ? "focus" : "visible-as-foreground", 0);
                if (a.Visible && !a.VisLogged)
                {
                    a.VisLogged = true;
                    Logger.Write($"LAB: {a.Name} is visible but not focused -> VisibleMode=Foreground: treated as foreground, left alone.");
                }
                continue;
            }
            a.VisLogged = false;
            if (a.UnfocusedSince == DateTime.MinValue) a.UnfocusedSince = now;
            if (!fgKnown) continue;                      // no foreground window (lock screen / desktop switch): do nothing

            double delay = _labExp == "E8" ? Math.Max(Config.UnfocusDelaySeconds, Config.LabSuspendDelaySeconds) : Config.UnfocusDelaySeconds;
            if (!a.Acted)
            {
                if ((now - a.UnfocusedSince).TotalSeconds >= Math.Max(0, delay)) LabApply(a, now);
            }
            else LabMaintain(a, now);
        }
    }

    void LabApply(LabApp a, DateTime now)
    {
        if (_labExp == "E6")
        {
            if (_labSafe.TryGetValue(a.Name, out int sm)) a.SafeMax = sm;
            a.Level = Math.Min(Math.Max(a.Level, 0), a.SafeMax);
            if (a.Level == 0 && a.SafeMax > 0) a.Level = 1;
        }
        a.Acted = true; a.ActedAt = now; a.AfterSampled = false;
        a.ActUnfocusedSince = a.UnfocusedSince;
        a.WsBefore = a.Rows.Sum(r => r.WsBytes);
        a.AvailBefore = _labSys?.AvailMB ?? -1;
        a.WasVisible = a.Visible;
        a.ProtPids = a.Rows.Count(r => LabPidProtected(r.Pid, now));
        LabApplyEffects(a, now);
        Logger.Write($"LAB {_labExp}: acting on {a.Name} ({a.Rows.Count} proc, {a.WsBefore / MB} MB working set) after {(now - a.UnfocusedSince).TotalSeconds:0}s unfocused; " +
                     $"visible={a.Visible}; protected pids={a.ProtPids}{(_labExp == "E6" ? "; level=" + a.Level : "")}.");
    }

    void LabMaintain(LabApp a, DateTime now)
    {
        if (!a.AfterSampled && (now - a.ActedAt).TotalSeconds >= 8)
        {
            a.AfterSampled = true;
            a.WsAfter = a.Rows.Sum(r => r.WsBytes);
            a.AvailAfter = _labSys?.AvailMB ?? -1;
            Logger.Write($"LAB {_labExp}: {a.Name} working set {a.WsBefore / MB} -> {a.WsAfter / MB} MB after 8s (NOT a success metric by itself - pages may only have moved to standby/modified).");
        }
        if ((now - a.LastMaint).TotalSeconds >= 2)
        {
            a.LastMaint = now;
            if (_labExp != "E8") LabApplyEffects(a, now);   // new child processes, level changes
        }
    }

    // ------------------------------------------------------------------ the experiments
    void LabApplyEffects(LabApp a, DateTime now)
    {
        string exp = _labExp;
        bool appCap = _labCap.Contains(a.Name);
        uint lowPrio = (uint)Math.Clamp(Config.LabPriorityLevel, 1, 5);
        uint protPrio = (uint)Math.Clamp(Config.LabProtectedPriority, 1, 5);
        int level = a.Level;

        foreach (var r in a.Rows)
        {
            bool prot = appCap || LabPidProtected(r.Pid, now);
            switch (exp)
            {
                case "E1":
                    LabSetPrio(a, r, prot ? protPrio : lowPrio);
                    break;
                case "E2s":
                case "E2h":
                    if (!prot) LabLimitIdleChild(a, r, exp == "E2h");     // protected: untouched (hard limits are forbidden for them)
                    break;
                case "E5":
                    if (!prot) LabThreadPrio(a, r, lowPrio);
                    break;
                case "E6":
                    if (level >= 1) LabSetPrio(a, r, prot ? protPrio : (level == 1 ? 3u : lowPrio));
                    if (!prot && level >= 4) LabLimitIdleChild(a, r, false);   // soft only, never hard, in the adaptive controller
                    break;
            }
        }

        if (exp == "E6" && level >= 3 && (now - a.LastTrim).TotalSeconds >= 60)
        {
            a.LastTrim = now;
            foreach (var r in a.Rows)
                if (!(appCap || LabPidProtected(r.Pid, now)) && r.Cpu >= 0 && r.Cpu < Config.LabIdleCpuPercent / 100.0 && r.WsBytes >= 32 * MB)
                    Native.Trim(r.Pid);
        }
        if (exp == "E8" && a.Suspended.Count == 0 && !a.E8Skipped) LabTrySuspend(a, now);
    }

    void LabSetPrio(LabApp a, ProcRow r, uint level)
    {
        if (a.Fail.Contains("prio" + r.Pid)) return;
        if (a.Prio.TryGetValue(r.Pid, out var st))
        {
            if (st.Create == r.CreateTime && st.Cur != level && Native.SetMemoryPriority(r.Pid, level))
                a.Prio[r.Pid] = (st.Create, st.Orig, level);
            return;
        }
        if (!Native.GetMemoryPriority(r.Pid, out uint orig) || !Native.SetMemoryPriority(r.Pid, level)) { a.Fail.Add("prio" + r.Pid); return; }
        a.Prio[r.Pid] = (r.CreateTime, orig, level);
    }

    /// <summary>E2: only IDLE CHILD processes (same-name parent, < LabIdleCpuPercent CPU, >= 32 MB).
    /// soft = SetProcessWorkingSetSizeEx without HARDWS flag (documented: only enforced under memory pressure);
    /// hard = QUOTA_LIMITS_HARDWS_MAX_ENABLE (enforced always, can cause page-fault storms).</summary>
    void LabLimitIdleChild(LabApp a, ProcRow r, bool hard)
    {
        if (a.Ws.ContainsKey(r.Pid) || a.Fail.Contains("ws" + r.Pid)) return;
        if (!LabIsChild(r) || r.Cpu < 0 || r.Cpu >= Config.LabIdleCpuPercent / 100.0 || r.WsBytes < 32 * MB) return;
        if (!Native.TryGetLimits(r.Pid, out var omin, out var omax)) { a.Fail.Add("ws" + r.Pid); return; }
        long cap = Math.Max(24 * MB, r.WsBytes * Math.Clamp(Config.LabWsKeepPercent, 10, 100) / 100);
        bool ok = hard ? Native.SetCeiling(r.Pid, cap) : LabNative.SetSoftMax(r.Pid, cap);
        if (ok) a.Ws[r.Pid] = (r.CreateTime, omin, omax); else a.Fail.Add("ws" + r.Pid);
    }

    static bool LabLooksAudio(string d)
    {
        if (string.IsNullOrEmpty(d)) return false;
        foreach (var k in new[] { "audio", "wasapi", "render", "capture", "voice", "rtc", "microphone", "mixer" })
            if (d.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>E5: lower the memory priority of the THREADS of an unfocused, non-protected process, except threads whose
    /// name looks like audio. Counts OK / failed / skipped and the last Win32 error: this answers
    /// "does SetThreadInformation work on another process' threads?".</summary>
    void LabThreadPrio(LabApp a, ProcRow r, uint level)
    {
        if (a.Thr.ContainsKey(r.Pid) || a.Fail.Contains("thr" + r.Pid)) return;
        var done = new List<(int Tid, uint Orig)>();
        int ok = 0, fail = 0, skip = 0, lastErr = 0;
        foreach (int tid in LabNative.ThreadIds(r.Pid))
        {
            if (!LabNative.GetThreadMemPrio(tid, out uint orig, out int err, out string desc)) { fail++; lastErr = err; continue; }
            if (LabLooksAudio(desc)) { skip++; continue; }
            if (orig <= level) continue;
            if (LabNative.SetThreadMemPrio(tid, level, out err)) { ok++; done.Add((tid, orig)); }
            else { fail++; lastErr = err; }
        }
        a.Thr[r.Pid] = (r.CreateTime, done);
        a.ThrOk += ok; a.ThrFail += fail; a.ThrSkip += skip; if (lastErr != 0) a.ThrErr = lastErr;
        if (!a.ThrLogged && (ok + fail + skip) > 0)
        {
            a.ThrLogged = true;
            Logger.Write($"LAB E5: {a.Name} pid {r.Pid}: SetThreadInformation OK on {ok} thread(s), failed on {fail} (last Win32 error {lastErr}; 5 = access denied), " +
                         $"skipped {skip} thread(s) with audio-like names.");
        }
    }

    /// <summary>E8 (RISKY): suspend + trim a SILENT app. Silent = in Targets, not visible, no mic/camera/capture,
    /// and none of its processes owns ANY audio session (even paused). Breaks the app's network connections.</summary>
    void LabTrySuspend(LabApp a, DateTime now)
    {
        string? why = null;
        if (!Config.Targets.Any(t => string.Equals(t.Exe, a.Name, StringComparison.OrdinalIgnoreCase))) why = "not in Targets";
        else if (a.Visible || a.Focused) why = "window visible";
        else if (_labCap.Contains(a.Name)) why = "mic/camera/capture in use";
        else if (a.Rows.Any(r => _labAudioAny.Contains(r.Pid) || LabPidProtected(r.Pid, now))) why = "owns an audio session (not silent)";
        if (why != null)
        {
            a.E8Skipped = true;
            Logger.Write($"LAB E8: {a.Name} NOT suspended: {why}.");
            return;
        }
        foreach (var r in a.Rows)
            if (LabNative.Suspend(r.Pid)) a.Suspended[r.Pid] = r.CreateTime;
        LabSaveSuspended();
        foreach (var pid in a.Suspended.Keys) Native.Trim(pid);
        Logger.Write($"LAB E8: suspended {a.Suspended.Count}/{a.Rows.Count} process(es) of {a.Name} and trimmed them. They resume when the app gets focus or the tool exits.");
    }

    void LabSaveSuspended()
    {
        try
        {
            string f = Path.Combine(LabCsv.Dir, "suspended.txt");
            var lines = _lab.Values.SelectMany(x => x.Suspended.Select(kv => $"{kv.Key}|{kv.Value}")).ToList();
            if (lines.Count == 0) { if (File.Exists(f)) File.Delete(f); }
            else File.WriteAllLines(f, lines);
        }
        catch { }
    }

    bool LabSame(int pid, long create) => _labByPid.TryGetValue(pid, out var row) && row.CreateTime == create;

    /// <summary>Undo everything done to an app (resume first, then priority, working set, threads).</summary>
    void LabUndoEffects(LabApp a)
    {
        foreach (var (pid, create) in a.Suspended.ToList())
            if (!_labByPid.TryGetValue(pid, out var row) || row.CreateTime == create) LabNative.Resume(pid);
        if (a.Suspended.Count > 0) { a.Suspended.Clear(); LabSaveSuspended(); }
        foreach (var (pid, st) in a.Prio) if (LabSame(pid, st.Create)) Native.SetMemoryPriority(pid, st.Orig);
        a.Prio.Clear();
        foreach (var (pid, st) in a.Ws) if (LabSame(pid, st.Create)) Native.Release(pid, st.Min, st.Max);
        a.Ws.Clear();
        foreach (var (pid, st) in a.Thr)
            if (LabSame(pid, st.Create))
                foreach (var (tid, orig) in st.Threads) LabNative.SetThreadMemPrio(tid, orig, out _);
        a.Thr.Clear();
        a.Fail.Clear(); a.E8Skipped = false;
    }

    // ------------------------------------------------------------------ restore + episode log
    void LabRestoreApp(LabApp a, string reason, long ts)
    {
        if (!a.Acted) return;
        var now = DateTime.UtcNow;
        double toRestoreMs = ts != 0 ? (Stopwatch.GetTimestamp() - ts) * 1000.0 / Stopwatch.Frequency : 0;
        var sw = Stopwatch.StartNew();
        LabUndoEffects(a);
        double restoreMs = sw.Elapsed.TotalMilliseconds;
        a.Acted = false;

        long wsNow = a.Rows.Sum(r => r.WsBytes);
        var ep = new LabEpisode
        {
            Run = _labRun, Exp = _labExp, App = a.Name, Reason = reason,
            UnfocusedS = (now - a.ActUnfocusedSince).TotalSeconds,
            FocusToRestoreMs = toRestoreMs, RestoreMs = restoreMs,
            WasVisible = a.WasVisible, ProtPids = a.ProtPids, Level = a.Level,
            WsBefore = a.WsBefore, WsAfter = a.AfterSampled ? a.WsAfter : wsNow,
            AvailDelta = (a.AfterSampled ? a.AvailAfter : (_labSys?.AvailMB ?? -1)) - a.AvailBefore,
            Due = now.AddSeconds(3),
        };
        foreach (var r in a.Rows) ep.HfBase[r.Pid] = r.HardFaults;
        _labPending.Add(ep);

        if (ts != 0 && reason == "focus")
        {
            IntPtr target = IntPtr.Zero;
            if (_fgHwnd != IntPtr.Zero && a.Rows.Any(r => r.Pid == _fgPid)) target = _fgHwnd;
            else target = LabNative.VisibleWindows(IntPtr.Zero).FirstOrDefault(w => a.Rows.Any(r => r.Pid == w.Pid)).Hwnd;
            Task.Run(() =>
            {
                try { ep.RespMs = LabNative.WaitResponsive(target, ts, 5000); } catch { }
                ep.RespDone = true;
            });
        }
        else ep.RespDone = true;

        Logger.Write($"LAB {_labExp}: restored {a.Name} ({reason}) after {ep.UnfocusedS:0}s unfocused; restore took {restoreMs:0.0} ms" +
                     (ts != 0 ? $", started {toRestoreMs:0.0} ms after the focus event." : "."));
    }

    void LabFinishEpisodes(DateTime now, bool force)
    {
        for (int i = _labPending.Count - 1; i >= 0; i--)
        {
            var ep = _labPending[i];
            if (!force && !(ep.RespDone && now >= ep.Due)) continue;
            double hf = 0;
            foreach (var kv in ep.HfBase)
                if (_labByPid.TryGetValue(kv.Key, out var row)) hf += Math.Max(0, (long)row.HardFaults - (long)kv.Value);
            double mbBefore = ep.WsBefore / 1048576.0, mbAfter = ep.WsAfter / 1048576.0;
            _labCsvEp?.Row(ep.Run, ep.Exp, ep.App, ep.Reason, ep.UnfocusedS, Config.UnfocusDelaySeconds, Config.VisibleMode, ep.WasVisible,
                           ep.ProtPids, mbBefore, mbAfter, mbBefore - mbAfter, ep.AvailDelta, ep.FocusToRestoreMs, ep.RestoreMs, ep.RespMs, hf, ep.Level);
            _labPending.RemoveAt(i);
        }
    }

    // ------------------------------------------------------------------ metrics -> CSV
    void LabSample(DateTime now)
    {
        if (_labMet == null) return;
        var s = _labMet.Read();
        _labSys = s;
        if (s.ReadsPerSec >= 0) { _labReadsSum += s.ReadsPerSec; _labReadsN++; }
        if ((now - _labGpuAt).TotalSeconds >= 10)
        {
            _labGpuAt = now;
            _labGpu = _labMet.GpuByPid();
            _labGpuAdapter = _labMet.GpuAdapterMB();
        }
        int reduced = _lab.Values.Count(x => x.Acted);
        double commitPct = s.CommitLimitMB > 0 ? s.CommitMB / s.CommitLimitMB * 100 : -1;
        _labCsvSys?.Row(_labRun, _labExp, _fgName, s.AvailMB, s.ModifiedMB, s.StandbyMB, s.CommitMB, s.CommitLimitMB, commitPct,
                        s.PagefilePct, s.ReadsPerSec, s.InputPerSec, _labGpuAdapter, reduced);

        if ((now - _labAppsAt).TotalSeconds < 5) return;
        double dt = _labAppsAt == DateTime.MinValue ? 5 : (now - _labAppsAt).TotalSeconds;
        bool first = _labAppsAt == DateTime.MinValue;
        _labAppsAt = now;
        foreach (var a in _lab.Values)
        {
            double ws = a.Rows.Sum(r => r.WsBytes) / 1048576.0;
            int protN = a.Rows.Count(r => LabPidProtected(r.Pid, now));
            if (ws < 20 && protN == 0) continue;
            double hf = 0, hfProt = 0;
            foreach (var r in a.Rows)
            {
                if (_labAppsHf.TryGetValue(r.Pid, out var prev) && !first)
                {
                    double d = Math.Max(0, (long)r.HardFaults - (long)prev) / dt;
                    hf += d;
                    if (LabPidProtected(r.Pid, now)) hfProt += d;
                }
            }
            double gpu = 0; bool hasGpu = false;
            foreach (var r in a.Rows) if (_labGpu.TryGetValue(r.Pid, out var g)) { gpu += g; hasGpu = true; }
            _labCsvApps?.Row(_labRun, _labExp, a.Name, a.Rows.Count, ws, a.Rows.Sum(r => r.PrivateBytes) / 1048576.0, hf, hfProt,
                             a.Rows.Count(r => _labAudioActive.Contains(r.Pid)), a.Rows.Count(r => _labAudioAny.Contains(r.Pid)),
                             _labCap.Contains(a.Name), a.Focused, a.Visible, a.Acted, a.Level, hasGpu ? gpu : -1);
        }
        _labAppsHf.Clear();
        foreach (var r in _labRows) _labAppsHf[r.Pid] = r.HardFaults;
    }

    // ------------------------------------------------------------------ E6: adaptive controller
    string LabSafeFile => Path.Combine(LabCsv.Dir, "safe.json");

    void LabLoadSafe()
    {
        try
        {
            if (File.Exists(LabSafeFile))
                _labSafe = new Dictionary<string, int>(JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(LabSafeFile)) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch { _labSafe = new(StringComparer.OrdinalIgnoreCase); }
    }

    void LabSaveSafe()
    {
        try { File.WriteAllText(LabSafeFile, JsonSerializer.Serialize(_labSafe, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    void LabSetLevel(LabApp a, int level, DateTime now, string why)
    {
        LabUndoEffects(a);
        a.Level = Math.Clamp(level, 0, 4);
        LabApplyEffects(a, now);
        Logger.Write($"LAB E6: {a.Name} level -> {a.Level} ({why}); safe max remembered = {a.SafeMax}.");
    }

    /// <summary>Levels: 0 nothing | 1 priority 3 | 2 priority LabPriorityLevel | 3 + trim idle processes once a minute | 4 + soft WS limit on idle children.
    /// Every 10 s: if system Page Reads/sec (average) is above LabPageReadsLimit, or an audio/mic process of the app
    /// shows hard faults above LabAudioHardFaultLimit, the app is loosened one level and the level below is remembered
    /// as its safe maximum (lab\safe.json). After LabControllerQuietSeconds without problems it tightens one step.</summary>
    void LabController(DateTime now)
    {
        if (_labCtlAt == DateTime.MinValue) { _labCtlAt = now; _labQuietSince = now; foreach (var r in _labRows) _labHfPrev[r.Pid] = r.HardFaults; return; }
        double dt = (now - _labCtlAt).TotalSeconds;
        if (dt < 10) return;
        _labCtlAt = now;

        double reads = _labReadsN > 0 ? _labReadsSum / _labReadsN : 0;
        _labReadsSum = 0; _labReadsN = 0;
        bool sysBad = reads > Config.LabPageReadsLimit;
        bool anyBad = sysBad;

        foreach (var a in _lab.Values.Where(x => x.Acted).ToList())
        {
            double prot = 0;
            foreach (var r in a.Rows)
                if (LabPidProtected(r.Pid, now) && _labHfPrev.TryGetValue(r.Pid, out var hv))
                    prot += Math.Max(0, (long)r.HardFaults - (long)hv) / dt;
            bool appBad = prot > Config.LabAudioHardFaultLimit;
            if (!(sysBad || appBad)) continue;
            anyBad = true;
            if (a.Level > 0)
            {
                a.SafeMax = a.Level - 1;
                _labSafe[a.Name] = a.SafeMax;
                LabSaveSafe();
                LabSetLevel(a, a.Level - 1, now, appBad ? $"audio/mic process hard faults {prot:0.0}/s" : $"Page Reads/sec avg {reads:0}");
            }
        }
        _labHfPrev.Clear();
        foreach (var r in _labRows) _labHfPrev[r.Pid] = r.HardFaults;

        if (anyBad) { _labQuietSince = now; return; }
        if ((now - _labQuietSince).TotalSeconds >= Math.Max(10, Config.LabControllerQuietSeconds))
        {
            _labQuietSince = now;
            foreach (var a in _lab.Values.Where(x => x.Acted && x.Level < x.SafeMax).ToList())
                LabSetLevel(a, a.Level + 1, now, $"quiet for {Config.LabControllerQuietSeconds}s (Page Reads/sec avg {reads:0})");
        }
    }
}
