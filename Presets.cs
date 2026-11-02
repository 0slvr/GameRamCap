namespace GameRamCap;

/// <summary>One intensity level for Smart mode. Everything the engine needs to decide how hard to trim.</summary>
internal sealed record Preset(
    string Name,
    bool Always,        // trim even when plenty of RAM is free
    int Pressure,       // start trimming when free RAM % drops below this
    int Critical,       // trim harder below this
    double Scan,        // seconds between RAM checks
    double Cooldown,    // min seconds between two trims of the same app
    double Grace,       // seconds an app you just left stays untouched
    double BudgetScale, // multiplies each app's budget (lower = squeeze more)
    double Busy,        // skip processes using more CPU than this (fraction of a core)
    double BusyCrit,    // same, when RAM is critical
    int MinProcMB,      // ignore processes smaller than this
    double CritFactor); // under critical pressure the budget is multiplied by this

internal static class Presets
{
    public static readonly string[] Names  = { "UltraPerformance", "Performance", "Balanced", "Safe", "UltraSafe" };
    public static readonly string[] Labels = { "Ultra Performance", "Performance", "Balanced", "Safe", "Ultra Safe" };

    static readonly Dictionary<string, Preset> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        //                                   always pres crit scan  cool  grace scale busy  busyC minMB crit
        ["UltraPerformance"] = new("UltraPerformance", true, 60, 35, 1.0,  5,    1,   0.35, 0.40, 1.00, 20,  0.25),
        ["Performance"]      = new("Performance",      true, 45, 25, 2.0, 10,    3,   0.70, 0.25, 0.70, 30,  0.40),
        ["Balanced"]         = new("Balanced",         false,30, 12, 2.0, 20,    5,   1.00, 0.15, 0.50, 40,  0.50),
        ["Safe"]             = new("Safe",             false,20,  8, 3.0, 45,   15,   1.50, 0.08, 0.30, 80,  0.70),
        ["UltraSafe"]        = new("UltraSafe",        false,12,  5, 5.0, 90,   40,   2.50, 0.04, 0.15, 150, 0.85),
    };

    /// <summary>The preset for the config. "Custom" uses the individual settings in config.json.</summary>
    public static Preset Resolve(Config c)
    {
        string key = (c.Profile ?? "").Replace(" ", "").Replace("-", "").Replace("_", "");
        if (string.Equals(key, "Custom", StringComparison.OrdinalIgnoreCase))
            return new Preset("Custom", c.LimitWhenRamFree, c.PressureFreePercent, c.CriticalFreePercent,
                c.SmartScanSeconds, c.TrimCooldownSeconds, c.GraceAfterFocusSeconds, 1.0, 0.15, 0.5, 40, 0.5);
        return Map.TryGetValue(key, out var p) ? p : Map["Balanced"];
    }
}
