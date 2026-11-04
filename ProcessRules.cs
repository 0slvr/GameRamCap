using System.Diagnostics;

namespace GameRamCap;

/// <summary>A saved CPU rule: every time this app runs it gets this priority and/or these CPU cores.
/// Task Manager forgets such changes when the process exits; a rule is applied again every time.</summary>
internal sealed class ProcessRule
{
    /// <summary>Process name without ".exe".</summary>
    public string Exe { get; set; } = "";
    /// <summary>Idle, BelowNormal, Normal, AboveNormal or High. Empty = leave the priority alone.</summary>
    public string Priority { get; set; } = "";
    /// <summary>Bit mask of allowed logical cores (bit 0 = core 0). 0 = leave the cores alone.</summary>
    public long AffinityMask { get; set; } = 0;
}

internal static class ProcessRules
{
    /// <summary>RealTime is left out on purpose: it can freeze the whole PC.</summary>
    public static readonly string[] PriorityNames = { "Idle", "BelowNormal", "Normal", "AboveNormal", "High" };

    public static string Label(string priority) => priority switch
    {
        "Idle" => "Low",
        "BelowNormal" => "Below normal",
        "Normal" => "Normal",
        "AboveNormal" => "Above normal",
        "High" => "High",
        _ => priority,
    };

    /// <summary>Mask with one bit per logical core (capped at 63 so it fits a long).</summary>
    public static long AllCoresMask()
    {
        int n = Math.Clamp(Environment.ProcessorCount, 1, 63);
        return (1L << n) - 1;
    }

    static bool TryParse(string name, out ProcessPriorityClass cls)
    {
        cls = ProcessPriorityClass.Normal;
        foreach (var n in PriorityNames)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                return Enum.TryParse(n, out cls);
        return false;
    }

    static void ApplyTo(Process p, string priority, long affinity)
    {
        if (priority.Length > 0 && TryParse(priority, out var cls) && p.PriorityClass != cls)
            p.PriorityClass = cls;
        if (affinity != 0)
        {
            long mask = affinity & AllCoresMask();
            if (mask != 0 && (long)p.ProcessorAffinity != mask)
                p.ProcessorAffinity = (IntPtr)mask;
        }
    }

    /// <summary>Applies a priority and/or core mask now to every process of the app. Returns how many worked / failed.</summary>
    public static (int ok, int failed) SetNow(string exe, string priority, long affinity)
    {
        int ok = 0, failed = 0;
        Process[] list;
        try { list = Process.GetProcessesByName(exe); } catch { return (0, 0); }
        foreach (var p in list)
        {
            try { ApplyTo(p, priority, affinity); ok++; }
            catch { failed++; }
            finally { p.Dispose(); }
        }
        return (ok, failed);
    }

    static DateTime _last = DateTime.MinValue;

    /// <summary>Called from the engine tick: re-applies every saved rule (new processes get it within ~5 seconds).</summary>
    public static void Apply(Config cfg)
    {
        var rules = cfg.Rules;
        if (rules == null || rules.Count == 0) return;
        var now = DateTime.UtcNow;
        if ((now - _last).TotalSeconds < 5) return;
        _last = now;
        foreach (var r in rules)
        {
            if (string.IsNullOrWhiteSpace(r.Exe) || Engine.Protected.Contains(r.Exe)) continue;
            if (r.Priority.Length == 0 && r.AffinityMask == 0) continue;
            SetNow(r.Exe, r.Priority, r.AffinityMask);
        }
    }
}
