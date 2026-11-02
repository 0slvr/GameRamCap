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
}

internal sealed class Config
{
    public bool Enabled { get; set; } = true;
    /// <summary>Seconds a game must stay in the foreground before limits are applied.</summary>
    public int DelaySeconds { get; set; } = 5;
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
