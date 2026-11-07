using System.Diagnostics;

namespace GameRamCap;

// Wake-up: when you come back to an app, read back the pages it was really using before it was squeezed
// (its "hot set", saved just before the first trim / ceiling), instead of guessing from the address space.
internal sealed partial class Engine
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, Native.ResidentSet> _resident = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _wokenAt = new();

    bool WakeAny => Config.PrefetchApps || Config.WakeAltTabOnKey || Config.WakeClickOnMouse || Config.WakeStartOnKey || Config.WakeWinVOnKey;

    /// <summary>Saves which pages the process has resident right now. Called BEFORE a process is trimmed or capped for the first time;
    /// a snapshot taken less than 30 s ago (the measured-need trim comes a few seconds before the ceiling) is kept.</summary>
    void CaptureResident(int pid, long ws = 0)
    {
        if (!WakeAny || Config.PrefetchMaxMB <= 0) return;
        if (ws > 0 && ws < 4 * MB) return;                       // too small to matter
        if (Native.GetStartTime(pid) is not DateTime start) return;
        if (_resident.TryGetValue(pid, out var old) && old.ProcStart == start && (DateTime.UtcNow - old.Taken).TotalSeconds < 30) return;
        var set = Native.CaptureResident(pid);
        if (set == null) return;
        set.ProcStart = start;
        _resident[pid] = set;
    }

    long HotPages(int pid) => _resident.TryGetValue(pid, out var s) ? s.Pages : 0;

    /// <summary>MB read back per app: PrefetchMaxMB, up to (1 + PrefetchFrequentBoost) times more for apps you come back to often.</summary>
    long WakeBudget(string name)
    {
        double f = 1.0 + Math.Max(0, Config.PrefetchFrequentBoost) * WakeBoost(name);
        return (long)(Math.Max(1, Config.PrefetchMaxMB) * 1024L * 1024 * f);
    }

    /// <summary>Reads one process back: its saved hot set when there is one, otherwise the old address-order guess (at most once per 10 s).</summary>
    long WakeProc(int pid, long budget)
    {
        if (_resident.TryRemove(pid, out var set))
        {
            if (Native.GetStartTime(pid) is not DateTime start) return 0;
            if (start == set.ProcStart)
            {
                long n = Native.PrefetchResident(pid, set, budget);
                if (n > 0) { _wokenAt[pid] = DateTime.UtcNow; return n; }
            }
        }
        var now = DateTime.UtcNow;
        if (_wokenAt.TryGetValue(pid, out var last) && (now - last).TotalSeconds < 10) return 0;
        _wokenAt[pid] = now;
        return Native.Prefetch(pid, Math.Min(budget, Math.Max(1, Config.PrefetchMaxMB) * 1024L * 1024));
    }

    readonly SemaphoreSlim _wakeGate = new(1, 1);

    /// <summary>Wakes every process of an app within one shared budget: the ones with the biggest saved hot set first.</summary>
    long WakeApp(string name, long budget)
    {
        // One wake-up reads pages at a time: a burst of events (Alt+Tab through several apps) must not pile up disk reads next to the game.
        if (!_wakeGate.Wait(250)) return 0;
        try { return WakeAppCore(name, budget); }
        finally { _wakeGate.Release(); }
    }

    long WakeAppCore(string name, long budget)
    {
        var pids = new List<int>();
        foreach (var kv in Native.ProcessMap())
            if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase)) pids.Add(kv.Key);
        pids.Sort((a, b) => HotPages(b).CompareTo(HotPages(a)));
        long used = 0;
        foreach (int pid in pids)
        {
            if (budget - used <= 0) break;
            used += WakeProc(pid, budget - used);
        }
        return used;
    }
}
