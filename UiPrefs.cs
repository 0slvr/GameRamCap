using System.Text.Json;

namespace GameRamCap;

/// <summary>Look-and-feel choices the user makes (accent colour, which list columns show, row height).
/// Kept in their own small file next to config.json so they never mix with the engine's settings.</summary>
internal static class UiPrefs
{
    public sealed class Data
    {
        public string Accent { get; set; } = "Blue";
        public List<string> HiddenColumns { get; set; } = new() { "Proc." };   // the process count is shown in brackets after the app name
        public int RowHeight { get; set; } = 36;
        /// <summary>Share of the list width each column of the Apps list takes (stays in proportion when the window is resized).</summary>
        public List<double> ColumnWeights { get; set; } = new();
        /// <summary>Width (at 100% scaling) of each column of the Apps list, like Task Manager: dragging one header edge changes only that column.</summary>
        public List<double> ColumnPx { get; set; } = new();
        /// <summary>How often the Apps list refreshes: High (1 s), Normal (2 s), Low (4 s) or Paused (only the Refresh button).</summary>
        /// <summary>Where each column of the Apps list sits (its display position), after the user dragged headers around.</summary>
        public List<int> ColumnOrder { get; set; } = new();
        public string UpdateSpeed { get; set; } = "Normal";
        public bool AlwaysOnTop { get; set; }
        /// <summary>Resource values (right-click menu of the Apps list): show Memory / Disk / Network as a percent instead of MB and MB/s.</summary>
        public bool MemoryAsPercent { get; set; }
        public bool DiskAsPercent { get; set; }
        public bool NetAsPercent { get; set; }
        /// <summary>Minimise the window after an action that takes you to another app (Open file location, Start new task).</summary>
        public bool MinimizeOnUse { get; set; }
        /// <summary>When the window is minimised, hide it from the taskbar too (the tray icon opens it again).</summary>
        public bool HideWhenMinimized { get; set; }
    }

    public static readonly string[] Speeds = { "High", "Normal", "Low", "Paused" };
    /// <summary>Raised after a window or update-speed choice changed, so open windows can apply it at once.</summary>
    public static event Action? WindowPrefsChanged;
    public static void NotifyWindowPrefs() => WindowPrefsChanged?.Invoke();

    static string FilePath => Path.Combine(ConfigStore.Dir, "ui.json");
    static Data? _cache;

    public static Data Current => _cache ??= Load();

    static Data Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch { }
        return new Data();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
