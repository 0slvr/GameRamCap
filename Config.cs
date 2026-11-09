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
    /// <summary>With one of the three levels: use THIS app's own Limit/Min/Max/Adaptive instead of the level's numbers.
    /// (Custom always uses them.)</summary>
    public bool UseOwnLimit { get; set; } = false;
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
    /// <summary>Custom level only: no process of a held app is squeezed below this many MB.</summary>
    public int? FloorMB { get; set; }
    /// <summary>Custom level only: nor below this percent of its current size.</summary>
    public int? KeepPct { get; set; }
}

internal sealed class Config
{
    /// <summary>Edits of the intensity levels, keyed by level name (UltraPerformance ... Balanced).</summary>
    public Dictionary<string, PresetOverride> PresetOverrides { get; set; } = new();
    /// <summary>The numbers of the Custom level (the last stop of the intensity slider on the Main tab).
    /// A null field means "use the Balanced value". Custom runs through the same engine path as the three levels.</summary>
    public PresetOverride CustomLevel { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>"Smart" (default): no hard caps. Background apps are trimmed gently, only when Windows is short on RAM,
    /// per app (all its processes together), and the budget adapts if the app fights back.
    /// "Hard": the old fixed per-process hard ceilings.</summary>
    public string Mode { get; set; } = "Smart";
    /// <summary>Smart mode (all levels, Custom included): keep background apps within their budget ALL the time,
    /// even when plenty of RAM is free. false = only act under memory pressure (the level's Pressure %;
    /// Custom: PressureFreePercent). Settings checkbox "Only limit when RAM is low" = the opposite of this.</summary>
    public bool LimitWhenRamFree { get; set; } = true;
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
    /// <summary>Smart mode intensity: UltraPerformance, Performance, Balanced, or Custom.
    /// The three levels use their own built-in table (Presets.cs; per-level overrides via PresetOverrides). Custom uses the individual numbers above AND
    /// the per-app Limit/Min/Max (Targets) and the "apps not in the list" limit (Default*) from the Apps tab;
    /// those app numbers are ignored with the three levels.</summary>
    public string Profile { get; set; } = "Balanced";
    /// <summary>Hold mode: no process of a held app is squeezed below this many MB (avoids page-fault stutter).</summary>
    public int HoldMinProcessMB { get; set; } = 64;
    /// <summary>Hold mode: no process is squeezed below this percent of its current size when the ceiling is set.</summary>
    public int HoldKeepPercent { get; set; } = 60;
    /// <summary>Smart mode: only the PROCESS that owns an active audio stream (playing sound or using the mic) is left
    /// alone, so audio cannot crackle. The other processes of the same app are limited as usual.</summary>
    public bool AudioProtect { get; set; } = true;
    /// <summary>Seconds an app stays protected after its audio stops.</summary>
    public double AudioProtectGraceSeconds { get; set; } = 8;
    /// <summary>Hold mode: the process that plays audio gets this ceiling (MB) while RAM is under pressure, instead of no ceiling at all.
    /// Generous on purpose, so playback is not affected. 0 = never limit it (old behaviour).</summary>
    public int AudioCapMB { get; set; } = 160;
    /// <summary>Hold mode: find out what a background process really needs (trim it once, see what it pulls back) and cap it at that
    /// x NeedMargin, instead of guessing with percentages and learned floors.</summary>
    public bool MeasureNeed { get; set; } = true;
    /// <summary>Apps you switch to often are squeezed less: every time you bring an app to the front in the last 30 minutes adds this much
    /// to its margin / keep share (0 = off). Rarely used apps keep the normal numbers.</summary>
    public double WakeBoostPerUse { get; set; } = 0.1;
    /// <summary>Margin on top of the measured need (1.3 = 30 % extra).</summary>
    public double NeedMargin { get; set; } = 1.3;
    /// <summary>Absolute ceiling (MB) for a measured need, and raises never go past twice this. Stops one busy process (Spotify's 1.9 GB one) from
    /// "needing" 800 MB. 0 = no limit.</summary>
    public int NeedMaxMB { get; set; } = 128;
    /// <summary>Seconds to wait after the trim before reading what the process pulled back.</summary>
    public double NeedProbeSeconds { get; set; } = 4;
    /// <summary>Sets the limit for apps that are not in the Apps list (used by Hard mode and the Custom level).</summary>
    public void SetDefaultLimit(int mb, bool fixedLimit)
    {
        DefaultCeilingMB = mb;
        DefaultAdaptive = !fixedLimit;
        DefaultMinCeilingMB = fixedLimit ? mb : Math.Max(20, mb / 2);
        DefaultMaxCeilingMB = fixedLimit ? mb : mb * 3;
    }
    /// <summary>Hard mode scope: "Process" (recommended) = the limit applies to each process of an app separately;
    /// "App" = the limit is the TOTAL for the app, split equally between its running processes.</summary>
    public string HardScope { get; set; } = "Process";
    public bool HardPerApp() => !IsSmart() && string.Equals(HardScope, "App", StringComparison.OrdinalIgnoreCase);
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
    /// <summary>Built-in protected apps (see Engine.BaseProtected) the user chose to unprotect from the right-click menu.</summary>
    public List<string> UnprotectedBuiltIn { get; set; } = new();
    /// <summary>Seconds to wait after leaving a target app before its limit comes back (avoids flicker on Alt+Tab).</summary>
    public double RelimitDelaySeconds { get; set; } = 1.5;
    /// <summary>Small apps you open briefly again and again (Win+V, Snipping Tool, a password manager popup...) are learned
    /// while GameRamCap runs and never trimmed, so they open instantly. Set false to turn this off.</summary>
    /// <summary>The moment an app comes to the front, its limit is lifted at once and its pages are read back into RAM
    /// in the background, so a trimmed app wakes up fast. Set false to turn this off.</summary>
    public bool PrefetchApps { get; set; } = true;
    /// <summary>Most MB read back into RAM per app when it is woken up (PrefetchApps and the Win+V wake-up). Lower = lighter on the disk and RAM.</summary>
    public int PrefetchMaxMB { get; set; } = 96;
    /// <summary>Apps you come back to often (see WakeBoostPerUse) are read back up to this many times PrefetchMaxMB extra: 2 = up to 3x. 0 = same for all.</summary>
    public double PrefetchFrequentBoost { get; set; } = 2.0;
    /// <summary>Wake-ups are skipped while free RAM is below this percent, so they never add pressure on a full machine.</summary>
    public int PrefetchMinFreePercent { get; set; } = 12;
    /// <summary>Win+V / Win+. / Win+; / Win+H: the moment the keys are pressed (before the panel shows) the Win+V host is freed and read back into RAM.</summary>
    public bool WakeWinVOnKey { get; set; } = true;
    /// <summary>Win (Start) / Win+S / Win+Q: the moment the key is pressed (before Start or Search shows) their hosts are freed and read back into RAM.</summary>
    public bool WakeStartOnKey { get; set; } = true;
    /// <summary>Alt+Tab: the moment Tab is pressed with Alt held (before the switcher closes) the app you most likely switch to is freed and read back into RAM.</summary>
    public bool WakeAltTabOnKey { get; set; } = true;
    /// <summary>Mouse click on another app's window: the moment the button goes down (before the window comes to the front) that app is freed and read back into RAM.</summary>
    public bool WakeClickOnMouse { get; set; } = true;
    /// <summary>Win+V / emoji host (TextInputHost) grows over time. When it is idle (not in front for 10 s) and bigger than
    /// this many MB, it is closed; Windows starts a fresh small one the next time you need it. 0 = never.</summary>
    /// <summary>Diagnostics: writes a log line whenever a background process gets a ceiling, when that ceiling is raised or lowered, and when an
    /// audio-playing process is freed from its ceiling. Turn off once you have the answer you need.</summary>
    public bool LogHolds { get; set; } = false;
    /// <summary>Processes at least this big (MB, size when first held) are never squeezed below BigProcKeepPercent of that size, at any level.
    /// Stops a huge process (Spotify at 1.3 GB squeezed to 12 MB) from thrashing the disk. 0 = off.</summary>
    public int BigProcMB { get; set; } = 300;
    public int BigProcKeepPercent { get; set; } = 20;
    /// <summary>A big process (see BigProcMB) that page-faults is given room back, but never more than this percent of the ceiling it was first
    /// held at (200 = twice). Without it the "give room back" step walks the ceiling all the way to the original size within seconds. 0 = no limit.</summary>
    public int BigProcMaxGrowPercent { get; set; } = 200;
    /// <summary>Smart mode gives a held app room back only when its page faults come with real disk reads (system "Pages Input/sec" at or above
    /// HardFaultPagesPerSec). Faults that are served from RAM (soft faults) are harmless and no longer undo the squeeze. Set false for the old behaviour.</summary>
    public bool SmartFaultCheck { get; set; } = true;
    public int HardFaultPagesPerSec { get; set; } = 30;
    /// <summary>Page faults per second per resident MB above which a held process counts as starved even when the disk is idle (soft faults burn CPU). 0 = off.</summary>
    public int ThrashFaultsPerMB { get; set; } = 60;
    public int InputHostRecycleMB { get; set; } = 0;
    /// <summary>TextInputHost is never closed until this many seconds have passed since the last Win+V / Win+. / Win+; / Win+H press.</summary>
    public int InputHostIdleSeconds { get; set; } = 120;
    /// <summary>A process that just showed, uncloaked or focused a window (any app, not only the foreground one) is treated as in use for this many
    /// seconds, so popups and helper apps you summon for a moment are not limited. 0 = off.</summary>
    public double RecentActivitySeconds { get; set; } = 5;
    public bool LearnPopupApps { get; set; } = false;
    /// <summary>Limit the target apps all the time (from the moment they start), not only while a game runs.</summary>
    public bool AlwaysLimit { get; set; } = true;
    /// <summary>Seconds a game must stay in the foreground before limits are applied.</summary>
    public int DelaySeconds { get; set; } = 5;
    /// <summary>Keep the limits applied while the game process is running, even if you Alt+Tab away
    /// (Task Manager, browser...). They are released only when the game closes.</summary>
    public bool KeepLimitsWhileGameRuns { get; set; } = true;
    /// <summary>Treat any borderless/fullscreen foreground window as a game.</summary>
    public bool AutoDetectFullscreen { get; set; } = true;   // always on (forced in ConfigStore), no longer a user option
    /// <summary>Process names (no ".exe") that always count as a game when in the foreground.</summary>
    public List<string> Games { get; set; } = new();
    /// <summary>Fullscreen apps that are NOT games (video players, browsers...).</summary>
    public List<string> FullscreenIgnore { get; set; } = new()
        { "chrome", "msedge", "firefox", "brave", "opera", "vlc", "explorer" };
    /// <summary>Saved CPU rules (Apps tab, right-click): every time one of these apps runs it gets this priority
    /// and/or these CPU cores. Independent of the RAM limits above.</summary>
    public List<ProcessRule> Rules { get; set; } = new();
    /// <summary>While a game runs and FREE RAM is low, clear the Windows standby list (at most once per interval).</summary>
    public bool StandbyClean { get; set; } = false;
    /// <summary>Global keyboard + mouse hooks (on their own thread) that wake an app a few milliseconds BEFORE you use it (Win key, Win+V, clicks).
    /// Off by default: some anti-cheats and antivirus tools dislike global hooks, and the wake-up on focus change / Alt+Tab switcher still works
    /// without them. Needs a restart.</summary>
    public bool GlobalHooks { get; set; } = false;
    /// <summary>Standby cleaner: act only when free RAM (free + zeroed pages, not the standby cache) is below this many MB...</summary>
    public int StandbyFreeBelowMB { get; set; } = 1024;
    /// <summary>...and the standby list holds at least this many MB.</summary>
    public int StandbyMinMB { get; set; } = 1024;
    /// <summary>Standby cleaner: minimum seconds between two cleanings.</summary>
    public int StandbyMinIntervalSeconds { get; set; } = 120;
    /// <summary>Smart and Hard also manage svchost and MsMpEng (Windows services, Defender), like any other background app: measured need,
    /// the app's own budget, floors, release when it needs more. Off by default. The ONLY protected names this switches; everything else
    /// in Engine.BaseProtected (dwm, audiodg, anti-cheat, GPU services...) stays untouched. Needs no restart.</summary>
    public bool ManageSystemServices { get; set; } = false;
    public List<Target> Targets { get; set; } = new();   // empty by default: you add the apps yourself
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

    /// <summary>%ProgramData%\GameRamCap: only SYSTEM and Administrators can write there (see SecureStore).
    /// It used to be %APPDATA%, where any program of the user could edit the files this admin app trusts.</summary>
    public static string Dir => SecureStore.Dir;
    public static string FilePath => Path.Combine(Dir, "config.json");

    public static void Save(Config cfg)
    {
        SecureStore.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, Options));
        try { File.Copy(FilePath, LastGoodPath, true); } catch { }   // last known-good copy
    }

    static string LastGoodPath => Path.Combine(Dir, "config.lastgood.json");

    /// <summary>Focus mode is always on; it is no longer a user option.</summary>
    static Config Force(Config c) { c.FocusMode = true; c.AutoDetectFullscreen = true; c.InputHostRecycleMB = 0; return c; }   // closing TextInputHost is retired: it is limited like any background app instead

    // ---------------------------------------------------------------- validation: never trust the file blindly

    static string? CleanName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        if (s.Length == 0 || s.Length > 128) return null;
        foreach (char ch in s) if (char.IsControl(ch) || "\\/:*?\"<>|".IndexOf(ch) >= 0) return null;   // a process name, never a path
        return s;
    }

    static List<string> CleanList(List<string>? list) =>
        (list ?? new List<string>()).Select(CleanName).Where(n => n != null).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();

    static int Clamp(int v, int lo, int hi) => Math.Min(hi, Math.Max(lo, v));
    static double Clamp(double v, double lo, double hi) => double.IsNaN(v) ? lo : Math.Min(hi, Math.Max(lo, v));

    /// <summary>Names must be plain process names and numbers must be in sane ranges (generous: real settings are untouched).</summary>
    static Config Sanitize(Config c)
    {
        c.PresetOverrides ??= new();
        c.CustomLevel ??= new();
        c.Targets = (c.Targets ?? new List<Target>()).Where(t => t != null).Select(t =>
        {
            t.Exe = CleanName(t.Exe) ?? "";
            t.CeilingMB = Clamp(t.CeilingMB, 2, 1_000_000);
            t.MinCeilingMB = Clamp(t.MinCeilingMB, 2, 1_000_000);
            t.MaxCeilingMB = Clamp(t.MaxCeilingMB, 2, 1_000_000);
            t.BudgetMB = Clamp(t.BudgetMB, 0, 1_000_000);
            t.FocusAlso = CleanList(t.FocusAlso);
            return t;
        }).Where(t => t.Exe.Length > 0).Take(500).ToList();
        c.Games = CleanList(c.Games);
        c.Exceptions = CleanList(c.Exceptions);
        c.FullscreenIgnore = CleanList(c.FullscreenIgnore);
        c.UnprotectedBuiltIn = CleanList(c.UnprotectedBuiltIn);
        c.Rules = (c.Rules ?? new List<ProcessRule>()).Where(r => r != null).Select(r =>
        {
            r.Exe = CleanName(r.Exe) ?? "";
            if (!ProcessRules.PriorityNames.Contains(r.Priority ?? "", StringComparer.OrdinalIgnoreCase)) r.Priority = "";
            if (r.AffinityMask < 0) r.AffinityMask = 0;
            return r;
        }).Where(r => r.Exe.Length > 0).Take(500).ToList();

        if (!c.IsSmart() && !string.Equals(c.Mode, "Hard", StringComparison.OrdinalIgnoreCase)) c.Mode = "Smart";
        if (!string.Equals(c.HardScope, "App", StringComparison.OrdinalIgnoreCase)) c.HardScope = "Process";
        if (!Presets.Names.Contains(c.Profile ?? "", StringComparer.OrdinalIgnoreCase) && !Presets.IsCustomLevel(c)) c.Profile = "Balanced";

        c.PressureFreePercent = Clamp(c.PressureFreePercent, 1, 90);
        c.CriticalFreePercent = Clamp(c.CriticalFreePercent, 1, 90);
        c.SmartScanSeconds = Clamp(c.SmartScanSeconds, 0.5, 60);
        c.TrimCooldownSeconds = Clamp(c.TrimCooldownSeconds, 1, 3600);
        c.GraceAfterFocusSeconds = Clamp(c.GraceAfterFocusSeconds, 0, 300);
        c.HoldMinProcessMB = Clamp(c.HoldMinProcessMB, 0, 4096);
        c.HoldKeepPercent = Clamp(c.HoldKeepPercent, 0, 100);
        c.AudioProtectGraceSeconds = Clamp(c.AudioProtectGraceSeconds, 0, 300);
        c.AudioCapMB = Clamp(c.AudioCapMB, 0, 100_000);
        c.NeedMaxMB = Clamp(c.NeedMaxMB, 0, 100_000);
        c.NeedMargin = Clamp(c.NeedMargin, 1, 10);
        c.NeedProbeSeconds = Clamp(c.NeedProbeSeconds, 0.5, 120);
        c.WakeBoostPerUse = Clamp(c.WakeBoostPerUse, 0, 5);
        c.RelimitDelaySeconds = Clamp(c.RelimitDelaySeconds, 0, 60);
        c.DefaultCeilingMB = Clamp(c.DefaultCeilingMB, 2, 1_000_000);
        c.DefaultMinCeilingMB = Clamp(c.DefaultMinCeilingMB, 2, 1_000_000);
        c.DefaultMaxCeilingMB = Clamp(c.DefaultMaxCeilingMB, 2, 1_000_000);
        c.PrefetchMaxMB = Clamp(c.PrefetchMaxMB, 0, 4096);
        c.PrefetchFrequentBoost = Clamp(c.PrefetchFrequentBoost, 0, 10);
        c.PrefetchMinFreePercent = Clamp(c.PrefetchMinFreePercent, 0, 100);
        c.BigProcMB = Clamp(c.BigProcMB, 0, 1_000_000);
        c.BigProcKeepPercent = Clamp(c.BigProcKeepPercent, 0, 100);
        c.BigProcMaxGrowPercent = Clamp(c.BigProcMaxGrowPercent, 0, 10_000);
        c.HardFaultPagesPerSec = Clamp(c.HardFaultPagesPerSec, 0, 1_000_000);
        c.ThrashFaultsPerMB = Clamp(c.ThrashFaultsPerMB, 0, 1_000_000);
        c.InputHostIdleSeconds = Clamp(c.InputHostIdleSeconds, 0, 86400);
        c.RecentActivitySeconds = Clamp(c.RecentActivitySeconds, 0, 300);
        c.DelaySeconds = Clamp(c.DelaySeconds, 0, 600);
        c.StandbyFreeBelowMB = Clamp(c.StandbyFreeBelowMB, 0, 1_000_000);
        c.StandbyMinMB = Clamp(c.StandbyMinMB, 0, 1_000_000);
        c.StandbyMinIntervalSeconds = Clamp(c.StandbyMinIntervalSeconds, 10, 86400);
        if (c.CriticalFreePercent > c.PressureFreePercent) c.CriticalFreePercent = c.PressureFreePercent;   // "critical" can never be above "pressure"
        foreach (var o in c.PresetOverrides.Values.Where(o => o != null)) ClampOverride(o);
        ClampOverride(c.CustomLevel);
        return c;
    }

    static void ClampOverride(PresetOverride? o)
    {
        if (o == null) return;
        if (o.Scan is double scan) o.Scan = Clamp(scan, 0.5, 60);
        if (o.HoldMB is int hold) o.HoldMB = Clamp(hold, 2, 100_000);
        if (o.Cooldown is double cool) o.Cooldown = Clamp(cool, 0, 3600);
        if (o.Grace is double grace) o.Grace = Clamp(grace, 0, 300);
        if (o.BudgetScale is double scale) o.BudgetScale = Clamp(scale, 0.1, 10);
        if (o.MinProcMB is int minp) o.MinProcMB = Clamp(minp, 1, 100_000);
        if (o.CritFactor is double crit) o.CritFactor = Clamp(crit, 0.1, 1);
        if (o.FloorMB is int floor) o.FloorMB = Clamp(floor, 2, 100_000);
        if (o.KeepPct is int keep) o.KeepPct = Clamp(keep, 1, 100);
    }

    public static Config Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var def = new Config();
                SecureStore.WriteAllText(FilePath, JsonSerializer.Serialize(def, Options));
                return def;
            }
            return Force(Sanitize(JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Options) ?? new Config()));
        }
        catch (Exception ex)
        {
            // Broken file: keep a copy for the user, log it, fall back to the last good config (else defaults).
            try
            {
                string bad = Path.Combine(Dir, $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(FilePath, bad, true);
                Logger.Write($"config.json is invalid ({ex.Message}). Saved a copy as {Path.GetFileName(bad)}.");
            }
            catch { }
            try
            {
                if (File.Exists(LastGoodPath))
                {
                    var good = JsonSerializer.Deserialize<Config>(File.ReadAllText(LastGoodPath), Options);
                    if (good != null)
                    {
                        Logger.Write("Using the last known-good config.");
                        File.Copy(LastGoodPath, FilePath, true);   // repair, so the next hot-reload sees a valid file
                        return Force(Sanitize(good));
                    }
                }
            }
            catch { }
            return new Config();
        }
    }
}
