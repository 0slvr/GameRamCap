using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameRamCap;

/// <summary>Windows services through the Service Control Manager (advapi32), without an extra package.
/// Used by WindowsUpdatePause only.</summary>
internal static class ServiceApi
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ChangeServiceConfigW(IntPtr service, uint type, uint start, uint errorControl, string? binary, string? group,
        IntPtr tag, string? deps, string? user, string? password, string? display);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ChangeServiceConfig2W(IntPtr service, uint level, ref SERVICE_DELAYED_AUTO_START_INFO info);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool StartServiceW(IntPtr service, uint argc, IntPtr argv);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS { public uint Type, State, Accepts, ExitCode, ServiceExitCode, CheckPoint, WaitHint; }

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_DELAYED_AUTO_START_INFO { public int Delayed; }

    const uint SC_MANAGER_CONNECT = 0x1;
    const uint SERVICE_QUERY_STATUS = 0x4, SERVICE_CHANGE_CONFIG = 0x2, SERVICE_START = 0x10, SERVICE_STOP = 0x20;
    const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    const int ERROR_SERVICE_ALREADY_RUNNING = 1056, ERROR_SERVICE_NOT_ACTIVE = 1062;

    public const int StartAuto = 2, StartManual = 3, StartDisabled = 4;
    public const uint StateRunning = 4;

    static string Err() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    /// <summary>The start type from the registry: (2 automatic, 3 manual, 4 disabled), delayed flag. null if the service does not exist.</summary>
    public static (int start, bool delayed)? StartType(string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            if (k == null) return null;
            return (k.GetValue("Start") is int i ? i : 3, k.GetValue("DelayedAutostart") is int d && d == 1);
        }
        catch { return null; }
    }

    public static uint? State(string name)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var svc = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return null;
            var buf = Marshal.AllocHGlobal(64);
            try { return QueryServiceStatusEx(svc, 0, buf, 64, out _) ? (uint)Marshal.ReadInt32(buf, 4) : null; }   // SERVICE_STATUS_PROCESS.dwCurrentState
            finally { Marshal.FreeHGlobal(buf); CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    static string? With(string name, uint access, Func<IntPtr, string?> act)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return Err();
        try
        {
            var svc = OpenServiceW(scm, name, access);
            if (svc == IntPtr.Zero) return Err();
            try { return act(svc); }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Returns null when it worked, otherwise the Windows error text.</summary>
    public static string? SetStartType(string name, int start, bool delayed) =>
        With(name, SERVICE_CHANGE_CONFIG, svc =>
        {
            if (!ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, (uint)start, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null)) return Err();
            if (start == StartAuto)
            {
                var info = new SERVICE_DELAYED_AUTO_START_INFO { Delayed = delayed ? 1 : 0 };
                ChangeServiceConfig2W(svc, 3 /* SERVICE_CONFIG_DELAYED_AUTO_START_INFO */, ref info);
            }
            return null;
        });

    public static string? Start(string name) =>
        With(name, SERVICE_START, svc => StartServiceW(svc, 0, IntPtr.Zero) || Marshal.GetLastWin32Error() == ERROR_SERVICE_ALREADY_RUNNING ? null : Err());

    public static string? Stop(string name) =>
        With(name, SERVICE_STOP, svc => ControlService(svc, 1 /* SERVICE_CONTROL_STOP */, out _) || Marshal.GetLastWin32Error() == ERROR_SERVICE_NOT_ACTIVE ? null : Err());
}
