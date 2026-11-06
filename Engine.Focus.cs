using System.Diagnostics;

namespace GameRamCap;

// Focus mode: the app you are using is never limited, everything else is (or only the Targets list,
// when LimitEverything is off).
internal sealed partial class Engine
{
    // Apps whose visible window belongs to a different process than the one we limit.
    static readonly Dictionary<string, string[]> DefaultAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steamwebhelper"] = new[] { "steam" },
    };

    readonly HashSet<string> _exempt = new(StringComparer.OrdinalIgnoreCase);          // names with no limit right now
    readonly Dictionary<string, DateTime> _exemptUntil = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _learned = new(StringComparer.OrdinalIgnoreCase); // adaptive ceilings
    string _focusName = "";
    // "Popup apps": small apps that take the foreground only briefly, again and again (Win+V, Snipping Tool, a password
    // manager popup). Learned in memory only, never written to disk or the log. They are kept out of trimming.
    string _fgPrev = ""; int _fgPrevPid; DateTime _fgPrevSince = DateTime.MinValue;
    readonly Dictionary<string, int> _briefCount = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _popupNames = new(StringComparer.OrdinalIgnoreCase);

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _prefetchAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called by Windows' foreground-change event (any thread). Lifts the limit of the new foreground app right away,
    /// then reads its trimmed pages back into RAM. The regular tick still does the full bookkeeping a moment later.</summary>
    /// <summary>Shell helpers you summon for a moment (Win+V / emoji panel, Start menu, search). They get a quick
    /// "wake up" when they appear and are trimmed again right after you leave them, instead of waiting for the normal grace time.</summary>
    internal static readonly HashSet<string> BurstApps = new(StringComparer.OrdinalIgnoreCase)
        { "TextInputHost", "StartMenuExperienceHost", "SearchHost" };
    string _lastFgEvent = "";
    // How often each app was brought to the front in the last 30 minutes: apps you come back to often pay a bigger price for being
    // squeezed (waking up from disk), so they are squeezed less. Apps you never touch keep the normal, strong numbers.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Queue<DateTime>> _uses = new(StringComparer.OrdinalIgnoreCase);
    void RecordUse(string name)
    {
        var q = _uses.GetOrAdd(name, _ => new Queue<DateTime>());
        var now = DateTime.UtcNow;
        lock (q)
        {
            if (q.Count > 0 && (now - q.Last()).TotalSeconds < 5) return;   // one use, not a flicker
            q.Enqueue(now);
            while (q.Count > 0 && (now - q.Peek()).TotalMinutes > 30) q.Dequeue();
        }
    }
    /// <summary>0 = never used lately .. up to 1.0: the extra share this app is spared. Config.WakeBoostPerUse x uses in the last 30 minutes.</summary>
    double WakeBoost(string name)
    {
        if (Config.WakeBoostPerUse <= 0 || !_uses.TryGetValue(name, out var q)) return 0;
        var now = DateTime.UtcNow;
        int n;
        lock (q) { while (q.Count > 0 && (now - q.Peek()).TotalMinutes > 30) q.Dequeue(); n = q.Count; }
        return Math.Min(1.0, n * Config.WakeBoostPerUse);
    }
    string _dbgSig = "";

    /// <summary>Diagnostics for Win+V: logs TextInputHost's windows (and which window is in front) only when they change.</summary>
    long _dbgKeyTicks;
    void DebugInputHost()
    {
        if (!Config.DebugInputHost) return;
        try
        {
            long k = Native.LastInputPanelKeyTicks;
            if (k != _dbgKeyTicks) { _dbgKeyTicks = k; Logger.Write("InputHost: Win+V/emoji/voice hotkey pressed"); }

            var parts = new List<string>();
            long wsMb = 0;
            foreach (var p in Process.GetProcessesByName("TextInputHost"))
            {
                try { parts.Add($"pid{p.Id} " + string.Join("", Native.DescribeWindows(p.Id))); wsMb = p.WorkingSet64 / MB; }
                finally { p.Dispose(); }
            }
            var fgw = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(fgw, out uint fpid);
            string fgName = "";
            try { using var fp = Process.GetProcessById((int)fpid); fgName = fp.ProcessName; } catch { }
            string sig = string.Join(" || ", parts) + $"  FOREGROUND={fgName}";   // ws is not part of it: it changes all the time
            if (sig == _dbgSig) return;
            _dbgSig = sig;
            Logger.Write("InputHost windows: " + (parts.Count == 0 ? "(TextInputHost not running)" : "") + sig + $"  ws={wsMb}MB");
        }
        catch { }
    }
    DateTime _inputHostSeen = DateTime.UtcNow, _recycleCheck = DateTime.MinValue;

    /// <summary>Closes an idle, bloated TextInputHost (never while it shows a window, and only after 30 s without it in front). Windows relaunches it on demand, small again.</summary>
    void RecycleInputHost(string fg, DateTime now)
    {
        if (string.Equals(fg, "TextInputHost", StringComparison.OrdinalIgnoreCase)) { _inputHostSeen = now; return; }
        int limit = Config.InputHostRecycleMB;
        // The windows of TextInputHost look the same open or closed and it never becomes the foreground app (see the
        // diagnostics log), so the two checks below cannot tell. The hotkey press is the signal: stay away from it
        // for InputHostIdleSeconds after the last Win+V / emoji / voice-typing press.
        long k = Native.LastInputPanelKeyTicks;
        if (k != 0) { var kd = new DateTime(k, DateTimeKind.Utc); if (kd > _inputHostSeen) _inputHostSeen = kd; }
        if (limit <= 0 || (now - _recycleCheck).TotalSeconds < 15) return;
        if ((now - _inputHostSeen).TotalSeconds < Math.Max(30, Config.InputHostIdleSeconds))
        {
            RecycleWhy($"the app started, or Win+V was pressed, less than {Math.Max(30, Config.InputHostIdleSeconds)} s ago");
            return;
        }
        _recycleCheck = now;
        foreach (var p in Process.GetProcessesByName("TextInputHost"))
        {
            try
            {
                long ws = p.WorkingSet64;
                if (ws < limit * 1024L * 1024) { RecycleWhy($"TextInputHost is under the {limit} MB limit"); continue; }
                // Fallback that does not depend on any window class name (a Windows update may rename them): any window activity of the host.
                long la = Native.LastActivityTicks(p.Id);
                if (la != 0 && (now - new DateTime(la, DateTimeKind.Utc)).TotalSeconds < Math.Max(30, Config.InputHostIdleSeconds)) { RecycleWhy($"it showed window activity less than {Math.Max(30, Config.InputHostIdleSeconds)} s ago"); return; }
                if (Native.IsInputPanelOpen(p) || Native.HasVisibleWindow(p.Id)) { RecycleWhy("the Win+V panel is open"); _inputHostSeen = now; return; }   // Win+V / emoji / keyboard is open right now
                p.Kill();
                Logger.Write($"Recycled TextInputHost (Win+V host): it was {ws / MB} MB and idle. Windows starts a fresh one when needed.");
                _recycleWhy = "";
            }
            catch (Exception ex) { RecycleWhy("closing it failed: " + ex.Message); }
            finally { p.Dispose(); }
        }
    }

    /// <summary>The Win+V panel is "the app in focus" while one of TextInputHost's threads has keyboard focus in a CoreComponentInputSource
    /// window (found with the diagnostics log; the foreground window never changes for it). Polled every tick; the focus event
    /// (see OnPanelOpened) lifts the limit even faster at the moment it opens.</summary>
    static bool InputPanelOpen()
    {
        foreach (var p in Process.GetProcessesByName("TextInputHost"))
        {
            try { if (Native.IsInputPanelOpen(p)) return true; }
            finally { p.Dispose(); }
        }
        return false;
    }
    bool _panelWasOpen;

    /// <summary>Called the moment the Win+V panel takes focus: lifts TextInputHost's ceiling at once, like OnForeground does for a normal app.</summary>
    public void OnPanelOpened()
    {
        if (!Enabled || !Config.Enabled) return;
        lock (Gate)
        {
            if (_groups.TryGetValue("TextInputHost", out var g)) { g.Holding = false; g.CritDone = false; }
            if (_limited.Count > 0) ReleaseByName("TextInputHost");
        }
        if (Config.DebugInputHost) Logger.Write("InputHost: panel opened - TextInputHost limit lifted");
    }

    // Most recently used apps (newest first), kept from the foreground events. Alt+Tab goes to the second one first, then the third, ...
    readonly List<string> _mru = new();
    readonly object _mruLock = new();
    static readonly HashSet<string> MruSkip = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "TextInputHost", "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost", "ApplicationFrameHost", "GameRamCap" };
    void TrackMru(string name)
    {
        if (MruSkip.Contains(name)) return;
        lock (_mruLock)
        {
            if (_mru.Count > 0 && string.Equals(_mru[0], name, StringComparison.OrdinalIgnoreCase)) return;
            _mru.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            _mru.Insert(0, name);
            if (_mru.Count > 6) _mru.RemoveAt(_mru.Count - 1);
        }
    }

    DateTime _altTabAt = DateTime.MinValue, _altTargetAt = DateTime.MinValue;
    int _altTabCount;
    string _altTarget = "";
    /// <summary>Called the moment Alt+Tab is pressed, BEFORE the switcher closes: the first Tab wakes the previous app, each further Tab (within
    /// 1.5 s) the next one back. Its limit is lifted and its trimmed pages are read back into RAM while you are still choosing.</summary>
    public void OnAltTabPressed()
    {
        if (!Enabled || !Config.Enabled || !Config.WakeAltTabOnKey) return;
        var now = DateTime.UtcNow;
        string target;
        lock (_mruLock)
        {
            _altTabCount = (now - _altTabAt).TotalMilliseconds < 1500 ? _altTabCount + 1 : 1;
            _altTabAt = now;
            int idx = Math.Min(_altTabCount, _mru.Count - 1);
            if (idx <= 0) return;                                   // nothing to switch to
            target = _mru[idx];
            if (string.Equals(target, _altTarget, StringComparison.OrdinalIgnoreCase) && (now - _altTargetAt).TotalSeconds < 2) return;   // key repeat
            _altTarget = target; _altTargetAt = now;
        }
        lock (Gate)
        {
            if (!_exempt.Contains(target) && !IsException(target))
            {
                if (_groups.TryGetValue(target, out var g)) { g.Holding = false; g.CritDone = false; }
                if (_limited.Count > 0) ReleaseByName(target);
            }
        }
        double freePct = Native.FreeRamPercent();
        if (freePct >= 0 && freePct < Config.PrefetchMinFreePercent) return;   // limit already lifted; just do not pull more pages in
        WakeApp(target, WakeBudget(target));
    }

    DateTime _clickTargetAt = DateTime.MinValue;
    string _clickTarget = "";
    /// <summary>Called when any mouse button (left, right, middle, X1/X2) goes down. If the click lands on a window of another app (not the one in front), that app is
    /// freed and its trimmed pages are read back into RAM while Windows is still bringing the window up.</summary>
    public void OnClickAt(int x, int y)
    {
        if (!Enabled || !Config.Enabled || !Config.WakeClickOnMouse) return;
        int pid = Native.PidAtPoint(x, y);
        if (pid <= 4) return;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint fpid);
        if ((uint)pid == fpid) return;                      // click inside the app already in front: nothing to wake
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
        catch { return; }
        if (name.Length == 0 || MruSkip.Contains(name)) return;   // shell / Start / UWP frame: handled elsewhere
        var now = DateTime.UtcNow;
        if (string.Equals(name, _clickTarget, StringComparison.OrdinalIgnoreCase) && (now - _clickTargetAt).TotalSeconds < 2) return;
        _clickTarget = name; _clickTargetAt = now;
        lock (Gate)
        {
            if (!_exempt.Contains(name) && !IsException(name))
            {
                if (_groups.TryGetValue(name, out var g)) { g.Holding = false; g.CritDone = false; }
                if (_limited.Count > 0) ReleaseByName(name);
            }
        }
        double freePct = Native.FreeRamPercent();
        if (freePct >= 0 && freePct < Config.PrefetchMinFreePercent) return;   // limit already lifted; just do not pull more pages in
        WakeApp(name, WakeBudget(name));
    }

    static readonly string[] StartHosts = { "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost" };
    DateTime _startKeyAt = DateTime.MinValue;
    /// <summary>Called the moment Win, Win+S or Win+Q is pressed, BEFORE Start / Search opens: lifts the limit of their hosts and reads their
    /// trimmed pages back into RAM in parallel with Windows creating the window (same idea as the Win+V wake-up).</summary>
    public void OnStartKeyPressed()
    {
        if (!Enabled || !Config.Enabled || !Config.WakeStartOnKey) return;
        var now = DateTime.UtcNow;
        if ((now - _startKeyAt).TotalSeconds < 3) return;   // key repeat, or Win+other shortcuts in a row
        _startKeyAt = now;
        lock (Gate)
        {
            foreach (var host in StartHosts)
            {
                if (_groups.TryGetValue(host, out var g)) { g.Holding = false; g.CritDone = false; }
                if (_limited.Count > 0) ReleaseByName(host);
            }
        }
        double freePct = Native.FreeRamPercent();
        if (freePct >= 0 && freePct < Config.PrefetchMinFreePercent) return;   // limits already lifted above; just do not pull more pages in
        foreach (var host in StartHosts) WakeApp(host, WakeBudget(host));
    }

    DateTime _panelKeyAt = DateTime.MinValue;
    /// <summary>Called the moment Win+V (or Win+. / Win+; / Win+H) is pressed, BEFORE the panel opens: lifts TextInputHost's limit and starts
    /// reading its trimmed pages back into RAM in parallel with Windows creating the window, so the panel opens without waiting for the disk.</summary>
    public void OnPanelKeyPressed()
    {
        if (!Enabled || !Config.Enabled || !Config.WakeWinVOnKey) return;
        var now = DateTime.UtcNow;
        if ((now - _panelKeyAt).TotalMilliseconds < 500) return;   // key repeat
        _panelKeyAt = now;
        lock (Gate)
        {
            if (_groups.TryGetValue("TextInputHost", out var g)) { g.Holding = false; g.CritDone = false; }
            if (_limited.Count > 0) ReleaseByName("TextInputHost");
        }
        double freePct = Native.FreeRamPercent();
        if (freePct >= 0 && freePct < Config.PrefetchMinFreePercent) return;   // limit already lifted above; just do not pull more pages in
        WakeApp("TextInputHost", WakeBudget("TextInputHost"));
        if (Config.DebugInputHost) Logger.Write("InputHost: Win+V pressed - limit lifted and pages prefetched before the panel opened");
    }

    string _recycleWhy = "";
    /// <summary>Logs (only when the reason changes) why an oversized TextInputHost was not closed yet, so the log explains a "nothing happens".</summary>
    void RecycleWhy(string why)
    {
        if (why == _recycleWhy) return;
        _recycleWhy = why;
        Logger.Write("InputHost recycler: waiting - " + why);
    }

    void TrimBurstLater(string name)
    {
        Task.Run(async () =>
        {
            foreach (int ms in new[] { 1200, 3800 })   // twice: pages touched while it was closing go back too
            {
                await Task.Delay(ms);
                if (!Enabled || !Config.Enabled) return;
                var (_, now) = ForegroundProc();
                if (string.Equals(now, name, StringComparison.OrdinalIgnoreCase)) return;   // opened again: leave it
                foreach (var kv in Native.ProcessMap())
                    if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase)) Native.Trim(kv.Key);
            }
        });
    }

    public void OnForeground()
    {
        if (!Enabled || !Config.Enabled) return;
        var (pid, name) = ForegroundProc();
        if (pid == 0 || name.Length == 0 || string.Equals(name, "GameRamCap", StringComparison.OrdinalIgnoreCase)) return;
        string prev = _lastFgEvent;
        _lastFgEvent = name;
        if (!string.Equals(prev, name, StringComparison.OrdinalIgnoreCase)) RecordUse(name);
        TrackMru(name);
        if (prev.Length > 0 && !string.Equals(prev, name, StringComparison.OrdinalIgnoreCase) && false && BurstApps.Contains(prev)) TrimBurstLater(prev);   // disabled: trimming right after use caused stutter
        lock (Gate)
        {
            if (!_exempt.Contains(name) && !IsException(name))
            {
                if (_groups.TryGetValue(name, out var g)) { g.Holding = false; g.CritDone = false; }
                if (_limited.Count > 0) ReleaseByName(name);
            }
        }
        if (!Config.PrefetchApps) return;
        var now = DateTime.UtcNow;
        if (_prefetchAt.TryGetValue(name, out var last) && (now - last).TotalSeconds < (BurstApps.Contains(name) ? 3 : 20)) return;   // not again and again
        _prefetchAt[name] = now;
        double free = Native.FreeRamPercent();
        if (free >= 0 && free < Config.PrefetchMinFreePercent) return;   // RAM is really short: do not pull more in
        WakeApp(name, WakeBudget(name));
    }

    void LearnPopupApps(int fgPid, string fg, DateTime now)
    {
        if (fg == _fgPrev) return;
        if (_fgPrev.Length > 0 && (now - _fgPrevSince).TotalSeconds < 8 && !_popupNames.Contains(_fgPrev)
            && !ProtectedFull.Contains(_fgPrev) && !IsException(_fgPrev))
        {
            int n = _briefCount.TryGetValue(_fgPrev, out var v) ? v + 1 : 1;
            _briefCount[_fgPrev] = n;
            // seen briefly 3 times, and really small (a normal app you only glance at is not a popup)
            if (n >= 3 && Native.GetMemory(_fgPrevPid, out long ws, out _) && ws < 80L * 1024 * 1024)
                _popupNames.Add(_fgPrev);
        }
        _fgPrev = fg; _fgPrevPid = fgPid; _fgPrevSince = now;
    }
    bool _gateWasOpen;
    DateTime _failReset = DateTime.UtcNow;

    // Fullscreen/borderless apps seen in front are treated as games and are NEVER trimmed or capped while they
    // run - even after you Alt+Tab away (trimming a game in the background makes it stutter when you return).
    readonly HashSet<string> _gameNames = new(StringComparer.OrdinalIgnoreCase);
    DateTime _lastGameCheck = DateTime.MinValue;

    static bool IsRunning(string name)
    {
        var ps = Safe(() => Process.GetProcessesByName(name));
        bool running = ps.Length > 0;
        foreach (var p in ps) p.Dispose();
        return running;
    }

    void TrackFullscreenGame(string fg, DateTime now)
    {
        if (!Config.AutoDetectFullscreen) { _gameNames.Clear(); return; }

        if (fg.Length > 0 && !_gameNames.Contains(fg)
            && !ProtectedFull.Contains(fg) && !IsException(fg)
            && !Config.FullscreenIgnore.Contains(fg, StringComparer.OrdinalIgnoreCase)
            && !Config.Targets.Any(t => string.Equals(t.Exe, fg, StringComparison.OrdinalIgnoreCase)))
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd != IntPtr.Zero && IsFullscreen(hwnd))
            {
                _gameNames.Add(fg);
                Logger.Write($"Fullscreen app detected: {fg} - protected from trimming while it runs.");
            }
        }

        if ((now - _lastGameCheck).TotalSeconds >= 5)
        {
            _lastGameCheck = now;
            _gameNames.RemoveWhere(n => !IsRunning(n));
        }
    }

    /// <summary>Foreground process. UWP apps live inside ApplicationFrameHost: for those the real app is the
    /// child window that belongs to a different process.</summary>
    static (int Pid, string Name) ForegroundProc()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (0, "");
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return (0, "");
            string name;
            using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName;
            if (string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                uint real = 0;
                Native.EnumChildWindows(hwnd, (child, _) =>
                {
                    Native.GetWindowThreadProcessId(child, out uint cp);
                    if (cp != 0 && cp != pid) { real = cp; return false; }
                    return true;
                }, IntPtr.Zero);
                if (real != 0)
                {
                    using var rp = Process.GetProcessById((int)real);
                    return ((int)real, rp.ProcessName);
                }
            }
            return ((int)pid, name);
        }
        catch { return (0, ""); }
    }

    // The foreground app's process tree (its launcher/parents and its children) counts as "in focus" too,
    // so a helper process of the app you are using is never capped. Cached for 2 s.
    HashSet<string> _treeNames = new(StringComparer.OrdinalIgnoreCase);
    int _treePid;
    DateTime _treeAt = DateTime.MinValue;

    HashSet<string> TreeNames(int pid, DateTime now)
    {
        if (pid == 0) return new(StringComparer.OrdinalIgnoreCase);
        if (pid == _treePid && (now - _treeAt).TotalSeconds < 2) return _treeNames;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var map = Native.ProcessMap();
            // parents: stop at system/shell processes (explorer, services...)
            int cur = pid;
            for (int i = 0; i < 6; i++)
            {
                if (!map.TryGetValue(cur, out var me) || !map.TryGetValue(me.Parent, out var par)) break;
                if (me.Parent == cur || ProtectedFull.Contains(par.Name)) break;
                set.Add(par.Name);
                cur = me.Parent;
            }
            // children (all levels)
            var kids = map.Where(kv => kv.Key != kv.Value.Parent).ToLookup(kv => kv.Value.Parent, kv => kv.Key);
            var queue = new Queue<int>(); queue.Enqueue(pid);
            var visited = new HashSet<int> { pid };
            while (queue.Count > 0)
                foreach (int k in kids[queue.Dequeue()])
                    if (visited.Add(k)) { set.Add(map[k].Name); queue.Enqueue(k); }
        }
        catch { }
        _treePid = pid; _treeAt = now; _treeNames = set;
        return set;
    }

    static bool IsFocused(Target t, string fg)
    {
        if (fg.Length == 0) return false;
        if (string.Equals(t.Exe, fg, StringComparison.OrdinalIgnoreCase)) return true;
        if (t.FocusAlso.Contains(fg, StringComparer.OrdinalIgnoreCase)) return true;
        return DefaultAliases.TryGetValue(t.Exe, out var a) && a.Contains(fg, StringComparer.OrdinalIgnoreCase);
    }

    void TickFocus()
    {
        if (!(Enabled && Config.Enabled))
        {
            if (_wasActive) { _wasActive = false; Logger.Write("Disabled - limits restored."); }
            _exempt.Clear(); _exemptUntil.Clear();
            RestoreAll();
            Status = "Disabled";
            return;
        }
        // Game gate: with "Limit all the time" off, limits only apply while a game runs and has been in front for
        // DelaySeconds. KeepLimitsWhileGameRuns keeps them on after Alt+Tab until the game process exits.
        var gnow = DateTime.UtcNow;
        bool gaming = !Config.AlwaysLimit && IsGaming();
        if (gaming) { if (_gamingSince == DateTime.MinValue) _gamingSince = gnow; }
        else _gamingSince = DateTime.MinValue;
        bool gateOpen = Config.AlwaysLimit || (gaming && (gnow - _gamingSince).TotalSeconds >= Math.Max(0, Config.DelaySeconds));
        if (!gateOpen)
        {
            if (_gateWasOpen)
            {
                _gateWasOpen = false;
                Logger.Write("No game running - limits released.");
                Notify?.Invoke("GameRamCap", "Limits released.");
            }
            _exempt.Clear(); _exemptUntil.Clear();
            RestoreAll();
            Status = gaming ? $"Game detected ({GameName}), waiting..." : "Idle - waiting for a game";
            return;
        }
        if (!_gateWasOpen)
        {
            _gateWasOpen = true;
            if (!Config.AlwaysLimit)
            {
                Logger.Write($"Game active: {GameName} - applying limits.");
                Notify?.Invoke("GameRamCap", $"Limiting while {GameName} runs.");
            }
        }
        _wasActive = true;

        var (fgPid, fg) = ForegroundProc();
        if (fg != _focusName) _focusName = fg;   // not logged: window switching would expose what the user does

        var now = DateTime.UtcNow;
        var delay = TimeSpan.FromSeconds(Math.Max(0, Config.RelimitDelaySeconds));

        // A process that was refused once (busy, transient error) gets another chance every 5 minutes.
        if ((now - _failReset).TotalMinutes >= 5) { _failReset = now; _failLogged.Clear(); }
        TrackFullscreenGame(fg, now);
        RecycleInputHost(fg, now);
        DebugInputHost();

        // Names that count as "in focus" right now: the foreground app + any target it stands for.
        var focusNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (fg.Length > 0) focusNames.Add(fg);
        if (Config.LearnPopupApps)
        {
            LearnPopupApps(fgPid, fg, now);
            foreach (var n in _popupNames) focusNames.Add(n);
        }
        foreach (var t in Config.Targets)
            if (!string.IsNullOrWhiteSpace(t.Exe) && IsFocused(t, fg)) focusNames.Add(t.Exe);
        foreach (var n in TreeNames(fgPid, now)) focusNames.Add(n);
        bool panelOpen = InputPanelOpen();
        if (panelOpen) focusNames.Add("TextInputHost");   // the Win+V panel counts as the app in use while it is open
        if (panelOpen != _panelWasOpen)
        {
            _panelWasOpen = panelOpen;
            if (Config.DebugInputHost) Logger.Write(panelOpen ? "InputHost: panel in focus" : "InputHost: panel closed - TextInputHost is a background app again");
        }
        if (Config.RecentActivitySeconds > 0)
        {
            var recent = Native.RecentlyActivePids(Config.RecentActivitySeconds);
            if (recent.Count > 0)
            {
                var pmap = Native.ProcessMap();
                foreach (int rp in recent)
                    if (pmap.TryGetValue(rp, out var rinfo)) focusNames.Add(rinfo.Name);
            }
        }

        // A newly focused app gets its ceiling lifted right now, before the (slower) scan below would do it.
        foreach (var n in focusNames)
        {
            if (_exempt.Contains(n)) continue;
            if (_groups.TryGetValue(n, out var fgGroup)) { fgGroup.Holding = false; fgGroup.CritDone = false; }
            if (_limited.Count > 0) ReleaseByName(n);
        }

        foreach (var n in focusNames) _exemptUntil[n] = DateTime.MaxValue;
        foreach (var key in _exemptUntil.Keys.ToList())
        {
            if (focusNames.Contains(key)) continue;
            var until = _exemptUntil[key];
            if (until == DateTime.MaxValue) until = now + delay;   // focus was just lost
            if (now < until) _exemptUntil[key] = until;
            else _exemptUntil.Remove(key);
        }
        _exempt.Clear();
        foreach (var k in _exemptUntil.Keys) _exempt.Add(k);

        if (Config.IsSmart())
        {
            ApplySmart();
            Status = _smartInfo + (fg.Length > 0 ? $" - {fg} in focus (free)" : "");
            return;
        }

        if (Config.LimitEverything) ApplyEverything();
        else ApplyAndAdapt();

        Status = $"Limiting {_limited.Count} process(es)" + (fg.Length > 0 ? $" - {fg} in focus (free)" : "");
    }
}
