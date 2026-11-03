namespace GameRamCap;

// Focus helper used by the lab: who counts as "focused".
internal sealed partial class Engine
{
    // Apps whose visible window belongs to a different process than the one we reduce.
    static readonly Dictionary<string, string[]> DefaultAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steamwebhelper"] = new[] { "steam" },
    };

    string _focusName = "";

    /// <summary>True when the foreground process (fg) is this app: same name, a FocusAlso name, or a built-in alias.</summary>
    bool IsFocused(string app, string fg)
    {
        if (fg.Length == 0) return false;
        if (string.Equals(app, fg, StringComparison.OrdinalIgnoreCase)) return true;
        var t = Config.Targets.FirstOrDefault(x => string.Equals(x.Exe, app, StringComparison.OrdinalIgnoreCase));
        if (t != null && t.FocusAlso.Contains(fg, StringComparer.OrdinalIgnoreCase)) return true;
        return DefaultAliases.TryGetValue(app, out var a) && a.Contains(fg, StringComparer.OrdinalIgnoreCase);
    }
}
