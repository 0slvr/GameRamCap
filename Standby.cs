using System.Runtime.InteropServices;

namespace GameRamCap;

/// <summary>Clears the Windows standby list (cached pages that are kept "just in case"). Nothing of any process is changed:
/// Windows simply forgets cache, and re-reads from disk what is needed again. Needs administrator rights (GameRamCap runs elevated).</summary>
internal static class StandbyCleaner
{
    [DllImport("ntdll.dll")]
    static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, IntPtr previous, IntPtr returnLength);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attributes; }

    const int SystemMemoryListInformation = 80;
    const int MemoryPurgeStandbyList = 4;
    static bool _privilege, _privilegeFailed;
    static PdhQuery? _q;
    static IntPtr _free, _reserve, _normal, _core;
    static bool _counterFailed;

    static bool EnablePrivilege()
    {
        if (_privilege) return true;
        if (_privilegeFailed) return false;
        IntPtr tok = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x28 /* ADJUST_PRIVILEGES | QUERY */, out tok)) { _privilegeFailed = true; return false; }
            if (!LookupPrivilegeValueW(null, "SeProfileSingleProcessPrivilege", out long luid)) { _privilegeFailed = true; return false; }
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            bool ok = AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
            if (!ok) { _privilegeFailed = true; Logger.Write("Standby cleaner: no permission for SeProfileSingleProcessPrivilege (is GameRamCap running as administrator?)."); }
            return _privilege = ok;
        }
        catch { _privilegeFailed = true; return false; }
        finally { if (tok != IntPtr.Zero) CloseHandle(tok); }
    }

    /// <summary>Free (free + zeroed) memory and standby cache in bytes. Note: "available RAM" includes the standby list,
    /// so only these two numbers say whether clearing it makes sense.</summary>
    public static bool TryRead(out long freeBytes, out long standbyBytes)
    {
        freeBytes = standbyBytes = 0;
        if (_counterFailed) return false;
        if (_q == null)
        {
            _q = PdhQuery.TryOpen();
            if (_q != null)
            {
                _free = _q.Add(@"\Memory\Free & Zero Page List Bytes");
                _reserve = _q.Add(@"\Memory\Standby Cache Reserve Bytes");
                _normal = _q.Add(@"\Memory\Standby Cache Normal Priority Bytes");
                _core = _q.Add(@"\Memory\Standby Cache Core Bytes");
            }
            if (_q == null || _free == IntPtr.Zero || _normal == IntPtr.Zero)
            {
                _q?.Dispose(); _q = null; _counterFailed = true;
                Logger.Write("Standby cleaner: the memory performance counters are not available on this PC.");
                return false;
            }
        }
        if (!_q.Collect()) return false;
        double f = _q.Value(_free) ?? -1;
        if (f < 0) return false;
        double sb = (_reserve != IntPtr.Zero ? _q.Value(_reserve) ?? 0 : 0) + (_q.Value(_normal) ?? 0) + (_core != IntPtr.Zero ? _q.Value(_core) ?? 0 : 0);
        freeBytes = (long)f;
        standbyBytes = (long)sb;
        return true;
    }

    public static bool Purge()
    {
        if (!EnablePrivilege()) return false;
        int cmd = MemoryPurgeStandbyList;
        try { return NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0; }
        catch { return false; }
    }

    /// <summary>One-shot "Boost" (like RAMMap's Empty menu / PC Manager's Boost): empties the working set of every process,
    /// writes the modified list to disk, then clears the standby list. Nothing is lost; apps re-read what they need,
    /// so the next seconds can feel a bit slower. Needs administrator rights.</summary>
    public static bool Boost()
    {
        if (!EnablePrivilege()) return false;
        bool any = false;
        foreach (int cmd in new[] { 2 /* MemoryEmptyWorkingSets */, 3 /* MemoryFlushModifiedList */, 4 /* MemoryPurgeStandbyList */ })
        {
            int c = cmd;
            try { any |= NtSetSystemInformation(SystemMemoryListInformation, ref c, sizeof(int)) == 0; } catch { }
        }
        return any;
    }

    public static void Close() { _q?.Dispose(); _q = null; _counterFailed = false; }
}

// While a game runs and FREE memory is low but the standby list is big, Windows is about to evict useful pages
// or stall on allocation; clearing the standby list gives the memory back at once. At most once per interval.
internal sealed partial class Engine
{
    DateTime _standbyCheck = DateTime.MinValue, _standbyLast = DateTime.MinValue;

    void TickStandby()
    {
        if (!Config.StandbyClean || !Enabled || !Config.Enabled) return;
        var now = DateTime.UtcNow;
        if ((now - _standbyCheck).TotalSeconds < 5) return;   // counters are cheap, but no need to read them every tick
        _standbyCheck = now;
        if ((now - _standbyLast).TotalSeconds < Math.Max(10, Config.StandbyMinIntervalSeconds)) return;
        if (!IsGaming()) return;
        if (!StandbyCleaner.TryRead(out long free, out long standby)) return;
        if (free > Config.StandbyFreeBelowMB * MB || standby < Config.StandbyMinMB * MB) return;
        _standbyLast = now;
        bool ok = StandbyCleaner.Purge();
        Logger.Write(ok
            ? $"Standby cleaner: cleared {standby / MB} MB of standby cache (free RAM was {free / MB} MB) while {GameName} runs."
            : "Standby cleaner: Windows refused to clear the standby list.");
    }
}
