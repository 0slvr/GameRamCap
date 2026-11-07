using System.Runtime.InteropServices;
using System.Text;

namespace GameRamCap;

// Windows and screen points: who owns the window under the mouse, is the taskbar there, is the Win+V panel open.
internal static partial class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

    [StructLayout(LayoutKind.Sequential)]
    struct CLICKPT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(CLICKPT pt);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>The process that owns the top-level window under a screen point, or 0.</summary>
    public static int PidAtPoint(int x, int y)
    {
        try
        {
            var h = WindowFromPoint(new CLICKPT { X = x, Y = y });
            if (h == IntPtr.Zero) return 0;
            var root = GetAncestor(h, 2 /* GA_ROOT */);
            GetWindowThreadProcessId(root != IntPtr.Zero ? root : h, out uint pid);
            return (int)pid;
        }
        catch { return 0; }
    }

    /// <summary>True when the screen point is on the taskbar (Start button, search box, tray...), whose window is owned by explorer.</summary>
    public static bool IsTaskbarAt(int x, int y)
    {
        try
        {
            var h = WindowFromPoint(new CLICKPT { X = x, Y = y });
            if (h == IntPtr.Zero) return false;
            var root = GetAncestor(h, 2 /* GA_ROOT */);
            var cls = new StringBuilder(80);
            GetClassNameW(root != IntPtr.Zero ? root : h, cls, 80);
            string c = cls.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
        }
        catch { return false; }
    }

    // ---- Is the Win+V panel open right now? ----
    // While the panel is open, a TextInputHost thread has keyboard focus inside a "Windows.UI.Core.CoreComponentInputSource" window;
    // closed (Esc / click outside), the focus is a plain "Windows.UI.Core.CoreWindow". The foreground window never changes for it.
    [StructLayout(LayoutKind.Sequential)] struct GuiRect { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public GuiRect rcCaret;
    }

    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo")]
    static extern bool GetGuiThreadInfoNative(uint idThread, ref GuiThreadInfo info);

    /// <summary>True if one of the process's threads has keyboard focus in a CoreComponentInputSource window (panel open).</summary>
    public static bool IsInputPanelOpen(System.Diagnostics.Process p)
    {
        try
        {
            var cls = new StringBuilder(80);
            // p came from GetProcessesByName, so its thread list is already in the process snapshot: no extra system call here
            foreach (System.Diagnostics.ProcessThread t in p.Threads)
            {
                var gi = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
                if (!GetGuiThreadInfoNative((uint)t.Id, ref gi) || gi.hwndFocus == IntPtr.Zero) continue;
                cls.Clear();
                GetClassNameW(gi.hwndFocus, cls, 80);
                if (cls.ToString() == "Windows.UI.Core.CoreComponentInputSource") return true;
            }
        }
        catch { }
        return false;
    }
}
