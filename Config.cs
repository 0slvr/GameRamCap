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
}

internal sealed class Config
{
    public bool Enabled { get; set; } = true;
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
