using System.Runtime.InteropServices;
using System.Text;

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

    static long _totalRam;
    /// <summary>Installed physical RAM in bytes (0 if unknown).</summary>
    public static long TotalRam()
    {
        if (_totalRam > 0) return _totalRam;
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref s)) _totalRam = (long)s.ullTotalPhys;
        return _totalRam;
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

    // ---- CPU time and I/O bytes of a process (used by the Apps tab columns) ----
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    /// <summary>Total CPU time (kernel + user, in 100 ns units) and total bytes read + written (files, pipes, network)
    /// of a process. Returns false when the process cannot be opened.</summary>
    public static bool GetCpuIo(int pid, out long cpu100ns, out long ioBytes)
    {
        cpu100ns = ioBytes = 0;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (GetProcessTimes(h, out _, out _, out var kernel, out var user)) cpu100ns = kernel + user;
            if (GetProcessIoCounters(h, out var io)) ioBytes = (long)(io.ReadTransferCount + io.WriteTransferCount);
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    /// <summary>Full path of the process' exe (works for most processes with only limited access). null when it cannot be read.</summary>
    public static string? GetImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

    [DllImport("shell32.dll")]
    static extern int SHQueryUserNotificationState(out int state);

    /// <summary>Windows' own "is a full-screen app in front" answer (QUNS_BUSY / D3D full screen / presentation).
    /// Returns true when it cannot be queried, so the caller falls back to its own window-size test.</summary>
    public static bool UserIsInFullscreenApp()
    {
        try
        {
            if (SHQueryUserNotificationState(out int s) != 0) return true;
            return s == 2 || s == 3 || s == 4;   // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
        }
        catch { return true; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);

    /// <summary>pid -> (parent pid, process name without .exe) for every running process, in one cheap snapshot.</summary>
    public static Dictionary<int, (int Parent, string Name)> ProcessMap()
    {
        var map = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
            {
                string n = e.szExeFile ?? "";
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
                map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, n);
            }
        }
        finally { CloseHandle(snap); }
        return map;
    }

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

    // ---- Foreground change event + prefetch (bring a trimmed app's pages back before you need them) ----
    const uint PROCESS_VM_OPERATION = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WIN32_MEMORY_RANGE_ENTRY { public IntPtr VirtualAddress; public nuint NumberOfBytes; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nuint VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION info, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PrefetchVirtualMemory(IntPtr process, nuint entries, [In] WIN32_MEMORY_RANGE_ENTRY[] ranges, uint flags);

    /// <summary>Asks Windows to read a process's committed pages back into RAM (from the standby cache, the page file or disk)
    /// in large efficient batches. Stops after maxBytes. Returns how many bytes were requested.</summary>
    public static long Prefetch(int pid, long maxBytes)
    {
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION | PROCESS_VM_OPERATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            var list = new List<WIN32_MEMORY_RANGE_ENTRY>();
            long total = 0;
            long addr = 0;
            const long MaxAddr = 0x7FFFFFFEFFFF;
            int guard = 0;
            while (addr < MaxAddr && total < maxBytes && guard++ < 200000)
            {
                if (VirtualQueryEx(h, (IntPtr)addr, out var mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) break;
                long size = (long)mbi.RegionSize;
                if (size <= 0) break;
                bool ok = mbi.State == 0x1000                       // MEM_COMMIT
                    && (mbi.Protect & 0x101) == 0                   // not PAGE_NOACCESS / PAGE_GUARD
                    && mbi.Protect != 0
                    && mbi.Type != 0x40000                      // not MEM_MAPPED (device / shared memory)
                    && size <= maxBytes - total;
                if (ok) { list.Add(new WIN32_MEMORY_RANGE_ENTRY { VirtualAddress = mbi.BaseAddress, NumberOfBytes = (nuint)size }); total += size; }
                long next = (long)mbi.BaseAddress + size;
                if (next <= addr) break;
                addr = next;
            }
            for (int i = 0; i < list.Count; i += 256)
            {
                var chunk = list.GetRange(i, Math.Min(256, list.Count - i)).ToArray();
                try { PrefetchVirtualMemory(h, (nuint)chunk.Length, chunk, 0); } catch { return total; }
            }
            return total;
        }
        finally { CloseHandle(h); }
    }

    // ---- Hot set: remember which pages a process had resident BEFORE it was squeezed, and read exactly those back later ----
    const uint PROCESS_VM_READ = 0x0010;

    [DllImport("kernel32.dll", EntryPoint = "K32QueryWorkingSet", SetLastError = true)]
    static extern bool K32QueryWorkingSet(IntPtr process, IntPtr buffer, uint size);

    /// <summary>The resident pages of a process at one moment, as sorted runs of virtual pages (start page number + page count).</summary>
    public sealed class ResidentSet
    {
        public ulong[] Starts = Array.Empty<ulong>();
        public uint[] Counts = Array.Empty<uint>();
        public long Pages;
        public DateTime Taken = DateTime.UtcNow;
        public DateTime ProcStart;
    }

    /// <summary>Reads the list of pages that are in the process' working set right now. null when it cannot be read or is empty.</summary>
    public static ResidentSet? CaptureResident(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            long entries = 1 << 16;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                long bytes = (entries + 1) * IntPtr.Size;
                if (bytes > 256L * 1024 * 1024) return null;
                buf = Marshal.AllocHGlobal((IntPtr)bytes);
                if (K32QueryWorkingSet(h, buf, (uint)bytes))
                {
                    long n = Marshal.ReadIntPtr(buf).ToInt64();
                    if (n <= 0 || n > entries) return null;
                    var raw = new long[n];
                    Marshal.Copy(IntPtr.Add(buf, IntPtr.Size), raw, 0, (int)n);
                    var pages = new ulong[n];
                    for (long i = 0; i < n; i++) pages[i] = (ulong)raw[i] >> 12;
                    Array.Sort(pages);
                    var starts = new List<ulong>(); var counts = new List<uint>();
                    ulong start = pages[0]; uint len = 1;
                    for (long i = 1; i < n; i++)
                    {
                        if (pages[i] == start + len) len++;
                        else if (pages[i] < start + len) continue;      // duplicate
                        else { starts.Add(start); counts.Add(len); start = pages[i]; len = 1; }
                    }
                    starts.Add(start); counts.Add(len);
                    return new ResidentSet { Starts = starts.ToArray(), Counts = counts.ToArray(), Pages = n };
                }
                int err = Marshal.GetLastWin32Error();
                long need = Marshal.ReadIntPtr(buf).ToInt64();           // ERROR_BAD_LENGTH: the first entry holds the number of entries needed
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                if (err != 24 || need <= 0) return null;
                entries = need + need / 4 + 1024;
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }
    }

    /// <summary>Asks Windows to read back exactly the pages of a saved resident set (only those that are still committed), up to maxBytes.
    /// Returns how many bytes were requested.</summary>
    public static long PrefetchResident(int pid, ResidentSet set, long maxBytes)
    {
        if (set.Starts.Length == 0 || maxBytes < 4096) return 0;
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION | PROCESS_VM_OPERATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            var list = new List<WIN32_MEMORY_RANGE_ENTRY>();
            long total = 0;
            int idx = 0, n = set.Starts.Length, guard = 0;
            long addr = 0;
            const long MaxAddr = 0x7FFFFFFEFFFF;
            while (addr < MaxAddr && total < maxBytes && idx < n && guard++ < 200000)
            {
                if (VirtualQueryEx(h, (IntPtr)addr, out var mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) break;
                long size = (long)mbi.RegionSize;
                if (size <= 0) break;
                ulong rb = (ulong)(long)mbi.BaseAddress, re = rb + (ulong)size;
                bool ok = mbi.State == 0x1000                       // MEM_COMMIT
                    && (mbi.Protect & 0x101) == 0                   // not PAGE_NOACCESS / PAGE_GUARD
                    && mbi.Protect != 0
                    && mbi.Type != 0x40000;                         // not MEM_MAPPED (device / shared memory)
                if (ok)
                {
                    while (idx < n && (set.Starts[idx] + set.Counts[idx]) * 4096UL <= rb) idx++;
                    for (int j = idx; j < n && set.Starts[j] * 4096UL < re && total < maxBytes; j++)
                    {
                        ulong s = Math.Max(set.Starts[j] * 4096UL, rb);
                        ulong e = Math.Min((set.Starts[j] + set.Counts[j]) * 4096UL, re);
                        if (e <= s) continue;
                        long len = Math.Min((long)(e - s), maxBytes - total) & ~4095L;
                        if (len <= 0) break;
                        list.Add(new WIN32_MEMORY_RANGE_ENTRY { VirtualAddress = (IntPtr)(long)s, NumberOfBytes = (nuint)len });
                        total += len;
                    }
                }
                long next = (long)rb + size;
                if (next <= addr) break;
                addr = next;
            }
            for (int i = 0; i < list.Count; i += 256)
            {
                var chunk = list.GetRange(i, Math.Min(256, list.Count - i)).ToArray();
                try { PrefetchVirtualMemory(h, (nuint)chunk.Length, chunk, 0); } catch { return total; }
            }
            return total;
        }
        finally { CloseHandle(h); }
    }

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    static WinEventProc? _fgProc;   // kept in a field so the garbage collector cannot free it while Windows still calls it
    static IntPtr _fgHook;

    /// <summary>Calls onChange the moment Windows switches the foreground window (no waiting for the next engine tick).
    /// Must be called from a thread that pumps messages (the UI thread).</summary>
    public static void WatchForeground(Action onChange)
    {
        if (_fgHook != IntPtr.Zero) return;
        _fgProc = (_, _, hwnd, idObject, _, _, _) => { if (idObject == 0 && hwnd != IntPtr.Zero) { try { onChange(); } catch { } } };
        _fgHook = SetWinEventHook(0x0003 /* EVENT_SYSTEM_FOREGROUND */, 0x0003, IntPtr.Zero, _fgProc, 0, 0, 0x0000 /* OUTOFCONTEXT */);
    }

    public static void StopWatchingForeground()
    {
        if (_fgHook != IntPtr.Zero) { UnhookWinEvent(_fgHook); _fgHook = IntPtr.Zero; }
    }

    // ---- Win+V / Win+. / Win+; / Win+H detector ----
    // The panel's windows look the same open or closed (cloak=2 all the time) and it never takes the foreground, so the only
    // reliable "it was just summoned" signal is the key combo itself. The hook only checks "Win held + one of these keys" and
    // stores a timestamp. No key is logged or kept. It passes every key on to Windows untouched.
    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);

    static LowLevelKeyboardProc? _msProc;
    static IntPtr _msHook;
    static long _markTicks;
    static volatile string _markLabel = "";
    /// <summary>Diagnostics only: last Esc / Enter / mouse click within 60 s after a Win+V press (a possible "panel closed" moment).</summary>
    public static long LastPanelMarkTicks => Interlocked.Read(ref _markTicks);
    public static string LastPanelMarkLabel => _markLabel;
    static void Mark(string label) { _markLabel = label; Interlocked.Exchange(ref _markTicks, DateTime.UtcNow.Ticks); }
    static bool NearPanelKey() { long k = Interlocked.Read(ref _panelKeyTicks); return k != 0 && DateTime.UtcNow.Ticks - k < TimeSpan.TicksPerSecond * 60; }

    static LowLevelKeyboardProc? _kbProc;   // kept in a field so the garbage collector cannot free it
    static IntPtr _kbHook;
    static long _panelKeyTicks;
    /// <summary>Raised (from the keyboard hook, so keep it fast) the instant Win+V / Win+. / Win+; / Win+H is pressed.</summary>
    public static Action? OnPanelKey;
    /// <summary>Raised (from the keyboard hook, so keep it fast) the instant Win, Win+S or Win+Q is pressed (Start menu / Search).</summary>
    public static Action? OnStartKey;
    /// <summary>Raised (from the keyboard hook, so keep it fast) the instant Tab is pressed while Alt is held (Alt+Tab).</summary>
    public static Action? OnAltTabKey;
    /// <summary>Raised (from the mouse hook, so keep it fast) when any mouse button (left, right, middle, X1/X2) goes down, with the screen position.</summary>
    public static Action<int, int>? OnMouseDown;

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

    /// <summary>UTC ticks of the last Win+V / Win+. / Win+; / Win+H press, or 0 if none yet.</summary>
    public static long LastInputPanelKeyTicks => Interlocked.Read(ref _panelKeyTicks);

    /// <summary>Must be called from a thread that pumps messages (the UI thread).</summary>
    public static void WatchInputPanelKeys(bool mouseMarks = false)
    {
        if (_kbHook != IntPtr.Zero) return;
        _kbProc = (nCode, wParam, lParam) =>
        {
            try
            {
                int msg = (int)wParam;
                if (nCode >= 0 && (msg == 0x0100 || msg == 0x0104))   // WM_KEYDOWN / WM_SYSKEYDOWN
                {
                    int vk = Marshal.ReadInt32(lParam);               // KBDLLHOOKSTRUCT.vkCode
                    if (vk == 0x5B || vk == 0x5C || ((vk == 0x53 || vk == 0x51) && ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0))   // Win, or Win+S / Win+Q
                    {
                        try { OnStartKey?.Invoke(); } catch { }
                    }
                    if (vk == 0x09 && (GetAsyncKeyState(0x12) & 0x8000) != 0)   // Tab with Alt held
                    {
                        try { OnAltTabKey?.Invoke(); } catch { }
                    }
                    if ((vk == 0x56 || vk == 0xBE || vk == 0xBA || vk == 0x48)   // V . ; H
                        && ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0)   // left or right Win held
                    {
                        Interlocked.Exchange(ref _panelKeyTicks, DateTime.UtcNow.Ticks);
                        try { OnPanelKey?.Invoke(); } catch { }
                    }
                    else if ((vk == 0x1B || vk == 0x0D) && NearPanelKey())   // Esc / Enter shortly after Win+V
                        Mark(vk == 0x1B ? "Esc" : "Enter");
                }
            }
            catch { }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        };
        _kbHook = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, _kbProc, GetModuleHandleW(null), 0);
        _msProc = (nCode, wParam, lParam) =>
        {
            try
            {
                int msg = (int)wParam;
                if (nCode >= 0 && (msg == 0x0201 || msg == 0x0204 || msg == 0x0207 || msg == 0x020B))   // left / right / middle / X1-X2 button down: tell the engine where (it does the work off the hook thread)
                {
                    var cb = OnMouseDown;
                    if (cb != null) cb(Marshal.ReadInt32(lParam), Marshal.ReadInt32(lParam, 4));   // MSLLHOOKSTRUCT.pt
                }
                if (mouseMarks && nCode >= 0 && (msg == 0x0201 || msg == 0x0204) && NearPanelKey()) Mark("click");   // diagnostics only
            }
            catch { }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        };
        _msHook = SetWindowsHookExW(14 /* WH_MOUSE_LL */, _msProc, GetModuleHandleW(null), 0);
    }

    public static void StopWatchingInputPanelKeys()
    {
        if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_msHook != IntPtr.Zero) { UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
    }

    // ---- Window activity of ANY process (no class names, nothing app-specific) ----
    // Windows raises these events for every app: a window shown, uncloaked, cloaked, or given focus. The diagnostics log showed that the
    // Win+V host raises them at both open and close, whatever its internal window classes are called. So "this process just did something
    // visible" works for new tools and survives Windows updates. It marks transitions, not how long a window stays open.
    static WinEventProc? _actProc;
    static readonly List<IntPtr> _actHooks = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _actTicks = new();

    /// <summary>Must be called from a thread that pumps messages (the UI thread).</summary>
    static Action? _onPanelOpened;

    /// <param name="onPanelOpened">Called (on the UI thread) the moment the Win+V panel takes keyboard focus.</param>
    public static void WatchWindowActivity(Action? onPanelOpened = null)
    {
        if (_actProc != null) return;
        _onPanelOpened = onPanelOpened;
        _actProc = (_, evt, hwnd, idObject, _, _, _) =>
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                if (evt != 0x0003 && evt != 0x8005 && idObject != 0) return;   // window-level events only (focus may come from a child control)
                if (evt == 0x8005 && _onPanelOpened != null)   // focus moved into the Win+V panel: lift its limit right now
                {
                    var cls = new StringBuilder(80);
                    GetClassNameW(hwnd, cls, 80);
                    if (cls.ToString() == "Windows.UI.Core.CoreComponentInputSource") _onPanelOpened();
                }
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0) _actTicks[(int)pid] = DateTime.UtcNow.Ticks;
            }
            catch { }
        };
        // foreground | show | focus | cloaked + uncloaked
        foreach (var (a, b) in new (uint, uint)[] { (0x0003, 0x0003), (0x8002, 0x8002), (0x8005, 0x8005), (0x8017, 0x8018) })
            _actHooks.Add(SetWinEventHook(a, b, IntPtr.Zero, _actProc, 0, 0, 0 /* OUTOFCONTEXT */));
    }

    public static void StopWatchingWindowActivity()
    {
        foreach (var h in _actHooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
        _actHooks.Clear();
        _actProc = null;
    }

    /// <summary>Process ids that showed / uncloaked / cloaked / focused a window within the last <paramref name="seconds"/> seconds.</summary>
    public static List<int> RecentlyActivePids(double seconds)
    {
        long now = DateTime.UtcNow.Ticks, limit = now - (long)(seconds * TimeSpan.TicksPerSecond);
        var list = new List<int>();
        foreach (var kv in _actTicks)
        {
            if (kv.Value >= limit) list.Add(kv.Key);
            else if (now - kv.Value > TimeSpan.TicksPerMinute * 5) _actTicks.TryRemove(kv.Key, out _);   // forget old entries
        }
        return list;
    }

    /// <summary>UTC ticks of the last window activity of a process, or 0 if none seen.</summary>
    public static long LastActivityTicks(int pid) => _actTicks.TryGetValue(pid, out var t) ? t : 0;

    // ---- Is the Win+V panel open right now? ----
    // Found with the diagnostics log: while the panel is open, a TextInputHost thread has keyboard focus inside a
    // "Windows.UI.Core.CoreComponentInputSource" window; closed (Esc / click outside), the focus is a plain "Windows.UI.Core.CoreWindow".
    // The foreground window never changes and the window flags (visible / cloak) of TextInputHost look the same either way.
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
            foreach (System.Diagnostics.ProcessThread t in p.Threads)
            {
                var gi = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
                if (!GetGuiThreadInfoNative((uint)t.Id, ref gi) || gi.hwndFocus == IntPtr.Zero) continue;
                var cls = new StringBuilder(80);
                GetClassNameW(gi.hwndFocus, cls, 80);
                if (cls.ToString() == "Windows.UI.Core.CoreComponentInputSource") return true;
            }
        }
        catch { }
        return false;
    }

    // ---- Is a process showing a real window right now? (hidden / cloaked UWP windows do not count) ----
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    /// <summary>True if the process owns a visible, not-cloaked top-level window (e.g. the Win+V flyout is open).</summary>
    public static bool HasVisibleWindow(int pid)
    {
        bool found = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint p);
            if (p != (uint)pid || !IsWindowVisible(hwnd)) return true;
            int cloaked = 0;
            DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out cloaked, sizeof(int));
            if (cloaked == 0 && GetWindowRect(hwnd, out var r) && r.Right - r.Left > 1 && r.Bottom - r.Top > 1) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

    /// <summary>Diagnostics: one line per top-level window of a process (class, title, visible, cloaked, size).</summary>
    public static List<string> DescribeWindows(int pid)
    {
        var list = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint p);
            if (p != (uint)pid) return true;
            var cls = new StringBuilder(80); GetClassNameW(hwnd, cls, 80);
            var txt = new StringBuilder(80); GetWindowTextW(hwnd, txt, 80);
            int cloaked = 0; DwmGetWindowAttribute(hwnd, 14, out cloaked, sizeof(int));
            GetWindowRect(hwnd, out var r);
            list.Add($"[{cls}|{txt}|vis={(IsWindowVisible(hwnd) ? 1 : 0)}|cloak={cloaked}|{r.Right - r.Left}x{r.Bottom - r.Top}]");
            return true;
        }, IntPtr.Zero);
        list.Sort(StringComparer.Ordinal);
        return list;
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

    // ---- Thread memory priority (same idea as the process one, per thread) ----
    const uint THREAD_SET_INFORMATION = 0x0020;
    const int ThreadMemoryPriority = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenThread(uint access, bool inherit, uint tid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetThreadInformation(IntPtr thread, int infoClass, ref MEMORY_PRIORITY_INFORMATION info, uint size);

    /// <summary>Sets the memory priority (1 very low ... 5 normal) of every thread of a process. Returns true if at least one thread changed.</summary>
    public static bool SetThreadMemoryPriority(int pid, uint level)
    {
        int ok = 0;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            foreach (System.Diagnostics.ProcessThread t in p.Threads)
            {
                var h = OpenThread(THREAD_SET_INFORMATION, false, (uint)t.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = level };
                    if (SetThreadInformation(h, ThreadMemoryPriority, ref info, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>())) ok++;
                }
                finally { CloseHandle(h); }
            }
        }
        catch { }
        return ok > 0;
    }
}
