using System.Runtime.InteropServices;

namespace GameRamCap;

internal static class Native
{
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint PROCESS_SET_QUOTA = 0x0100;

    // Working-set quota flags (documented in SetProcessWorkingSetSizeEx).
    const uint HARDWS_MIN_DISABLE = 0x2;
    const uint HARDWS_MAX_ENABLE = 0x4;
    const uint HARDWS_MAX_DISABLE = 0x8;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessWorkingSetSizeEx(IntPtr process, out nuint min, out nuint max, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nuint min, nuint max, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Reads the current soft limits of a process (needed to restore it later).</summary>
    public static bool TryGetLimits(int pid, out nuint min, out nuint max)
    {
        min = max = 0;
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetProcessWorkingSetSizeEx(h, out min, out max, out _); }
        finally { CloseHandle(h); }
    }

    /// <summary>True when the process currently has a hard max working-set cap (e.g. left by a crashed run).</summary>
    public static bool HasHardCeiling(int pid, out nuint min, out nuint max)
    {
        min = max = 0;
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetProcessWorkingSetSizeEx(h, out min, out max, out uint flags) && (flags & HARDWS_MAX_ENABLE) != 0; }
        finally { CloseHandle(h); }
    }

    /// <summary>Applies a hard ceiling on the resident working set.</summary>
    public static bool SetCeiling(int pid, long ceilingBytes)
    {
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            const long minBytes = 1L * 1024 * 1024;
            return SetProcessWorkingSetSizeEx(h, (nuint)minBytes, (nuint)Math.Max(ceilingBytes, minBytes * 2),
                HARDWS_MIN_DISABLE | HARDWS_MAX_ENABLE);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Removes the hard ceiling and puts the original soft limits back.</summary>
    public static bool Release(int pid, nuint originalMin, nuint originalMax)
    {
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            // The original soft limits are Windows' tiny defaults (~1.3 MB max). Writing them back while
            // the process uses hundreds of MB makes Windows trim it almost to zero. So lift the cap by
            // giving a soft max well above the current usage instead of the tiny original value.
            long ws = 0;
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); ws = p.WorkingSet64; } catch { }
            long softMax = Math.Max((long)originalMax, Math.Max(ws * 2, 512L * 1024 * 1024));
            nuint max = (nuint)softMax;
            nuint min = originalMin < max ? originalMin : (nuint)(max / 2);
            return SetProcessWorkingSetSizeEx(h, min, max, HARDWS_MIN_DISABLE | HARDWS_MAX_DISABLE);
        }
        finally { CloseHandle(h); }
    }
}
