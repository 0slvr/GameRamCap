using System.Diagnostics;

namespace GameRamCap;

// Split into partial files so each edit touches a small file:
//   Engine.cs (core/tick) | Engine.Limits.cs | Engine.Adaptive.cs | Engine.GameDetection.cs
internal sealed partial class Engine : IDisposable
{
    const long MB = 1024 * 1024;

    // Never touched, even if listed as a target.
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

    readonly Dictionary<int, Limited> _limited = new();
    DateTime _gamingSince = DateTime.MinValue;

    public Config Config { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public int LimitedCount => _limited.Count;
    public string Status { get; private set; } = "Idle";
    public string GameName { get; private set; } = "";
    /// <summary>(title, text) - shown as a tray balloon when limiting starts / stops.</summary>
    public event Action<string, string>? Notify;

    int _gamePid;
    DateTime _gameStart = DateTime.MinValue;
    bool _wasActive;
    string _lastFg = "";
    readonly HashSet<int> _failLogged = new();

    /// <summary>One engine pass. Focus mode is always on (forced in ConfigStore), so every pass goes through TickFocus,
    /// which handles the game gate (AlwaysLimit / DelaySeconds / KeepLimitsWhileGameRuns) and both Smart and Hard mode.</summary>
    public void Tick() { lock (Gate) { _pCache = null; TickFocus(); } }

    /// <summary>Tick runs on a background thread; anything on the UI thread that touches engine state
    /// (RestoreAll, Dispose, config swap) must take this lock first.</summary>
    public readonly object Gate = new();

    static T[] Safe<T>(Func<T[]> f) { try { return f(); } catch { return Array.Empty<T>(); } }
    static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

    public void Dispose() { RestoreAll(); LearnedStore.Flush(true); }
}
