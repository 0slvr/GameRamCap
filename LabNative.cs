using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameRamCap;

/// <summary>One process as reported by NtQuerySystemInformation(SystemProcessInformation).
/// Reading this list opens NO process handle (safe next to anti-cheat).</summary>
internal sealed class ProcRow
{
    public int Pid, ParentPid, Threads;
    public string Name = "";
    public uint HardFaults;                 // REAL hard faults (pages read from disk), per process
    public long WsBytes, PrivateBytes, CpuTicks, CreateTime;
    public double Cpu = -1;                 // fraction of one core, -1 = unknown yet (filled by Engine)
}

internal static class LabNative
{
    // ---------------------------------------------------------------- process list
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    static IntPtr _buf = IntPtr.Zero;
    static int _bufLen = 1 << 20;

    /// <summary>x64 layout of SYSTEM_PROCESS_INFORMATION (offsets as used by Process Hacker):
    /// 0 Next, 4 Threads, 8 WorkingSetPrivate, 16 HardFaultCount, 32 CreateTime, 40 User, 48 Kernel,
    /// 56 ImageName(UNICODE_STRING), 80 PID, 88 ParentPID, 144 WorkingSetSize, 200 PrivatePageCount.</summary>
    public static List<ProcRow> Snapshot()
    {
        var list = new List<ProcRow>(400);
        if (IntPtr.Size != 8) return list;
        try
        {
            if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(_bufLen);
            int ret, status;
            while ((status = NtQuerySystemInformation(5, _buf, _bufLen, out ret)) == unchecked((int)0xC0000004))
            {
                Marshal.FreeHGlobal(_buf);
                _bufLen = Math.Max(_bufLen * 2, ret + 65536);
                _buf = Marshal.AllocHGlobal(_bufLen);
            }
            if (status != 0) return list;

            long off = 0;
            while (true)
            {
                IntPtr p = _buf + (int)off;
                int next = Marshal.ReadInt32(p, 0);
                var r = new ProcRow
                {
                    Threads = Marshal.ReadInt32(p, 4),
                    HardFaults = unchecked((uint)Marshal.ReadInt32(p, 16)),
                    CreateTime = Marshal.ReadInt64(p, 32),
                    CpuTicks = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48),
                    Pid = (int)Marshal.ReadIntPtr(p, 80),
                    ParentPid = (int)Marshal.ReadIntPtr(p, 88),
                    WsBytes = Marshal.ReadInt64(p, 144),
                    PrivateBytes = Marshal.ReadInt64(p, 200),
                };
                int chars = Marshal.ReadInt16(p, 56) / 2;
                IntPtr s = Marshal.ReadIntPtr(p, 64);
                string name = s != IntPtr.Zero && chars > 0 ? (Marshal.PtrToStringUni(s, chars) ?? "") : "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                r.Name = name;
                list.Add(r);
                if (next == 0) break;
                off += next;
            }
        }
        catch { }
        return list;
    }

    // ---------------------------------------------------------------- handles
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenThread(uint access, bool inherit, uint tid);

    // ---------------------------------------------------------------- suspend / resume (E8)
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr h);
    const uint PROCESS_SUSPEND_RESUME = 0x0800;

    public static bool Suspend(int pid)
    {
        var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return NtSuspendProcess(h) == 0; } finally { CloseHandle(h); }
    }

    public static bool Resume(int pid)
    {
        var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return NtResumeProcess(h) == 0; } finally { CloseHandle(h); }
    }

    // ---------------------------------------------------------------- soft working-set max (E2s)
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nuint min, nuint max, uint flags);

    /// <summary>Soft maximum (QUOTA_LIMITS_HARDWS_MIN_DISABLE | HARDWS_MAX_DISABLE): Windows only enforces it under memory pressure.</summary>
    public static bool SetSoftMax(int pid, long maxBytes)
    {
        var h = OpenProcess(0x0400 | 0x0100, false, pid);   // QUERY_INFORMATION | SET_QUOTA
        if (h == IntPtr.Zero) return false;
        try { return SetProcessWorkingSetSizeEx(h, (nuint)(1L << 20), (nuint)Math.Max(maxBytes, 2L << 20), 0x2 | 0x8); }
        finally { CloseHandle(h); }
    }

    // ---------------------------------------------------------------- thread memory priority (E5)
    [StructLayout(LayoutKind.Sequential)] struct MEM_PRIO { public uint MemoryPriority; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetThreadInformation(IntPtr t, int cls, ref MEM_PRIO i, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetThreadInformation(IntPtr t, int cls, ref MEM_PRIO i, uint size);
    [DllImport("kernel32.dll")] static extern int GetThreadDescription(IntPtr t, out IntPtr desc);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
    const uint THREAD_SET_INFORMATION = 0x0020, THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    struct THREADENTRY32 { public uint dwSize, cntUsage, th32ThreadID, th32OwnerProcessID; public int tpBasePri, tpDeltaPri; public uint dwFlags; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 te);
    [DllImport("kernel32.dll")] static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 te);

    public static List<int> ThreadIds(int pid)
    {
        var res = new List<int>();
        IntPtr snap = CreateToolhelp32Snapshot(0x4 /* TH32CS_SNAPTHREAD */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return res;
        try
        {
            var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (!Thread32First(snap, ref te)) return res;
            do
            {
                if (te.th32OwnerProcessID == (uint)pid) res.Add((int)te.th32ThreadID);
                te.dwSize = (uint)Marshal.SizeOf<THREADENTRY32>();
            } while (Thread32Next(snap, ref te));
        }
        finally { CloseHandle(snap); }
        return res;
    }

    /// <summary>Reads memory priority + thread description of ANOTHER process' thread. err = Win32 error if it fails
    /// (5 = access denied, 87 = invalid parameter ...). This answers "does SetThreadInformation work externally?".</summary>
    public static bool GetThreadMemPrio(int tid, out uint level, out int err, out string desc)
    {
        level = 5; err = 0; desc = "";
        IntPtr h = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION, false, (uint)tid);
        if (h == IntPtr.Zero) { err = Marshal.GetLastWin32Error(); return false; }
        try
        {
            try
            {
                if (GetThreadDescription(h, out IntPtr d) == 0 && d != IntPtr.Zero)
                {
                    desc = Marshal.PtrToStringUni(d) ?? "";
                    LocalFree(d);
                }
            }
            catch { }
            var info = new MEM_PRIO();
            if (!GetThreadInformation(h, 0 /* ThreadMemoryPriority */, ref info, (uint)Marshal.SizeOf<MEM_PRIO>()))
            { err = Marshal.GetLastWin32Error(); return false; }
            level = info.MemoryPriority;
            return true;
        }
        finally { CloseHandle(h); }
    }

    public static bool SetThreadMemPrio(int tid, uint level, out int err)
    {
        err = 0;
        IntPtr h = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION, false, (uint)tid);
        if (h == IntPtr.Zero) { err = Marshal.GetLastWin32Error(); return false; }
        try
        {
            var info = new MEM_PRIO { MemoryPriority = level };
            if (SetThreadInformation(h, 0, ref info, (uint)Marshal.SizeOf<MEM_PRIO>())) return true;
            err = Marshal.GetLastWin32Error();
            return false;
        }
        finally { CloseHandle(h); }
    }

    // ---------------------------------------------------------------- windows (visibility, responsiveness, focus hook)
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeoutMs, out IntPtr result);

    /// <summary>Top-level windows that are really on screen: visible, not minimized, not cloaked (other virtual desktop),
    /// has a title, not a tool window, and not completely covered by the foreground window (guess for "hidden behind the game").</summary>
    public static List<(IntPtr Hwnd, int Pid)> VisibleWindows(IntPtr fg)
    {
        var res = new List<(IntPtr, int)>();
        Native.RECT fr = default;
        bool hasFg = fg != IntPtr.Zero && Native.GetWindowRect(fg, out fr);
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
                if (GetWindowTextLength(h) == 0) return true;
                long ex = GetWindowLongPtr(h, -20).ToInt64();
                if ((ex & 0x80) != 0 && (ex & 0x40000) == 0) return true;      // WS_EX_TOOLWINDOW without APPWINDOW
                if (!Native.GetWindowRect(h, out var r)) return true;
                if (r.Right - r.Left < 80 || r.Bottom - r.Top < 60) return true;
                if (hasFg && h != fg && r.Left >= fr.Left && r.Top >= fr.Top && r.Right <= fr.Right && r.Bottom <= fr.Bottom) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid != 0) res.Add((h, (int)pid));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return res;
    }

    /// <summary>Milliseconds from startTimestamp (Stopwatch ticks) until the window's message loop answers a WM_NULL
    /// (SMTO_ABORTIFHUNG). This measures "UI thread is alive again", NOT frame smoothness. -1 = no answer in time.</summary>
    public static double WaitResponsive(IntPtr hwnd, long startTimestamp, int timeoutMs)
    {
        if (hwnd == IntPtr.Zero) return -1;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            IntPtr r = SendMessageTimeout(hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0x2 /* ABORTIFHUNG */, 250, out _);
            if (r != IntPtr.Zero)
                return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            Thread.Sleep(5);
        }
        return -1;
    }

    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
}
