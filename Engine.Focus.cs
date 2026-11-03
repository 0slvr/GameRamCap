using System.Diagnostics;

namespace GameRamCap;

// Focus helpers used by the lab: who counts as "focused", and which fullscreen apps are games.
internal sealed partial class Engine
{
    // Apps whose visible window belongs to a different process than the one we reduce.
    static readonly Dictionary<string, string[]> DefaultAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steamwebhelper"] = new[] { "steam" },
    };

    string _focusName = "";

    // Fullscreen/borderless apps seen in front are treated as games and are NEVER touched while they run,
    // even after you Alt+Tab away.
    readonly HashSet<string> _gameNames = new(StringComparer.OrdinalIgnoreCase);
    DateTime _lastGameCheck = DateTime.MinValue;

    static bool IsRunning(string name)
    {
        Process[] ps;
        try { ps = Process.GetProcessesByName(name); } catch { return false; }
        bool running = ps.Length > 0;
        foreach (var p in ps) p.Dispose();
        return running;
    }

    void TrackFullscreenGame(string fg, DateTime now)
    {
        if (!Config.AutoDetectFullscreen) { _gameNames.Clear(); return; }

        if (fg.Length > 0 && !_gameNames.Contains(fg)
            && !Protected.Contains(fg)
            && !Config.FullscreenIgnore.Contains(fg, StringComparer.OrdinalIgnoreCase)
            && !Config.Targets.Any(t => string.Equals(t.Exe, fg, StringComparison.OrdinalIgnoreCase)))
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd != IntPtr.Zero && IsFullscreen(hwnd))
            {
                _gameNames.Add(fg);
                Logger.Write($"Fullscreen app detected: {fg} - protected (never touched) while it runs.");
            }
        }

        if ((now - _lastGameCheck).TotalSeconds >= 5)
        {
            _lastGameCheck = now;
            _gameNames.RemoveWhere(n => !IsRunning(n));
        }
    }

    static bool IsFullscreen(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var b = Screen.FromHandle(hwnd).Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    /// <summary>True when the foreground process (fg) is this app: same name, a FocusAlso name, or a built-in alias.</summary>
    bool IsFocused(string app, string fg)
    {
        if (fg.Length == 0) return false;
        if (string.Equals(app, fg, StringComparison.OrdinalIgnoreCase)) return true;
        var t = Config.Targets.FirstOrDefault(x => string.Equals(x.Exe, app, StringComparison.OrdinalIgnoreCase));
        if (t != null && t.FocusAlso.Contains(fg, StringComparer.OrdinalIgnoreCase)) return true;
        return DefaultAliases.TryGetValue(app, out var a) && a.Contains(fg, StringComparer.OrdinalIgnoreCase);
    }
}
