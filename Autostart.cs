using System.Diagnostics;

namespace GameRamCap;

/// <summary>"Start with Windows" through a Scheduled Task with highest privileges
/// (the app needs admin rights, so the normal Startup folder cannot start it silently).</summary>
internal static class Autostart
{
    const string TaskName = "GameRamCap";

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

    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Set(bool on)
    {
        string? exe = Environment.ProcessPath;
        if (on)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            return Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F") == 0;
        }
        return Run($"/Delete /TN \"{TaskName}\" /F") == 0;
    }
}
