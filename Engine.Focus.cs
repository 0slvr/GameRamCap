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

        // Names that count as "in focus" right now: the foreground app + any target it stands for.
        var focusNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (fg.Length > 0) focusNames.Add(fg);
        foreach (var t in Config.Targets)
            if (!string.IsNullOrWhiteSpace(t.Exe) && IsFocused(t, fg)) focusNames.Add(t.Exe);
        foreach (var n in TreeNames(fgPid, now)) focusNames.Add(n);

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
