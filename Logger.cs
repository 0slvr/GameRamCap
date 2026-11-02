using System.Diagnostics;

namespace GameRamCap;

/// <summary>Tiny text log at %APPDATA%\GameRamCap\log.txt (restarts when it passes 512 KB).</summary>
internal static class Logger
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(ConfigStore.Dir, "log.txt");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ConfigStore.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }

    public static void Open()
    {
        try
        {
            if (!File.Exists(FilePath)) Write("Log created.");
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
        }
        catch { }
    }
}
