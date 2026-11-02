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
    double CritFactor,  // under critical pressure the budget is multiplied by this
    int HoldMB = 0,
    int HoldFloorMB = 0,  // no process of a held app is squeezed below this (0 = use config HoldMinProcessMB)
    int HoldKeepPct = 0); // nor below this % of its current size (0 = use config HoldKeepPercent)    // hard RAM ceiling for a whole background app, all processes together (0 = use each target's CeilingMB per process)

internal static class Presets
{
    public static readonly string[] Names  = { "UltraPerformance", "Performance", "Balanced", "Safe", "UltraSafe" };
    public static readonly string[] Labels = { "Ultra Performance", "Performance", "Balanced", "Safe", "Ultra Safe" };

    static readonly Dictionary<string, Preset> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Every level acts at all times (Always) and re-checks every N seconds: Ultra = 1s ... UltraSafe = 5s.
        // The real difference is HoldMB: the hard RAM ceiling per background APP (shared between its processes).
        //                                   always pres crit scan cool grace scale busy  busyC minMB crit  holdMB
        ["UltraPerformance"] = new("UltraPerformance", true, 60, 35, 1.0, 1.0, 1.0, 0.35, 0.40, 1.00, 20,  0.25, 20, 16, 25),
        ["Performance"]      = new("Performance",      true, 45, 25, 2.0, 2.0, 2.0, 0.70, 0.25, 0.70, 30,  0.40, 40, 24, 35),
        ["Balanced"]         = new("Balanced",         true, 30, 12, 3.0, 3.0, 3.0, 1.00, 0.15, 0.50, 40,  0.50, 100, 48, 55),
        ["Safe"]             = new("Safe",             true, 20,  8, 4.0, 4.0, 4.0, 1.50, 0.08, 0.30, 80,  0.70, 250, 64, 70),
        ["UltraSafe"]        = new("UltraSafe",        true, 12,  5, 5.0, 5.0, 5.0, 2.50, 0.04, 0.15, 150, 0.85, 400, 96, 80),
    };

    /// <summary>The preset for the config. "Custom" uses the individual settings in config.json.</summary>
    public static Preset Resolve(Config c)
    {
        string key = (c.Profile ?? "").Replace(" ", "").Replace("-", "").Replace("_", "");
        if (string.Equals(key, "Custom", StringComparison.OrdinalIgnoreCase))
            return new Preset("Custom", c.LimitWhenRamFree, c.PressureFreePercent, c.CriticalFreePercent,
                c.SmartScanSeconds, c.TrimCooldownSeconds, c.GraceAfterFocusSeconds, 1.0, 0.15, 0.5, 40, 0.5);
        var p = Map.TryGetValue(key, out var found) ? found : Map["Balanced"];
        PresetOverride? o = null;
        if (c.PresetOverrides != null)
            foreach (var kv in c.PresetOverrides)
                if (string.Equals(kv.Key, p.Name, StringComparison.OrdinalIgnoreCase)) { o = kv.Value; break; }
        if (o == null) return p;
        return p with
        {
            Scan        = Math.Max(0.5, o.Scan ?? p.Scan),
            HoldMB      = Math.Max(1, o.HoldMB ?? p.HoldMB),
            Cooldown    = Math.Max(0, o.Cooldown ?? p.Cooldown),
            Grace       = Math.Max(0, o.Grace ?? p.Grace),
            BudgetScale = Math.Clamp(o.BudgetScale ?? p.BudgetScale, 0.1, 10),
            MinProcMB   = Math.Max(1, o.MinProcMB ?? p.MinProcMB),
            CritFactor  = Math.Clamp(o.CritFactor ?? p.CritFactor, 0.1, 1),
        };
    }

    /// <summary>Built-in values of a level, ignoring user edits.</summary>
    public static Preset Default(string name) => Map[name];
}
