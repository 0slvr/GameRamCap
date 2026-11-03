using System.Text.Json;

namespace GameRamCap;

/// <summary>An app that takes part in the experiments (all its processes together).</summary>
internal sealed class Target
{
    /// <summary>Process name without ".exe" (e.g. "Discord").</summary>
    public string Exe { get; set; } = "";
    /// <summary>Extra process names that count as "this app is focused" (e.g. steamwebhelper -> steam).</summary>
    public List<string> FocusAlso { get; set; } = new();
}

internal sealed class Config
{
    /// <summary>Master switch (the tray menu uses Experiment = Off instead).</summary>
    public bool Enabled { get; set; } = true;

    // ================= LAB (experiments) =================
    /// <summary>Off = nothing runs. Otherwise one experiment only: E0 (measure only), E1 (memory priority),
    /// E2s (soft working-set limit), E2h (hard working-set limit), E5 (thread memory priority),
    /// E6 (adaptive controller), E8 (suspend + trim, RISKY), E9 (v28-style hard hold, RISKY).</summary>
    public string Experiment { get; set; } = "Off";
    /// <summary>"Background": a visible but unfocused app (Discord on a 2nd monitor) is treated as background.
    /// "Foreground": a visible app counts as foreground and is left alone.</summary>
    public string VisibleMode { get; set; } = "Background";
    /// <summary>false = experiments touch only the apps in Targets. true = every non-protected app.</summary>
    public bool LabEverything { get; set; } = false;
    /// <summary>E1/E6: memory priority for unfocused apps (1 = lowest).</summary>
    public int LabPriorityLevel { get; set; } = 1;
    /// <summary>E1/E6: memory priority for processes that use audio/mic/capture (5 = leave untouched, 3 = light).</summary>
    public int LabProtectedPriority { get; set; } = 3;
    /// <summary>E2/E6: the idle child process keeps this percent of its current working set as limit.</summary>
    public int LabWsKeepPercent { get; set; } = 60;
    /// <summary>E2/E6: a child is "idle" when it uses less than this percent of one core.</summary>
    public int LabIdleCpuPercent { get; set; } = 3;
    /// <summary>E9 (v28-style hard hold): MB each process of an app may keep on average (app total = this x process count).</summary>
    public int LabHoldMB { get; set; } = 30;
    /// <summary>E9: no process is squeezed below this percent of its current size (and never below 16 MB).</summary>
    public int LabHoldKeepPercent { get; set; } = 15;
    /// <summary>E6: Memory\Page Reads/sec (10 s average) above this = too much hard paging, loosen.</summary>
    public int LabPageReadsLimit { get; set; } = 100;
    /// <summary>E6: hard faults per second of an audio/mic process above this = loosen that app.</summary>
    public double LabAudioHardFaultLimit { get; set; } = 1.0;
    /// <summary>E6: seconds without a problem before tightening one step.</summary>
    public int LabControllerQuietSeconds { get; set; } = 60;
    /// <summary>Also register IAudioSessionNotification (catches new audio sessions the enumerator misses).</summary>
    public bool LabAudioNotifications { get; set; } = true;
    /// <summary>Seconds a process stays protected after its audio stops.</summary>
    public double AudioProtectGraceSeconds { get; set; } = 8;

    /// <summary>Apps that take part in the experiments (used when LabEverything is false).</summary>
    public List<Target> Targets { get; set; } = new()
    {
        new Target { Exe = "Discord" },
        new Target { Exe = "claude" },
        new Target { Exe = "steamwebhelper" },
        new Target { Exe = "Spotify" },
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
            // Old config files with removed settings still load: unknown fields are ignored.
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Options) ?? new Config();
        }
        catch
        {
            // Broken file: run with defaults instead of crashing.
            return new Config();
        }
    }
}
