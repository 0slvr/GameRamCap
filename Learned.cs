using System.Text.Json;

namespace GameRamCap;

internal sealed class LearnedEntry
{
    public int Mb { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Remembers, per app, the lowest per-process ceiling (MB) that was found safe, so auto-squeeze does not
/// start from zero every time. Kept in learned.json next to config.json - a separate file on purpose: saving
/// config.json makes the app reload and reset every limit. Entries older than 14 days are forgotten.</summary>
internal static class LearnedStore
{
    const int MaxAgeDays = 14;
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };
    static readonly Dictionary<string, LearnedEntry> Map = Read();
    static bool _dirty;
    static DateTime _lastSave = DateTime.UtcNow;

    public static string FilePath => Path.Combine(ConfigStore.Dir, "learned.json");

    static Dictionary<string, LearnedEntry> Read()
    {
        var m = new Dictionary<string, LearnedEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, LearnedEntry>>(File.ReadAllText(FilePath), Opt);
                if (d != null) foreach (var kv in d) m[kv.Key] = kv.Value;
            }
        }
        catch { /* broken file: start empty */ }
        return m;
    }

    /// <summary>One app has many processes of very different sizes (Chrome: a 300 MB renderer next to a 40 MB
    /// helper), so the level is learned per app AND per size class (power-of-two bucket of the process size).
    /// A value learned on a big process can no longer be applied to a small one, and vice versa.</summary>
    static string Key(string app, long wsBytes)
    {
        long mb = Math.Max(1, wsBytes / (1024 * 1024));
        return app + "#" + (int)Math.Log2(mb);
    }

    /// <summary>Safe floor in MB for a process of this app and size (0 = nothing learned yet).</summary>
    public static int Get(string app, long wsBytes)
    {
        lock (Gate)
            return Map.TryGetValue(Key(app, wsBytes), out var e) && (DateTime.UtcNow - e.At).TotalDays <= MaxAgeDays ? e.Mb : 0;
    }

    /// <summary>Called when a process of this app started page-faulting at its ceiling: that level is the floor.</summary>
    public static void Raise(string app, long wsBytes, int mb)
    {
        if (string.IsNullOrWhiteSpace(app) || mb <= 0) return;
        var key = Key(app, wsBytes);
        lock (Gate)
        {
            bool fresh = Map.TryGetValue(key, out var e) && (DateTime.UtcNow - e.At).TotalDays <= MaxAgeDays;
            if (fresh && e!.Mb >= mb) return;
            Map[key] = new LearnedEntry { Mb = mb, At = DateTime.UtcNow };
            _dirty = true;
        }
        Logger.Write($"Learned: {app} (~{wsBytes / (1024 * 1024)} MB process) needs at least {mb} MB (remembered).");
    }

    /// <summary>Writes the file when something changed (at most every 30 s unless forced).</summary>
    public static void Flush(bool force)
    {
        lock (Gate)
        {
            if (!_dirty || (!force && (DateTime.UtcNow - _lastSave).TotalSeconds < 30)) return;
            _dirty = false;
            _lastSave = DateTime.UtcNow;
            try
            {
                Directory.CreateDirectory(ConfigStore.Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Map, Opt));
            }
            catch { }
        }
    }

    public static void Clear()
    {
        lock (Gate) { Map.Clear(); _dirty = true; }
        Flush(true);
    }
}
