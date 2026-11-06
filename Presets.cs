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
    public static readonly string[] Names  = { "UltraPerformance", "Performance", "Balanced" };
    public static readonly string[] Labels = { "Ultra Performance", "Performance", "Balanced" };

    public const string CustomName = "Custom";

    /// <summary>Smallest ceiling (MB) the engine will ever set on a process. Windows itself will not go below 2 MB.</summary>
    public const int AbsMinMB = 2;

    /// <summary>True when the Custom level is selected (the slider's last stop).</summary>
    public static bool IsCustomLevel(Config c) =>
        string.Equals((c.Profile ?? "").Replace(" ", "").Replace("-", "").Replace("_", ""), CustomName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The OLD per-app Custom mode (own numbers per app on the Apps tab) is gone: Custom is now a level of its own
    /// that goes through the same engine path as the three levels, so this is always false. Per-app numbers still work in
    /// Hard mode and for Apps rows ticked "Own limit".</summary>
    public static bool IsCustom(Config c) => false;

    static readonly Dictionary<string, Preset> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Every level acts at all times (Always) and re-checks every N seconds: Ultra = 1s ... Balanced = 3s.
        // The real difference is HoldMB: the hard RAM ceiling per background process (app total = HoldMB x its processes).
        // HoldFloorMB is the smallest a process of a held app is squeezed to at that level, so Ultra can really go down to 8 MB.
        //                                   always pres crit scan cool grace scale busy  busyC minMB crit  holdMB floor keep%
        ["UltraPerformance"] = new("UltraPerformance", true, 60, 35, 1.0, 1.0, 1.0, 0.35, 0.40, 1.00, 20,  0.25, 8,   8,   1),
        ["Performance"]      = new("Performance",      true, 45, 25, 2.0, 2.0, 2.0, 0.70, 0.25, 0.70, 30,  0.40, 20,  12,  8),
        ["Balanced"]         = new("Balanced",         true, 30, 12, 3.0, 3.0, 3.0, 1.00, 0.15, 0.50, 40,  0.50, 30,  16,  15),
        // Custom starts as a copy of Balanced; the user's own numbers (Config.CustomLevel) are applied on top in Resolve.
        ["Custom"]           = new("Custom",           true, 30, 12, 3.0, 3.0, 3.0, 1.00, 0.15, 0.50, 40,  0.50, 30,  16,  15),
    };

    /// <summary>The preset for the config. "Custom" = Balanced with the user's own numbers (Config.CustomLevel).</summary>
    public static Preset Resolve(Config c)
    {
        string key = (c.Profile ?? "").Replace(" ", "").Replace("-", "").Replace("_", "");
        bool custom = string.Equals(key, CustomName, StringComparison.OrdinalIgnoreCase);
        var p = Map.TryGetValue(key, out var found) ? found : Map["Balanced"];
        // "Only limit when RAM is low" (LimitWhenRamFree off) works with every level:
        // they only act once free RAM drops below the level's Pressure value.
        p = p with { Always = c.LimitWhenRamFree };
        PresetOverride? o = null;
        if (custom) o = c.CustomLevel ?? new PresetOverride();
        else if (c.PresetOverrides != null)
            foreach (var kv in c.PresetOverrides)
                if (string.Equals(kv.Key, p.Name, StringComparison.OrdinalIgnoreCase)) { o = kv.Value; break; }
        if (o == null) return p;
        var r = p with
        {
            Scan        = Math.Max(0.5, o.Scan ?? p.Scan),
            HoldMB      = Math.Max(AbsMinMB, o.HoldMB ?? p.HoldMB),
            Cooldown    = Math.Max(0, o.Cooldown ?? p.Cooldown),
            Grace       = Math.Max(0, o.Grace ?? p.Grace),
            BudgetScale = Math.Clamp(o.BudgetScale ?? p.BudgetScale, 0.1, 10),
            MinProcMB   = Math.Max(1, o.MinProcMB ?? p.MinProcMB),
            CritFactor  = Math.Clamp(o.CritFactor ?? p.CritFactor, 0.1, 1),
        };
        if (!custom) return r;
        // Custom: the small helper numbers follow the ceiling unless the user set them, so a tiny ceiling
        // (for example 3 MB) is not silently ignored by "min process" / "floor".
        int hold = r.HoldMB;
        return r with
        {
            MinProcMB    = Math.Max(1, o.MinProcMB ?? Math.Min(40, hold)),
            HoldFloorMB  = Math.Max(AbsMinMB, o.FloorMB ?? Math.Min(hold, 16)),
            HoldKeepPct  = Math.Clamp(o.KeepPct ?? 15, 1, 100),
        };
    }

    /// <summary>Built-in values of a level, ignoring user edits.</summary>
    public static Preset Default(string name) => Map[name];
}
