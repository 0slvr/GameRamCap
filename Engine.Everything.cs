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

    bool IsException(string name) =>
        Config.Exceptions.Contains(name, StringComparer.OrdinalIgnoreCase);

    Target ResolveTarget(string name)
    {
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

        // Focus changes are handled every tick: lift the limit at once.
        foreach (var name in _exempt) ReleaseByName(name);

        var now = DateTime.UtcNow;
        string key = string.Join("|", _exempt.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        bool exemptChanged = key != _lastExemptKey;
        if (!exemptChanged && (now - _lastScan).TotalSeconds < 1.0) return;
        _lastExemptKey = key;
        _lastScan = now;

        var alive = new HashSet<int>();
        foreach (var p in Safe(() => Process.GetProcesses()))
        {
            try
            {
                int pid = p.Id;
                if (pid <= 4) continue;
                alive.Add(pid);
                if (_failLogged.Contains(pid)) continue;           // access denied before: do not retry

                string name = p.ProcessName;
                if (Protected.Contains(name) || IsException(name) || _exempt.Contains(name)) continue;

                DateTime start = Safe(() => p.StartTime, DateTime.MinValue);
                var t = ResolveTarget(name);

                if (_limited.TryGetValue(pid, out var st) && st.Start == start)
                {
                    if (t.Adaptive) Adapt(p, t, st);
                    continue;
                }

                if (!Native.TryGetLimits(pid, out var omin, out var omax)) { _failLogged.Add(pid); continue; }

                long min = t.MinCeilingMB * MB, max = Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB;
                long ceiling = Math.Clamp(t.CeilingMB * MB, min, max);
                if (_learned.TryGetValue(name, out var lc)) ceiling = Math.Clamp(lc, min, max);

                long wsNow = Safe(() => p.WorkingSet64, 0L);
                if (!Native.SetCeiling(pid, ceiling)) { _failLogged.Add(pid); continue; }
                if (_loggedNames.Add(name))
                    Logger.Write($"Limiting {name} to {ceiling / MB} MB (was {wsNow / MB} MB).");

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
    }
}
