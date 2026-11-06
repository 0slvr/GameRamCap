using System.Reflection;

namespace GameRamCap;

/// <summary>The app's icon (app.ico, embedded): used by the exe, the tray icon and every window, so it is the same everywhere.</summary>
internal static class AppIcon
{
    static byte[]? _data;
    static Icon? _window, _tray;

    static byte[]? Data()
    {
        if (_data != null) return _data;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return _data = ms.ToArray();
        }
        catch { return null; }
    }

    static Icon Make(Size size)
    {
        var d = Data();
        if (d == null) return SystemIcons.Application;
        try { using var ms = new MemoryStream(d); return new Icon(ms, size); }
        catch { return SystemIcons.Application; }
    }

    /// <summary>For windows (Form.Icon): the title bar, the taskbar and Alt+Tab pick the right size from it.</summary>
    public static Icon Window => _window ??= Make(SystemInformation.IconSize);

    /// <summary>For the tray: the small size the notification area uses at the current DPI.</summary>
    public static Icon Tray => _tray ??= Make(SystemInformation.SmallIconSize);
}
