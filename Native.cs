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

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool K32EmptyWorkingSet(IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                     QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool K32GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Percent of physical RAM that is available (free + standby cache). -1 on failure.</summary>
    public static double FreeRamPercent()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref s) || s.ullTotalPhys == 0) return -1;
        return 100.0 * s.ullAvailPhys / s.ullTotalPhys;
    }

    /// <summary>Current working set and cumulative page-fault count of a process.</summary>
    public static bool GetMemory(int pid, out long workingSet, out long pageFaults)
    {
        workingSet = pageFaults = 0;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var c = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
            if (!K32GetProcessMemoryInfo(h, ref c, c.cb)) return false;
            workingSet = (long)c.WorkingSetSize;
            pageFaults = c.PageFaultCount;
            return true;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Soft trim: moves the process' pages to the standby list. No cap is set, the process
    /// can take any page back at once (a cheap soft fault, no disk) if it needs it.</summary>
    public static bool Trim(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return K32EmptyWorkingSet(h); }
        finally { CloseHandle(h); }
    }

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

    // ---- Memory priority (Windows decides which pages to take first when RAM is short) ----
    const uint PROCESS_SET_INFORMATION = 0x0200;
    const int ProcessMemoryPriority = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref MEMORY_PRIORITY_INFORMATION info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr process, int infoClass, ref MEMORY_PRIORITY_INFORMATION info, uint size);

    /// <summary>Current memory priority of a process: 1 (very low) ... 5 (normal, the default).</summary>
    public static bool GetMemoryPriority(int pid, out uint level)
    {
        level = 5;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var info = new MEMORY_PRIORITY_INFORMATION();
            if (!GetProcessInformation(h, ProcessMemoryPriority, ref info, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>())) return false;
            level = info.MemoryPriority;
            return true;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Sets the memory priority (1 very low ... 5 normal). A low priority makes Windows take this process'
    /// pages first when RAM is short, with no cap and no extra page faults while RAM is free.</summary>
    public static bool SetMemoryPriority(int pid, uint level)
    {
        var h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = level };
            return SetProcessInformation(h, ProcessMemoryPriority, ref info, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());
        }
        finally { CloseHandle(h); }
    }
}
