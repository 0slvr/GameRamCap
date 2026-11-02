using System.Text.Json;

namespace GameRamCap;

internal sealed class Target
{
    /// <summary>Process name without ".exe" (e.g. "Discord").</summary>
    public string Exe { get; set; } = "";
    public int CeilingMB { get; set; } = 200;
    public int MinCeilingMB { get; set; } = 64;
    public int MaxCeilingMB { get; set; } = 600;
    /// <summary>Raise the ceiling when the app is busy, lower it when idle.</summary>
    public bool Adaptive { get; set; } = true;
    /// <summary>Extra process names that count as "this app is focused" (e.g. steamwebhelper -> steam).</summary>
    public List<string> FocusAlso { get; set; } = new();
    /// <summary>Smart mode: total RAM (all processes of this app together) it may keep while in the background.
    /// 0 = use MaxCeilingMB.</summary>
    public int BudgetMB { get; set; } = 0;
}

/// <summary>User edits of one intensity level. A null field means "use the built-in value".</summary>
internal sealed class PresetOverride
{
    public double? Scan { get; set; }
    public int? HoldMB { get; set; }
    public double? Cooldown { get; set; }
    public double? Grace { get; set; }
    public double? BudgetScale { get; set; }
    public int? MinProcMB { get; set; }
    public double? CritFactor { get; set; }
}

internal sealed class Config
{
    /// <summary>Edits of the five intensity levels, keyed by level name (UltraPerformance ... UltraSafe).</summary>
    public Dictionary<string, PresetOverride> PresetOverrides { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>"Smart" (default): no hard caps. Background apps are trimmed gently, only when Windows is short on RAM,
    /// per app (all its processes together), and the budget adapts if the app fights back.
    /// "Hard": the old fixed per-process hard ceilings.</summary>
    public string Mode { get; set; } = "Smart";
    /// <summary>Smart mode: keep background apps within their budget ALL the time, even when plenty of RAM is free.
    /// false = only act under memory pressure (PressureFreePercent).</summary>
    public bool LimitWhenRamFree { get; set; } = false;
    /// <summary>Smart mode: with LimitWhenRamFree off, start trimming when available RAM drops below this percent.</summary>
    public int PressureFreePercent { get; set; } = 25;
    /// <summary>Smart mode: below this percent trimming gets stronger (budget halved).</summary>
    public int CriticalFreePercent { get; set; } = 10;
    /// <summary>Smart mode: how often the RAM state is checked.</summary>
    public double SmartScanSeconds { get; set; } = 2;
    /// <summary>Smart mode: minimum seconds between two trims of the same app (doubles when the app fights back).</summary>
    public double TrimCooldownSeconds { get; set; } = 30;
    /// <summary>Smart mode: an app you just left is not touched for this many seconds (quick Alt+Tab back is instant).</summary>
    public double GraceAfterFocusSeconds { get; set; } = 5;
    /// <summary>Smart mode intensity: UltraPerformance, Performance, Balanced, Safe, UltraSafe, or Custom
    /// (Custom uses the individual numbers above; the five profiles override them).</summary>
    public string Profile { get; set; } = "Balanced";
    /// <summary>Smart mode: once a background app has been trimmed, keep it from growing back with a hard ceiling
    /// (its target CeilingMB per process) until you focus it again. Focusing the app lifts the ceiling at once.
    /// false = pure soft trimming (the app may re-grow between trims).</summary>
    public bool HoldBackground { get; set; } = true;
    public bool IsSmart() => string.Equals(Mode, "Smart", StringComparison.OrdinalIgnoreCase);
    /// <summary>Focus mode: a target app is unlimited while it is the foreground app and limited again
    /// as soon as you click anywhere else. Works for every app in Targets, no game needed.</summary>
    public bool FocusMode { get; set; } = true;
    /// <summary>Limit EVERY app (games included) except the focused one and the Exceptions list.
    /// Apps not in Targets get the Default* values below; apps in Targets keep their own numbers.</summary>
    public bool LimitEverything { get; set; } = true;
    public int DefaultCeilingMB { get; set; } = 300;
    public int DefaultMinCeilingMB { get; set; } = 100;
    public int DefaultMaxCeilingMB { get; set; } = 1500;
    public bool DefaultAdaptive { get; set; } = true;
    /// <summary>Process names (no ".exe") that are NEVER limited, whatever else is set.</summary>
    public List<string> Exceptions { get; set; } = new();
    /// <summary>Seconds to wait after leaving a target app before its limit comes back (avoids flicker on Alt+Tab).</summary>
    public double RelimitDelaySeconds { get; set; } = 1.5;
    /// <summary>Limit the target apps all the time (from the moment they start), not only while a game runs.</summary>
    public bool AlwaysLimit { get; set; } = true;
    /// <summary>Seconds a game must stay in the foreground before limits are applied.</summary>
    public int DelaySeconds { get; set; } = 5;
    /// <summary>Keep the limits applied while the game process is running, even if you Alt+Tab away
    /// (Task Manager, browser...). They are released only when the game closes.</summary>
    public bool KeepLimitsWhileGameRuns { get; set; } = true;
    /// <summary>Treat any borderless/fullscreen foreground window as a game.</summary>
    public bool AutoDetectFullscreen { get; set; } = true;
    /// <summary>Process names (no ".exe") that always count as a game when in the foreground.</summary>
    public List<string> Games { get; set; } = new();
    /// <summary>Fullscreen apps that are NOT games (video players, browsers...).</summary>
    public List<string> FullscreenIgnore { get; set; } = new()
        { "chrome", "msedge", "firefox", "brave", "opera", "vlc", "explorer" };
    public List<Target> Targets { get; set; } = new()
    {
        new Target { Exe = "Discord", CeilingMB = 250, MinCeilingMB = 100, MaxCeilingMB = 700 },
        new Target { Exe = "claude", CeilingMB = 200 },
        new Target { Exe = "steamwebhelper", CeilingMB = 150 },
        new Target { Exe = "Spotify", CeilingMB = 120, MinCeilingMB = 48, MaxCeilingMB = 400 },
    };
}

internal static class ConfigStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameRamCap");
    public static string FilePath => Path.Combine(Dir, "config.json");

    public static void Save(Config cfg)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, Options));
    }

    public static Config Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Directory.CreateDirectory(Dir);
                var def = new Config();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(def, Options));
                return def;
            }
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Options) ?? new Config();
        }
        catch
        {
            // Broken file: run with defaults instead of crashing.
            return new Config();
        }
    }
}
