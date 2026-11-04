using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GameRamCap;

/// <summary>Switches Windows to the High performance power plan while a game runs and puts the previous plan back
/// afterwards. Uses powercfg.exe. The previous plan is written to powerplan.txt so a crash or End Task of GameRamCap
/// cannot leave the PC on High performance: the next start (or the watchdog) restores it.</summary>
internal static class PowerPlan
{
    const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    static readonly object Lock = new();
    static string? _original;

    static string FilePath => Path.Combine(ConfigStore.Dir, "powerplan.txt");

    static (int code, string output) Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (p == null) return (-1, "");
            string text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return (-1, text); }
            return (p.ExitCode, text);
        }
        catch { return (-1, ""); }
    }

    static string? Active()
    {
        var (code, text) = Run("/getactivescheme");
        if (code != 0) return null;
        var m = GuidRx.Match(text);
        return m.Success ? m.Value : null;
    }

    public static bool IsBoosted { get { lock (Lock) return _original != null; } }

    /// <summary>Switches to High performance. Returns false when it was already active, or the PC has no such plan.</summary>
    public static bool Boost()
    {
        lock (Lock)
        {
            if (_original != null) return true;
            var cur = Active();
            if (cur == null || string.Equals(cur, HighPerformance, StringComparison.OrdinalIgnoreCase)) return false;
            try { Directory.CreateDirectory(ConfigStore.Dir); File.WriteAllText(FilePath, cur); } catch { }
            var (code, _) = Run("/setactive " + HighPerformance);
            if (code != 0 || !string.Equals(Active(), HighPerformance, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(FilePath); } catch { }
                return false;   // this PC has no High performance plan (many laptops hide it)
            }
            _original = cur;
            return true;
        }
    }

    /// <summary>Puts the previous plan back. If you changed the plan yourself meanwhile, your choice is kept.</summary>
    public static void Restore()
    {
        lock (Lock)
        {
            if (_original == null) return;
            var cur = Active();
            if (cur != null && string.Equals(cur, HighPerformance, StringComparison.OrdinalIgnoreCase))
                Run("/setactive " + _original);
            _original = null;
            try { File.Delete(FilePath); } catch { }
        }
    }

    /// <summary>After a crash / End Task: restores the plan saved by the previous run (also used by the watchdog).</summary>
    public static void RecoverStale()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var m = GuidRx.Match(File.ReadAllText(FilePath));
            var cur = Active();
            if (m.Success && cur != null && string.Equals(cur, HighPerformance, StringComparison.OrdinalIgnoreCase))
                Run("/setactive " + m.Value);
            File.Delete(FilePath);
        }
        catch { }
    }
}
