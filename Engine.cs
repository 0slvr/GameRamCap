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
    public string Status { get; private set; } = "Idle";
    public string GameName { get; private set; } = "";
    /// <summary>(title, text) - shown as a tray balloon when limiting starts / stops.</summary>
    public event Action<string, string>? Notify;

    int _gamePid;
    DateTime _gameStart = DateTime.MinValue;
    bool _wasActive;
    string _lastFg = "";
    readonly HashSet<int> _failLogged = new();

    public void Tick()
    {
        if (Config.FocusMode || Config.IsSmart()) { TickFocus(); return; }
        bool gaming = Enabled && Config.Enabled && IsGaming();
        var now = DateTime.UtcNow;

        if (gaming) { if (_gamingSince == DateTime.MinValue) _gamingSince = now; }
        else _gamingSince = DateTime.MinValue;

        bool always = Enabled && Config.Enabled && Config.AlwaysLimit;
        bool active = always || (gaming && (now - _gamingSince).TotalSeconds >= Config.DelaySeconds);
        if (!active)
        {
            if (_wasActive)
            {
                _wasActive = false;
                Logger.Write("Stopped limiting (left the game / disabled). Original limits restored.");
                Notify?.Invoke("GameRamCap", "Limits released.");
            }
            _failLogged.Clear();
            RestoreAll();
            Status = !Enabled || !Config.Enabled ? "Disabled" : gaming ? "Game detected, waiting..." : "Idle";
            return;
        }

        bool first = !_wasActive;
        if (first)
        {
            _wasActive = true;
            Logger.Write(always ? "Always-on mode - applying limits." : $"Game active: {GameName} - applying limits.");
        }
        ApplyAndAdapt();
        if (first)
        {
            if (_limited.Count == 0)
            {
                Logger.Write("No target processes (Discord, Spotify, ...) could be limited - none running, or access denied.");
                { if (!always) Notify?.Invoke("GameRamCap", $"Game detected ({GameName}) but nothing to limit."); }
            }
            else
                Notify?.Invoke("GameRamCap", always ? $"Limiting {_limited.Count} process(es)." : $"Limiting {_limited.Count} process(es) while {GameName} runs.");
        }
        Status = $"Limiting {_limited.Count} process(es)";
    }

    static T[] Safe<T>(Func<T[]> f) { try { return f(); } catch { return Array.Empty<T>(); } }
    static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

    public void Dispose() => RestoreAll();
}
