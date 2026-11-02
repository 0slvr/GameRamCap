using System.Diagnostics;

namespace GameRamCap;

// Two Smart-mode helpers that avoid stutter without any hard cap:
//  * Audio protection: an app that is playing sound / using the mic is left alone.
//  * Memory priority: background apps get a low Windows memory priority instead of (or besides) a ceiling.
internal sealed partial class Engine
{
    // ---------- audio protection ----------
    readonly Dictionary<string, DateTime> _audioSeen = new(StringComparer.OrdinalIgnoreCase);
    DateTime _audioPoll = DateTime.MinValue;

    void RefreshAudio(DateTime now)
    {
        if (!Config.AudioProtect) { _audioSeen.Clear(); return; }
        if ((now - _audioPoll).TotalSeconds >= 2)
        {
            _audioPoll = now;
            foreach (int pid in AudioSessions.ActivePids())
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!_audioSeen.ContainsKey(p.ProcessName)) Logger.Write($"{p.ProcessName} is using audio - protected while it plays.");
                    _audioSeen[p.ProcessName] = now;
                }
                catch { }
            }
        }
        var grace = TimeSpan.FromSeconds(Math.Max(0, Config.AudioProtectGraceSeconds));
        foreach (var k in _audioSeen.Keys.Where(k => now - _audioSeen[k] > grace).ToList()) _audioSeen.Remove(k);
    }

    bool IsAudioActive(string name) => _audioSeen.ContainsKey(name);

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

    /// <summary>Puts the original memory priority back (only if it is still the same process).</summary>
    void RestorePriority(List<int> pids)
    {
        foreach (int pid in pids)
        {
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
        if (_prio.Count == 0) return;
        RestorePriority(_prio.Keys.ToList());
    }
}
