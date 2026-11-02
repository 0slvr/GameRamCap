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

    static string ForegroundName()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
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
        _wasActive = true;

        string fg = ForegroundName();
        if (fg != _focusName) { _focusName = fg; Logger.Write($"Focus: {fg}"); }

        var now = DateTime.UtcNow;
        var delay = TimeSpan.FromSeconds(Math.Max(0, Config.RelimitDelaySeconds));

        // Names that count as "in focus" right now: the foreground app + any target it stands for.
        var focusNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (fg.Length > 0) focusNames.Add(fg);
        foreach (var t in Config.Targets)
            if (!string.IsNullOrWhiteSpace(t.Exe) && IsFocused(t, fg)) focusNames.Add(t.Exe);

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
