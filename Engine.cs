using System.Diagnostics;

namespace GameRamCap;

// Split into partial files so each edit touches a small file:
//   Engine.cs (core/tick) | Engine.Limits.cs | Engine.Adaptive.cs | Engine.GameDetection.cs
internal sealed partial class Engine : IDisposable
{
    const long MB = 1024 * 1024;

    // Never touched, even if listed as a target.
    internal static readonly HashSet<string> ProtectedFull = new(StringComparer.OrdinalIgnoreCase)
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

    // Shell helpers that may now be trimmed (they grow over time). They are still ignored for game detection
    // and for the focus-tree walk (see ProtectedFull), so the Start menu / search never counts as a game.
    internal static readonly string[] Relaxed = { "explorer", "TextInputHost" };   // TextInputHost (Win+V) is handled like a normal app: "in focus" while the panel is open (see InputPanelOpen); Start and Search stay protected

    /// <summary>What the engine refuses to trim: ProtectedFull minus the Relaxed shell helpers.</summary>
    /// <summary>Protected by default: the Windows processes that crash or log you out if they are ended or starved
    /// (blue screen, black screen, lost session). Everything else is unprotected unless the user protects it
    /// (right-click > Protect, or the Exceptions list). The user can unprotect these too (right-click > Unprotect).
    /// ProtectedFull is still used for game detection and the focus walk.</summary>
    internal static readonly HashSet<string> BaseProtected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Secure System", "Memory Compression",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm", "svchost",
        "dwm", "fontdrvhost",
    };

    /// <summary>What is protected right now: built-in list + the user's own protected apps (Config.Exceptions) - the ones the user unprotected.
    /// Replaced as a whole (never edited in place) so the engine thread can read it while the UI changes it.</summary>
    internal static volatile HashSet<string> Protected = new(BaseProtected, StringComparer.OrdinalIgnoreCase);

    internal static void RebuildProtected(Config c)
    {
        var set = new HashSet<string>(BaseProtected, StringComparer.OrdinalIgnoreCase);
        foreach (var n in c.UnprotectedBuiltIn ?? new()) set.Remove(n);
        foreach (var n in c.Exceptions ?? new()) set.Add(n);
        Protected = set;
    }

    readonly Dictionary<int, Limited> _limited = new();
    DateTime _gamingSince = DateTime.MinValue;

    Config _config = new();
    public Config Config { get => _config; set { _config = value; RebuildProtected(value); } }
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
    public void Tick()
    {
        lock (Gate)
        {
            _pCache = null;
            TickFocus();
            TickStandby();
            try { UsageMonitor.Update(Config); } catch (Exception ex) { Logger.Write("Usage monitor: " + ex.Message); }
            if (Enabled && Config.Enabled) ProcessRules.Apply(Config);   // saved priority / CPU-core rules (throttled inside)
        }
    }

    /// <summary>Tick runs on a background thread; anything on the UI thread that touches engine state
    /// (RestoreAll, Dispose, config swap) must take this lock first.</summary>
    public readonly object Gate = new();

    static T[] Safe<T>(Func<T[]> f) { try { return f(); } catch { return Array.Empty<T>(); } }
    static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

    public void Dispose() { RestoreAll(); Suspender.ResumeAll(); StandbyCleaner.Close(); }
}
