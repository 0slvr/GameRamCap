using System.Runtime.InteropServices;

namespace GameRamCap;

/// <summary>Turns on SeDebugPrivilege for this process (needs administrator rights, which GameRamCap already runs with).
/// Without it Windows refuses to open many system processes (dwm, services...), so their RAM shows "?" and
/// End task / priority / limits fail on them. With it, most of them can be read and changed.</summary>
internal static class DebugPrivilege
{
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

    public static void Enable()
    {
        IntPtr tok = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out tok)) return;   // ADJUST_PRIVILEGES | QUERY
            if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out long luid)) return;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 };   // SE_PRIVILEGE_ENABLED
            bool ok = AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
            Logger.Write(ok ? "SeDebugPrivilege enabled: system processes can be read." : "SeDebugPrivilege not available (run GameRamCap as administrator).");
        }
        catch { }
        finally { if (tok != IntPtr.Zero) CloseHandle(tok); }
    }
}
