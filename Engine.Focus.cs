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
    readonly Dictionary<string, (long Ceiling, int Base)> _learned = new(StringComparer.OrdinalIgnoreCase); // adaptive ceilings (+ the Limit MB they were learned from)
    string _focusName = "";
    // "Popup apps": small apps that take the foreground only briefly, again and again (Win+V, Snipping Tool, a password
    // manager popup). Learned in memory only, never written to disk or the log. They are kept out of trimming.
    string _fgPrev = ""; int _fgPrevPid; DateTime _fgPrevSince = DateTime.MinValue;
    readonly Dictionary<string, int> _briefCount = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _popupNames = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _popupAt = new(StringComparer.OrdinalIgnoreCase);   // when each popup app was learned (they expire, see CleanCaches)
    readonly Dictionary<string, DateTime> _fsLeftSince = new(StringComparer.OrdinalIgnoreCase);
    readonly object _evLock = new();   // tiny critical sections of the event handlers (never held while calling anything else)

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _prefetchAt = new(StringComparer.OrdinalIgnoreCase);

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
    bool _panelWasOpen, _panelOpenCached;
    DateTime _panelPoll = DateTime.MinValue, _cleanAt = DateTime.UtcNow;

    /// <summary>Every 10 minutes, whatever the mode and whether the game gate is open (called at the top of TickFocus, under Gate):
    /// forgets the bookkeeping of apps and processes that are gone, so no dictionary grows for days. Names of installers / updaters
    /// with random file names are what used to pile up in _groups, _synth and _neutral.</summary>
    void CleanCaches(DateTime now)
    {
        if (_briefCount.Count > 200) _briefCount.Clear();
        foreach (var kv in _prefetchAt.ToList())
            if ((now - kv.Value).TotalMinutes > 10) _prefetchAt.TryRemove(kv.Key, out _);
        foreach (var kv in _uses.ToList())
        {
            bool empty;
            lock (kv.Value)
            {
                while (kv.Value.Count > 0 && (now - kv.Value.Peek()).TotalMinutes > 30) kv.Value.Dequeue();
                empty = kv.Value.Count == 0;
            }
            if (empty) _uses.TryRemove(kv.Key, out _);
        }

        // Everything below is keyed by process id or by app name: keep only what is still running.
        var map = Native.ProcessMap();
        if (map.Count == 0) return;   // the snapshot failed: do not wipe anything on a bad reading
        var livePids = new HashSet<int>(map.Keys);
        var liveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in map.Values) liveNames.Add(v.Name);

        foreach (var k in _groups.Keys.Where(k => !liveNames.Contains(k)).ToList()) _groups.Remove(k);
        foreach (var k in _synth.Keys.Where(k => !liveNames.Contains(k)).ToList()) _synth.Remove(k);
        foreach (var k in _neutral.Keys.Where(k => !liveNames.Contains(k)).ToList()) _neutral.Remove(k);
        foreach (var k in _statusAt.Keys.Where(k => !liveNames.Contains(k)).ToList()) _statusAt.Remove(k);
        _loggedNames.RemoveWhere(k => !liveNames.Contains(k));
        _audioLogged.RemoveWhere(k => !liveNames.Contains(k));

        foreach (var k in _cpuPrev.Keys.Where(k => !livePids.Contains(k)).ToList()) _cpuPrev.Remove(k);
        foreach (var k in _tuneLogAt.Keys.Where(k => !livePids.Contains(k)).ToList()) _tuneLogAt.Remove(k);
        foreach (var k in _softLogAt.Keys.Where(k => !livePids.Contains(k)).ToList()) _softLogAt.Remove(k);
        foreach (var k in _hot.Keys.Where(k => !livePids.Contains(k)).ToList()) _hot.TryRemove(k, out _);
        foreach (var k in _resident.Keys.Where(k => !livePids.Contains(k)).ToList()) _resident.TryRemove(k, out _);
        foreach (var k in _wokenAt.Keys.Where(k => !livePids.Contains(k)).ToList()) _wokenAt.TryRemove(k, out _);
        _prioFail.RemoveWhere(k => !livePids.Contains(k));

        // Popup apps are learned from a few brief visits: they are forgotten after two hours or when the app is gone.
        foreach (var k in _popupNames.Where(k => !liveNames.Contains(k) || !_popupAt.TryGetValue(k, out var at) || (now - at).TotalHours >= 2).ToList())
        { _popupNames.Remove(k); _popupAt.Remove(k); }
        foreach (var k in _fsLeftSince.Keys.Where(k => !_gameNames.Contains(k)).ToList()) _fsLeftSince.Remove(k);

        foreach (var k in _closedFocus.Where(kv => (now - kv.Value).TotalMinutes > 10).Select(kv => kv.Key).ToList()) _closedFocus.Remove(k);

        try { Suspender.PruneDead(); } catch { }
    }

    /// <summary>Called the moment the Win+V panel takes focus: lifts TextInputHost's ceiling at once, like OnForeground does for a normal app.</summary>
    public void OnPanelOpened()
    {
        if (!Enabled || !Config.Enabled) return;
        lock (Gate)
        {
            if (_groups.TryGetValue("TextInputHost", out var g)) { g.Holding = false; g.CritDone = false; }
            if (_limited.Count > 0) ReleaseByName("TextInputHost");
        }
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
        if (Native.IsTaskbarAt(x, y)) { OnStartKeyPressed(); return; }   // click on the taskbar / Start button: wake the Start hosts just like the Win key does
        int pid = Native.PidAtPoint(x, y);
        if (pid <= 4) return;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint fpid);
        if ((uint)pid == fpid) return;                      // click inside the app already in front: nothing to wake
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
        catch { return; }
        if (name.Length == 0 || MruSkip.Contains(name)) return;   // shell / Start / UWP frame: handled elsewhere
        var now = DateTime.UtcNow;
        lock (_evLock)
        {
            if (string.Equals(name, _clickTarget, StringComparison.OrdinalIgnoreCase) && (now - _clickTargetAt).TotalSeconds < 2) return;
            _clickTarget = name; _clickTargetAt = now;
        }
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
        lock (_evLock)
        {
            if ((now - _startKeyAt).TotalSeconds < 3) return;   // key repeat, or Win+other shortcuts in a row
            _startKeyAt = now;
        }
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
        lock (_evLock)
        {
            if ((now - _panelKeyAt).TotalMilliseconds < 500) return;   // key repeat
            _panelKeyAt = now;
        }
        lock (Gate)
        {
            if (_groups.TryGetValue("TextInputHost", out var g)) { g.Holding = false; g.CritDone = false; }
            if (_limited.Count > 0) ReleaseByName("TextInputHost");
        }
        double freePct = Native.FreeRamPercent();
        if (freePct >= 0 && freePct < Config.PrefetchMinFreePercent) return;   // limit already lifted above; just do not pull more pages in
        WakeApp("TextInputHost", WakeBudget("TextInputHost"));
    }

    /// <summary>Called by Windows' foreground-change event (any thread). Lifts the limit of the new foreground app right away,
    /// then reads its trimmed pages back into RAM. The regular tick still does the full bookkeeping a moment later.</summary>
    public void OnForeground()
    {
        if (!Enabled || !Config.Enabled) return;
        var (pid, name) = ForegroundProc();
        if (pid == 0 || name.Length == 0 || string.Equals(name, "GameRamCap", StringComparison.OrdinalIgnoreCase)) return;
        string prev = Interlocked.Exchange(ref _lastFgEvent, name);
        if (!string.Equals(prev, name, StringComparison.OrdinalIgnoreCase)) RecordUse(name);
        TrackMru(name);
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
        lock (_evLock)
        {
            if (_prefetchAt.TryGetValue(name, out var last) && (now - last).TotalSeconds < (BurstApps.Contains(name) ? 3 : 20)) return;   // not again and again
            _prefetchAt[name] = now;
        }
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
            { _popupNames.Add(_fgPrev); _popupAt[_fgPrev] = now; }
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

        // Taken for a game only because it was fullscreen (not in the Games list), and now in front but NOT fullscreen for 10 s
        // (a video player left in a window, a presentation that ended): it is a normal app again. Games in the Games list stay.
        if (fg.Length > 0 && _gameNames.Contains(fg) && !Config.Games.Contains(fg, StringComparer.OrdinalIgnoreCase))
        {
            var hw = Native.GetForegroundWindow();
            if (hw != IntPtr.Zero && !IsFullscreen(hw))
            {
                if (!_fsLeftSince.TryGetValue(fg, out var since)) _fsLeftSince[fg] = now;
                else if ((now - since).TotalSeconds >= 10)
                {
                    _gameNames.Remove(fg); _fsLeftSince.Remove(fg);
                    Logger.Write($"{fg} is no longer fullscreen - treated as a normal app again.");
                }
            }
            else _fsLeftSince.Remove(fg);
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

    // A shell process in front (Explorer: desktop, taskbar, the Alt+Tab switcher; Start; Search) is NOT a root: Explorer is the parent
    // of nearly every app the user started, so walking its children would put the whole desktop "in focus" and lift every ceiling
    // each time you Alt+Tab. Parents are still walked for normal apps; a sane app has far fewer than MaxTreeChildren descendants.
    const int MaxTreeChildren = 60;

    HashSet<string> TreeNames(int pid, string fgName, DateTime now)
    {
        if (pid == 0 || ProtectedFull.Contains(fgName)) return new(StringComparer.OrdinalIgnoreCase);
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
            var childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (queue.Count > 0)
                foreach (int k in kids[queue.Dequeue()])
                    if (visited.Add(k)) { childNames.Add(map[k].Name); queue.Enqueue(k); }
            if (visited.Count - 1 <= MaxTreeChildren) set.UnionWith(childNames);   // a huge tree is a launcher / shell-like parent, not "the app in use"
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
        var cnow = DateTime.UtcNow;
        if ((cnow - _cleanAt).TotalMinutes >= 10)
        {
            _cleanAt = cnow;
            try { CleanCaches(cnow); } catch (Exception ex) { Logger.Write("Cache cleanup: " + ex.Message); }
        }
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
        bool gaming = !Config.AlwaysLimit && IsGamingOnce();
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
            NoteGateClosed(gnow);
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
        foreach (var n in TreeNames(fgPid, fg, now)) focusNames.Add(n);
        if ((now - _panelPoll).TotalMilliseconds >= 1000) { _panelPoll = now; _panelOpenCached = InputPanelOpen(); }   // it walks the threads of TextInputHost: once a second is plenty (the focus event lifts the limit at once)
        bool panelOpen = _panelOpenCached;
        if (panelOpen) focusNames.Add("TextInputHost");   // the Win+V panel counts as the app in use while it is open
        if (panelOpen != _panelWasOpen)
        {
            _panelWasOpen = panelOpen;
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
