using System.Diagnostics;

namespace GameRamCap;

// Lab engine, split into partial files:
//   Engine.cs (core / tick) | Engine.Focus.cs (focus) | Engine.Lab.cs (the experiments)
internal sealed partial class Engine : IDisposable
{
    const long MB = 1024 * 1024;

    // Never touched, never opened.
    internal static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "audiodg", "MsMpEng", "SecurityHealthService", "GameRamCap",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "vgc", "vgtray",
        "Memory Compression", "Registry", "Secure System", "vmmem", "TextInputHost", "ctfmon", "SearchHost", "StartMenuExperienceHost",
        "ApplicationFrameHost", "RuntimeBroker", "ShellExperienceHost", "sihost", "taskhostw", "fontdrvhost", "conhost",
        "dllhost", "WmiPrvSE", "spoolsv", "WUDFHost", "dasHost", "LockApp", "LogonUI", "SystemSettings", "NisSrv",
        "MpDefenderCoreService", "SgrmBroker", "NVDisplay.Container", "nvcontainer", "RadeonSoftware", "AMDRSServ",
        "RtkAudUService64", "vmware-vmx", "VirtualBoxVM", "VBoxHeadless", "vmwp", "qemu-system-x86_64",
        "FACEITService", "FACEIT", "GameGuard", "xhunter1",
        // Shell + recording/streaming tools: trimming these causes lag or dropped frames.
        "explorer", "obs64", "obs32", "Streamlabs OBS", "NVIDIA Share", "nvsphelper64", "ShellHost", "SearchIndexer",
    };

    // The exe may be renamed (GameRamCap-v0.1.30.exe): protect our own process name too.
    static Engine()
    {
        try { Protected.Add(Process.GetCurrentProcess().ProcessName); } catch { }
    }

    public Config Config { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string Status { get; private set; } = "Idle";

    public void Tick()
    {
        if (LabOn) { LabTick(); return; }
        if (_labStarted) LabShutdown();
        Status = "Idle (no experiment running)";
    }

    /// <summary>Undo everything the running experiment did (it is re-applied on the next tick).</summary>
    public void RestoreAll()
    {
        if (_labStarted) LabRestoreAll("restore-all");
    }

    /// <summary>Lifts every hard working-set cap left on other processes (used by the watchdog after an End Task).
    /// Protected processes are skipped, so no handle is opened to them.</summary>
    public static int ReleaseAllHardCeilings()
    {
        int n = 0, self = Environment.ProcessId;
        Process[] all;
        try { all = Process.GetProcesses(); } catch { return 0; }
        foreach (var p in all)
        {
            try
            {
                if (p.Id <= 4 || p.Id == self || Protected.Contains(p.ProcessName)) continue;
                if (Native.HasHardCeiling(p.Id, out var min, out var max) && Native.Release(p.Id, min, max)) n++;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return n;
    }

    public void Dispose() { LabShutdown(); }
}
