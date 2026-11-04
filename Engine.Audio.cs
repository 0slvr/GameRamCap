using System.Diagnostics;

namespace GameRamCap;

// Two Smart-mode helpers that avoid stutter without any hard cap:
//  * Audio protection: an app that is playing sound / using the mic is left alone.
//  * Memory priority: background apps get a low Windows memory priority instead of (or besides) a ceiling.
internal sealed partial class Engine
{
    // ---------- audio protection (per process) ----------
    // Only the process that owns an active audio stream is protected, never the whole app: the audio service of
    // Discord / Chrome is small, while its renderers and GPU process can still be squeezed.
    readonly Dictionary<int, DateTime> _audioSeen = new();
    readonly HashSet<string> _audioLogged = new(StringComparer.OrdinalIgnoreCase);
    DateTime _audioPoll = DateTime.MinValue;

    void RefreshAudio(DateTime now)
    {
        if (!Config.AudioProtect) { _audioSeen.Clear(); return; }
        if ((now - _audioPoll).TotalSeconds >= 2)
        {
            _audioPoll = now;
            foreach (int pid in AudioSessions.ActivePids())
            {
                if (!_audioSeen.ContainsKey(pid))
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (_audioLogged.Add(p.ProcessName)) Logger.Write($"{p.ProcessName} (pid {pid}) owns an audio stream - that process is left alone, the rest of the app is limited.");
                    }
                    catch { }
                }
                _audioSeen[pid] = now;
            }
        }
        var grace = TimeSpan.FromSeconds(Math.Max(0, Config.AudioProtectGraceSeconds));
        foreach (var k in _audioSeen.Keys.Where(k => now - _audioSeen[k] > grace).ToList()) _audioSeen.Remove(k);
    }

    bool IsAudioActive(int pid) => _audioSeen.ContainsKey(pid);

    /// <summary>Lifts the ceiling of one process (if it still is the same process).</summary>
    void ReleasePid(int pid)
    {
        if (!_limited.TryGetValue(pid, out var st)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.StartTime == st.Start) Native.Release(pid, st.OrigMin, st.OrigMax);
        }
        catch { }
        _limited.Remove(pid);
    }

    // ---------- memory priority ----------
    readonly Dictionary<int, (DateTime Start, uint Orig)> _prio = new();
    readonly HashSet<int> _prioFail = new();

    /// <summary>Gives every process of a background app the low memory priority (remembers the original).</summary>
    void LowerPriority(List<Proc> procs)
    {
        uint want = (uint)Math.Clamp(Config.BackgroundMemoryPriority, 1, 4);
        foreach (var x in procs)
        {
            if (_prio.ContainsKey(x.Pid) || _prioFail.Contains(x.Pid)) continue;
            DateTime start;
            try { using var p = Process.GetProcessById(x.Pid); start = p.StartTime; } catch { continue; }
            if (!Native.GetMemoryPriority(x.Pid, out uint orig)) { _prioFail.Add(x.Pid); continue; }
            if (orig <= want) continue;   // the app already set an equal or lower priority itself: leave it alone
            if (!Native.SetMemoryPriority(x.Pid, want)) { _prioFail.Add(x.Pid); continue; }
            _prio[x.Pid] = (start, orig);
        }
    }

    // Thread memory priority (optional): pid -> start time of the process whose threads were lowered.
    readonly Dictionary<int, DateTime> _threadPrio = new();

    void LowerThreads(List<Proc> procs)
    {
        uint want = (uint)Math.Clamp(Config.BackgroundMemoryPriority, 1, 4);
        foreach (var x in procs)
        {
            bool has = _threadPrio.ContainsKey(x.Pid);
            if (IsAudioActive(x.Pid)) { if (has) RestoreThreads(x.Pid); continue; }   // audio owner: never touched
            if (has) continue;
            DateTime start;
            try { using var p = Process.GetProcessById(x.Pid); start = p.StartTime; } catch { continue; }
            if (Native.SetThreadMemoryPriority(x.Pid, want)) _threadPrio[x.Pid] = start;
        }
    }

    void RestoreThreads(int pid)
    {
        if (!_threadPrio.Remove(pid, out var start)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.StartTime == start) Native.SetThreadMemoryPriority(pid, 5);
        }
        catch { /* process is gone */ }
    }

    /// <summary>Puts the original memory priority back (only if it is still the same process).</summary>
    void RestorePriority(List<int> pids)
    {
        foreach (int pid in pids)
        {
            RestoreThreads(pid);
            if (!_prio.Remove(pid, out var st)) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.StartTime == st.Start) Native.SetMemoryPriority(pid, st.Orig);
            }
            catch { /* process is gone */ }
        }
    }

    void RestoreAllPriorities()
    {
        if (_prio.Count == 0 && _threadPrio.Count == 0) return;
        RestorePriority(_prio.Keys.Union(_threadPrio.Keys).ToList());
    }
}
