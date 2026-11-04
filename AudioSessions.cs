using System.Runtime.InteropServices;

namespace GameRamCap;

/// <summary>Finds which processes are playing or capturing sound right now, through the Windows Core Audio
/// session API (the same data the volume mixer shows). No extra packages needed.</summary>
internal static class AudioSessions
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        // IAudioSessionManager (base) methods keep their slots:
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingId, int streamFlags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingId, int streamFlags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl (base) methods:
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr value, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
        // IAudioSessionControl2:
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    /// <summary>Process ids that have an ACTIVE audio session (playing sound, or capturing the microphone)
    /// on the default playback / recording devices.</summary>
    public static HashSet<int> ActivePids()
    {
        var pids = new HashSet<int>();
        IMMDeviceEnumerator? en;
        try { en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }
        catch { return pids; }
        try
        {
            foreach (int flow in new[] { 0, 1 })        // 0 = playback, 1 = microphone
                foreach (int role in new[] { 0, 2 })    // 0 = console, 2 = communications (Discord calls)
                {
                    try { Collect(en, flow, role, pids); } catch { }
                }
        }
        finally { try { Marshal.ReleaseComObject(en); } catch { } }
        return pids;
    }

    static void Collect(IMMDeviceEnumerator en, int flow, int role, HashSet<int> pids)
    {
        if (en.GetDefaultAudioEndpoint(flow, role, out var dev) != 0 || dev == null) return;
        try
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) != 0) return;
            if (o is not IAudioSessionManager2 mgr) return;
            try
            {
                if (mgr.GetSessionEnumerator(out var list) != 0 || list == null) return;
                try
                {
                    if (list.GetCount(out int count) != 0) return;
                    for (int i = 0; i < count; i++)
                    {
                        if (list.GetSession(i, out var s) != 0 || s == null) continue;
                        try
                        {
                            // state 1 = active (sound is flowing right now); 0 = inactive, 2 = expired
                            if (s.GetState(out int state) == 0 && state == 1
                                && s.GetProcessId(out uint pid) == 0 && pid != 0)
                                pids.Add((int)pid);
                        }
                        finally { Marshal.ReleaseComObject(s); }
                    }
                }
                finally { Marshal.ReleaseComObject(list); }
            }
            finally { Marshal.ReleaseComObject(mgr); }
        }
        finally { Marshal.ReleaseComObject(dev); }
    }
}
