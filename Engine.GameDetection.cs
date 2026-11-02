using System.Diagnostics;

namespace GameRamCap;

internal sealed partial class Engine
{
    void Remember(int pid, DateTime start)
    {
        if (!Config.KeepLimitsWhileGameRuns) return;
        _gamePid = pid;
        _gameStart = start;
    }

    bool IsGaming()
    {
        if (Config.KeepLimitsWhileGameRuns)
        {
            // Sticky: once a game was detected, keep limiting until that process exits.
            if (_gamePid != 0)
            {
                try
                {
                    using var gp = Process.GetProcessById(_gamePid);
                    if (!gp.HasExited && Safe(() => gp.StartTime, DateTime.MinValue) == _gameStart) return true;
                }
                catch { /* process is gone */ }
                Logger.Write("Game process exited.");
                _gamePid = 0;
            }

            // A listed game that is running counts even when it is not in front.
            foreach (var g in Config.Games)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                foreach (var gp in Safe(() => Process.GetProcessesByName(g)))
                {
                    try
                    {
                        GameName = g;
                        Remember(gp.Id, Safe(() => gp.StartTime, DateTime.MinValue));
                        return true;
                    }
                    finally { gp.Dispose(); }
                }
            }
        }

        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        string name;
        DateTime fgStart;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            fgStart = Safe(() => p.StartTime, DateTime.MinValue);
        }
        catch { return false; }

        if (name != _lastFg) { _lastFg = name; Logger.Write($"Foreground app: {name}"); }

        if (Config.Games.Contains(name, StringComparer.OrdinalIgnoreCase)) { GameName = name; Remember((int)pid, fgStart); return true; }
        if (!Config.AutoDetectFullscreen) return false;
        if (Protected.Contains(name)) return false;
        if (Config.FullscreenIgnore.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        if (Config.Targets.Any(t => string.Equals(t.Exe, name, StringComparison.OrdinalIgnoreCase))) return false;

        if (IsFullscreen(hwnd)) { GameName = name + " (fullscreen)"; Remember((int)pid, fgStart); return true; }
        return false;
    }

    static bool IsFullscreen(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var b = Screen.FromHandle(hwnd).Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }
}
