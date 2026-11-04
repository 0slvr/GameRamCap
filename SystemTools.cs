using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace GameRamCap;

/// <summary>One-click Windows tweaks shown under Settings > Advanced options > Tools.
/// Each returns a short result text. Nothing here touches game processes.</summary>
internal static class SystemTools
{
    public static string Ps(string cmd)
    {
        try
        {
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd));
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + enc)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            return (o + (string.IsNullOrWhiteSpace(e) ? "" : "\nERR: " + e)).Trim();
        }
        catch (Exception ex) { return "PowerShell error: " + ex.Message; }
    }

    public const string MmToggleMc = "$m=Get-MMAgent; if($m.MemoryCompression){Disable-MMAgent -mc; 'MemoryCompression -> OFF'}else{Enable-MMAgent -mc; 'MemoryCompression -> ON'}; 'Reboot required for the change to apply.'";
    public const string MmTogglePc = "$m=Get-MMAgent; if($m.PageCombining){Disable-MMAgent -PageCombining; 'PageCombining -> OFF'}else{Enable-MMAgent -PageCombining; 'PageCombining -> ON'}; 'Reboot required for the change to apply.'";

    /// <summary>iGPU routing = the same registry value that Settings > System > Display > Graphics writes. mode 1 = power saving (iGPU), 0 = remove the override.</summary>
    public static string GpuPref(string exe, int mode)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (mode == 1) k.SetValue(exe, "GpuPreference=1;", RegistryValueKind.String); else k.DeleteValue(exe, false);
            return mode == 1 ? $"Routed to power-saving GPU: {exe} (restart the app to apply)" : $"Override removed: {exe}";
        }
        catch (Exception ex) { return "GPU preference error: " + ex.Message; }
    }
}
