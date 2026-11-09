using System.Diagnostics;

namespace GameRamCap;

/// <summary>Windows' memory compression (the "Memory Compression" process): turn it off or back on. It is the same switch as
/// PowerShell's Disable-MMAgent / Enable-MMAgent -MemoryCompression. Windows applies the change after a RESTART (not Shut down:
/// Fast Startup keeps the old state). It is a system setting you choose on purpose, so GameRamCap never changes it by itself
/// and never undoes it; the button on the Main tab switches it back.</summary>
internal static class MemoryCompression
{
    static string PendingFile => Path.Combine(ConfigStore.Dir, "memcomp-change.txt");

    static (int Code, string Output) Ps(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "Could not start PowerShell.");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } return (-1, "PowerShell took too long."); }
            string o = outTask.GetAwaiter().GetResult().Trim(), e = errTask.GetAwaiter().GetResult().Trim();
            return (p.ExitCode, p.ExitCode == 0 ? o : (e.Length > 0 ? e : o));
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary>The configured state (true = on). null when it cannot be read.</summary>
    public static bool? IsEnabled()
    {
        var (code, o) = Ps("(Get-MMAgent).MemoryCompression");
        if (code != 0) return null;
        return o.Equals("True", StringComparison.OrdinalIgnoreCase) ? true : o.Equals("False", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    /// <summary>true while a change was made after the last Windows start (so it is not active yet).</summary>
    public static bool RestartPending()
    {
        try
        {
            if (!File.Exists(PendingFile)) return false;
            if (!long.TryParse(File.ReadAllText(PendingFile).Trim(), out long ticks)) return false;
            var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            return new DateTime(ticks, DateTimeKind.Utc) > boot;
        }
        catch { return false; }
    }

    /// <summary>null = done (restart Windows to apply), otherwise the error text.</summary>
    public static string? Set(bool enable)
    {
        var (code, o) = Ps(enable ? "Enable-MMAgent -MemoryCompression" : "Disable-MMAgent -MemoryCompression");
        if (code != 0) { Logger.Write("Memory compression: could not change it: " + o); return o.Length > 0 ? o : "Windows refused (run GameRamCap as administrator)."; }
        try { Directory.CreateDirectory(ConfigStore.Dir); File.WriteAllText(PendingFile, DateTime.UtcNow.Ticks.ToString()); } catch { }
        Logger.Write("Memory compression: " + (enable ? "turned on" : "turned off") + " (applies after a Windows restart).");
        return null;
    }
}
