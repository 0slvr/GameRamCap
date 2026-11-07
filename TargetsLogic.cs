namespace GameRamCap;

/// <summary>The numbers of one row of the limits table, after checking.</summary>
internal sealed record TargetValues(string Exe, int CeilingMB, int MinMB, int MaxMB, bool Adaptive, bool Own, int BudgetMB, List<string> Also);

/// <summary>Pure rules for the limits table (no UI), so they can be tested on their own.</summary>
internal static class TargetsLogic
{
    /// <summary>A whole number above 0, or the fallback.</summary>
    public static int Num(object? v, int fallback) =>
        int.TryParse(Convert.ToString(v), out var n) && n > 0 ? n : fallback;

    /// <summary>True when the cell has text that is not a whole number above 0 (an empty cell is fine: the default is used).</summary>
    public static bool BadNum(object? v)
    {
        string txt = (Convert.ToString(v) ?? "").Trim();
        return txt.Length > 0 && !(int.TryParse(txt, out var num) && num > 0);
    }

    /// <summary>"a.exe, b; c" -> ["a", "b", "c"].</summary>
    public static List<string> ParseAlso(string? text) =>
        (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x)
            .Where(x => x.Length > 0).ToList();

    /// <summary>Checks one row. Anything that had to be corrected is described in <paramref name="problems"/>.</summary>
    public static TargetValues Build(string exe, object? ceiling, object? min, object? max, bool adaptive, bool own,
                                     object? budget, string? also, List<string> problems)
    {
        foreach (var (name, v) in new[] { ("Ceiling", ceiling), ("Min", min), ("Max", max), ("Budget", budget) })
            if (BadNum(v)) problems.Add($"{exe}: \"{v}\" in {name} is not a whole number above 0 (a default was used)");
        int mn = Num(min, 64);
        int rawMax = Num(max, 600);
        if (rawMax < mn) problems.Add($"{exe}: Max ({rawMax}) was below Min ({mn}); Max was raised to {mn}");
        int mx = Math.Max(mn, rawMax);
        int ceil = Math.Clamp(Num(ceiling, 200), mn, mx);
        int bud = int.TryParse(Convert.ToString(budget), out var b) && b > 0 ? b : 0;
        return new TargetValues(exe, ceil, mn, mx, adaptive, own, bud, ParseAlso(also));
    }
}
