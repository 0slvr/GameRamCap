using System.Runtime.InteropServices;

namespace GameRamCap;

/// <summary>Turns on one Windows privilege (SeDebugPrivilege, SeProfileSingleProcessPrivilege...) for this process.
/// The token handle is always closed.</summary>
internal static class Privilege
{
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, IntPtr previous, IntPtr returnLength);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attributes; }

    /// <summary>True when the privilege is now enabled (it must already be in the token: administrator rights).</summary>
    public static bool Enable(string name)
    {
        IntPtr tok = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x28 /* ADJUST_PRIVILEGES | QUERY */, out tok)) return false;
            if (!LookupPrivilegeValueW(null, name, out long luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            // ERROR_NOT_ALL_ASSIGNED comes back as "success" with last error 1300: the privilege was not in the token
            return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        catch { return false; }
        finally { if (tok != IntPtr.Zero) Native.CloseHandle(tok); }
    }
}
