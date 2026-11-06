using System.Diagnostics;

namespace GameRamCap;

/// <summary>"Start with Windows" through a Scheduled Task with highest privileges
/// (the app needs admin rights, so the normal Startup folder cannot start it silently).</summary>
internal static class Autostart
{
    const string TaskName = "GameRamCap";
    public const string MinimizedArg = "--minimized";

    static int Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return -1;
            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex) { Logger.Write("schtasks failed: " + ex.Message); return -1; }
    }

    static volatile int _cache = -1;   // -1 unknown, 0 off, 1 on

    /// <summary>Last known state without running schtasks (instant). Unknown counts as off until <see cref="RefreshAsync"/> finishes.</summary>
    public static bool Cached => _cache == 1;

    /// <summary>Asks Windows in the background (schtasks takes a moment, never call IsEnabled on the UI thread); then calls done on that thread.</summary>
    public static void RefreshAsync(Action<bool>? done = null) =>
        Task.Run(() => { bool on = IsEnabled(); done?.Invoke(on); });

    public static bool IsEnabled()
    {
        bool on = Run($"/Query /TN \"{TaskName}\"") == 0;
        _cache = on ? 1 : 0;
        return on;
    }

    public static bool Set(bool on)
    {
        bool ok = SetCore(on);
        if (ok) _cache = on ? 1 : 0;
        return ok;
    }

    static bool SetCore(bool on)
    {
        string? exe = Environment.ProcessPath;
        if (on)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            return Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" {MinimizedArg}\" /SC ONLOGON /RL HIGHEST /F") == 0;
        }
        return Run($"/Delete /TN \"{TaskName}\" /F") == 0;
    }
}
