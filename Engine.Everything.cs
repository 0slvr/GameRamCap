using System.Diagnostics;

namespace GameRamCap;

// LimitEverything: every process (games, apps, anything) gets a ceiling unless it is protected,
// in the Exceptions list, or currently in focus. Apps listed in Targets keep their own numbers.
internal sealed partial class Engine
{
    DateTime _lastScan = DateTime.MinValue;
    string _lastExemptKey = "";
    Config? _cfgSeen;
    readonly Dictionary<string, Target> _synth = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _loggedNames = new(StringComparer.OrdinalIgnoreCase);

    // Exceptions as a set (it is asked for every process on every scan). Rebuilt when the list object or its size changes;
    // always called under Gate.
    List<string>? _excSrc; int _excCount = -1;
    HashSet<string> _excSet = new(StringComparer.OrdinalIgnoreCase);
    bool IsException(string name)
    {
        var src = Config.Exceptions;
        if (src == null) return false;
        if (!ReferenceEquals(src, _excSrc) || src.Count != _excCount)
        {
            var snap = src.ToArray();
            _excSet = new HashSet<string>(snap.Where(s => s != null), StringComparer.OrdinalIgnoreCase);
            _excSrc = src; _excCount = snap.Length;
        }
        return _excSet.Contains(name);
    }

    readonly Dictionary<string, Target> _neutral = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Smart mode with one of the three levels: the level alone decides the ceiling, so the per-app
    /// Limit/Min/Max and the Default* numbers are NOT used (only Custom and Hard mode use them).</summary>
    bool UsesLevelOnly => Config.IsSmart() && !Presets.IsCustom(Config);

    Target ResolveTarget(string name)
    {
        if (UsesLevelOnly)
        {
            foreach (var own in Config.Targets)
                if (own.UseOwnLimit && string.Equals(own.Exe, name, StringComparison.OrdinalIgnoreCase)) return own;
            if (!_neutral.TryGetValue(name, out var n))
                _neutral[name] = n = new Target { Exe = name, CeilingMB = 0, MinCeilingMB = 8, MaxCeilingMB = 100000, Adaptive = false };
            return n;
        }
        foreach (var t in Config.Targets)
            if (string.Equals(t.Exe, name, StringComparison.OrdinalIgnoreCase)) return t;
        if (!_synth.TryGetValue(name, out var s))
        {
            s = new Target
            {
                Exe = name,
                CeilingMB = Config.DefaultCeilingMB,
                MinCeilingMB = Config.DefaultMinCeilingMB,
                MaxCeilingMB = Config.DefaultMaxCeilingMB,
                Adaptive = Config.DefaultAdaptive,
            };
            _synth[name] = s;
        }
        return s;
    }

    void ApplyEverything()
    {
        if (!ReferenceEquals(_cfgSeen, Config))
        {
            _cfgSeen = Config;
            _synth.Clear();
            _failLogged.Clear();
            _lastScan = DateTime.MinValue;
        }

        // Focus changes are handled at once: lift the limit when the exempt / game names change, otherwise with the 1 s scan
        // (ReleaseByName enumerates every process, so it is not done on every tick for every name).
        var now = DateTime.UtcNow;
        string key = string.Join("|", _exempt.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                   + "#" + string.Join("|", _gameNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        bool exemptChanged = key != _lastExemptKey;
        if (!exemptChanged && (now - _lastScan).TotalSeconds < 1.0) return;
        _lastExemptKey = key;
        _lastScan = now;
        foreach (var name in _exempt.ToList()) ReleaseByName(name);
        foreach (var name in _gameNames.ToList()) ReleaseByName(name); // detected games are never capped

        var alive = new HashSet<int>();
        var all = Safe(() => Process.GetProcesses());
        bool perApp = Config.HardPerApp();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (perApp)
            foreach (var q in all) { try { counts[q.ProcessName] = counts.GetValueOrDefault(q.ProcessName) + 1; } catch { } }
        foreach (var p in all)
        {
            try
            {
                int pid = p.Id;
                if (pid <= 4 || pid == SelfPid) continue;
                alive.Add(pid);
                if (_failLogged.Contains(pid)) continue;           // access denied before: do not retry

                string name = p.ProcessName;
                if (IsProtected(name, pid) || IsException(name) || _exempt.Contains(name) || _gameNames.Contains(name)) continue;

                DateTime start = Safe(() => p.StartTime, DateTime.MinValue);
                var t = ResolveTarget(name);

                if (_limited.TryGetValue(pid, out var st) && st.Start == start)
                {
                    if (t.Adaptive && !perApp) Adapt(p, t, st);
                    continue;
                }

                if (!Native.TryGetLimits(pid, out var omin, out var omax)) { _failLogged.Add(pid); continue; }

                long min = t.MinCeilingMB * MB, max = Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB;
                long ceiling = Math.Clamp(t.CeilingMB * MB, min, max);
                if (_learned.TryGetValue(name, out var lc)) ceiling = Math.Clamp(lc, min, max);

                if (perApp) ceiling = Share(ceiling, counts.GetValueOrDefault(name, 1));
                long wsNow = Safe(() => p.WorkingSet64, 0L);
                if (!Native.SetCeiling(pid, ceiling)) { _failLogged.Add(pid); continue; }
                if (_loggedNames.Add(name))
                    Logger.Write($"Limiting {name} to {ceiling / MB} MB (was {wsNow / MB} MB).");
                var lp = new List<Proc> { new Proc { Pid = pid } };
                LowerPriority(lp);
                LowerThreads(lp);

                _limited[pid] = new Limited
                {
                    Start = start,
                    OrigMin = omin,
                    OrigMax = omax,
                    Ceiling = ceiling,
                    LastCpu = Safe(() => p.TotalProcessorTime, TimeSpan.Zero),
                };
            }
            finally { p.Dispose(); }
        }

        foreach (var pid in _limited.Keys.Where(k => !alive.Contains(k)).ToList())
            _limited.Remove(pid);
        RestorePriority(_prio.Keys.Union(_threadPrio.Keys).Where(k => !alive.Contains(k)).ToList());
    }
}
