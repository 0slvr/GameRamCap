using System.Runtime.InteropServices;
using System.Text;

namespace GameRamCap;

internal static partial class Native
{
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint PROCESS_SET_QUOTA = 0x0100;

    // Working-set quota flags (documented in SetProcessWorkingSetSizeEx).
    const uint HARDWS_MIN_DISABLE = 0x2;
    const uint HARDWS_MAX_ENABLE = 0x4;
    const uint HARDWS_MAX_DISABLE = 0x8;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

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
    static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] name, ref uint size);

    /// <summary>When the process started (local time, the same value as Process.StartTime), or null if it cannot be read / is gone.
    /// One handle and one call: no Process object, no list of all PIDs.</summary>
    public static DateTime? GetStartTime(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessTimes(h, out long creation, out _, out _, out _) || creation <= 0) return null;
            return DateTime.FromFileTime(creation);
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    /// <summary>Full path of the process' exe (works for most processes with only limited access). null when it cannot be read.</summary>
    public static string? GetImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            foreach (int len in new[] { 1024, 32768 })   // 32768 = the longest path Windows allows
            {
                var buf = new char[len];
                uint size = (uint)len;
                if (QueryFullProcessImageNameW(h, 0, buf, ref size)) return new string(buf, 0, (int)size);
                if (Marshal.GetLastWin32Error() != 122 /* ERROR_INSUFFICIENT_BUFFER */) return null;
            }
            return null;
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
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);   // reading needs no write right
        if (h == IntPtr.Zero) return false;
        try { return GetProcessWorkingSetSizeEx(h, out min, out max, out _); }
        finally { CloseHandle(h); }
    }

    /// <summary>True when the process currently has a hard max working-set cap (e.g. left by a crashed run).</summary>
    public static bool HasHardCeiling(int pid, out nuint min, out nuint max)
    {
        min = max = 0;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetProcessWorkingSetSizeEx(h, out min, out max, out uint flags) && (flags & HARDWS_MAX_ENABLE) != 0; }
        finally { CloseHandle(h); }
    }

    /// <summary>Applies a hard ceiling on the resident working set.</summary>
    public static bool SetCeiling(int pid, long ceilingBytes)
    {
        var h = OpenProcess(PROCESS_SET_QUOTA, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            const long minBytes = 1L * 1024 * 1024;
            bool ok = SetProcessWorkingSetSizeEx(h, (nuint)minBytes, (nuint)Math.Max(ceilingBytes, minBytes * 2),
                HARDWS_MIN_DISABLE | HARDWS_MAX_ENABLE);
            if (ok) CeilingLedger.Add(pid);   // so a crash can be cleaned up without touching other programs' caps
            return ok;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Removes the hard ceiling and puts the original soft limits back.</summary>
    public static bool Release(int pid, nuint originalMin, nuint originalMax)
    {
        var h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            // The original soft limits are Windows' tiny defaults (~1.3 MB max). Writing them back while
            // the process uses hundreds of MB makes Windows trim it almost to zero. So lift the cap by
            // giving a soft max well above the current usage instead of the tiny original value.
            long ws = 0;
            var counters = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
            if (K32GetProcessMemoryInfo(h, ref counters, counters.cb)) ws = (long)counters.WorkingSetSize;   // same handle: no Process object
            long softMax = Math.Max((long)originalMax, Math.Max(ws * 2, 512L * 1024 * 1024));
            nuint max = (nuint)softMax;
            nuint min = originalMin < max ? originalMin : (nuint)(max / 2);
            bool ok = SetProcessWorkingSetSizeEx(h, min, max, HARDWS_MIN_DISABLE | HARDWS_MAX_DISABLE);
            if (ok) CeilingLedger.Remove(pid);
            return ok;
        }
        finally { CloseHandle(h); }
    }

    // ---- Foreground change event + prefetch (bring a trimmed app's pages back before you need them) ----
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
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION, false, pid);
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
                if (bytes > 64L * 1024 * 1024) return null;   // 8 million pages = 32 GB of resident memory: more than we want to copy and sort
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
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION, false, pid);
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

    /// <summary>Sets the memory priority (1 very low ... 5 normal) of every thread of a process. Returns true if at least one thread changed.
    /// The thread list comes from one cached system snapshot, not from a Process object per app.</summary>
    public static bool SetThreadMemoryPriority(int pid, uint level)
    {
        int ok = 0;
        try
        {
            foreach (uint tid in ThreadIds(pid, 1500))
            {
                var h = OpenThread(THREAD_SET_INFORMATION, false, tid);
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

    // ---- thread ids of all processes in one snapshot (cached for a moment) ----
    [StructLayout(LayoutKind.Sequential)]
    struct THREADENTRY32 { public uint dwSize, cntUsage, th32ThreadID, th32OwnerProcessID; public int tpBasePri, tpDeltaPri; public uint dwFlags; }

    [DllImport("kernel32.dll")]
    static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 e);
    [DllImport("kernel32.dll")]
    static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 e);

    static readonly object _thrLock = new();
    static Dictionary<int, List<uint>> _thrMap = new();
    static long _thrMapAt = long.MinValue / 2;

    static List<uint> ThreadIds(int pid, int maxAgeMs)
    {
        lock (_thrLock)
        {
            long now = Environment.TickCount64;
            if (now - _thrMapAt > maxAgeMs) { _thrMap = SnapshotThreads(); _thrMapAt = now; }
            return _thrMap.TryGetValue(pid, out var l) ? l : new List<uint>();
        }
    }

    static Dictionary<int, List<uint>> SnapshotThreads()
    {
        var map = new Dictionary<int, List<uint>>();
        var snap = CreateToolhelp32Snapshot(0x4 /* TH32CS_SNAPTHREAD */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            uint size = (uint)Marshal.SizeOf<THREADENTRY32>();
            var e = new THREADENTRY32 { dwSize = size };
            for (bool ok = Thread32First(snap, ref e); ok; ok = Thread32Next(snap, ref e))
            {
                int owner = (int)e.th32OwnerProcessID;
                if (!map.TryGetValue(owner, out var l)) map[owner] = l = new List<uint>();
                l.Add(e.th32ThreadID);
                e.dwSize = size;   // Thread32Next may shrink it
            }
        }
        finally { CloseHandle(snap); }
        return map;
    }
}
