using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameRamCap;

/// <summary>Task Manager style extras for one app: Efficiency mode. Uses documented Windows APIs.</summary>
internal static class ProcessTools
{
    const uint PROCESS_SET_INFORMATION = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    // ---------------- Efficiency mode (EcoQoS + low priority, what Task Manager does) ----------------

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    const int ProcessPowerThrottling = 4;
    const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    /// <summary>Turns Efficiency mode on or off for every process of the app. On = EcoQoS + Low priority.
    /// Off = the system decides again + Normal priority. Returns how many processes worked / failed.</summary>
    public static (int ok, int failed) SetEfficiency(string exe, bool on)
    {
        int ok = 0, failed = 0;
        Process[] list;
        try { list = Process.GetProcessesByName(exe); } catch { return (0, 0); }
        foreach (var p in list)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_SET_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) { failed++; continue; }
                var st = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = on ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,   // 0 = give control back to the system
                    StateMask = on ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
                };
                bool good = SetProcessInformation(h, ProcessPowerThrottling, ref st, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                try { p.PriorityClass = on ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal; } catch { }
                if (good) ok++; else failed++;
            }
            catch { failed++; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); p.Dispose(); }
        }
        return (ok, failed);
    }
}
