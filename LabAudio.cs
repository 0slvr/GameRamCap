using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameRamCap;

/// <summary>Audio / microphone / camera / capture detection used by the Lab.
///  1) WASAPI: every ACTIVE playback AND capture device (not only the default ones) -> IAudioSessionManager2 ->
///     sessions -> GetProcessId / GetState.
///  2) IAudioSessionNotification: catches sessions created after our scan (the enumerator can miss new sessions).
///  3) Windows privacy "ConsentStore" (the data behind the mic/camera tray icon): who uses mic, webcam, screen capture.
///     This is a registry trick, widely used but NOT officially documented API - treat as a second opinion.</summary>
internal static class LabAudio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr props);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingId, int streamFlags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingId, int streamFlags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duck);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duck);
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
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr value, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComVisible(true), Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated([MarshalAs(UnmanagedType.IUnknown)] object newSession);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    sealed class Notif : IAudioSessionNotification
    {
        public int OnSessionCreated(object newSession)
        {
            try
            {
                var c = (IAudioSessionControl2)newSession;
                if (c.GetProcessId(out uint pid) == 0 && pid != 0) Created[(int)pid] = DateTime.UtcNow;
            }
            catch { }
            return 0;
        }
    }

    // ------------------------------------------------------------------ state
    /// <summary>pid -> time a NEW audio session was announced by the notification callback.</summary>
    public static readonly ConcurrentDictionary<int, DateTime> Created = new();
    public static bool EnableNotifications = true;
    public static int NotifyRegistered;
    public static string NotifyError = "";
    static readonly Notif _notif = new();
    static readonly HashSet<string> _registered = new();
    static readonly List<object> _keepAlive = new();     // managers that hold our notification: must stay alive

    public sealed class Result
    {
        public HashSet<int> Active = new();    // a stream is flowing right now (state = active)
        public HashSet<int> Capture = new();   // ... and it is on a capture (microphone) device
        public HashSet<int> Any = new();       // has ANY session (active, inactive, expired) on any active device
        public int Endpoints;
    }

    public static Result Poll()
    {
        var r = new Result();
        IMMDeviceEnumerator? en;
        try { en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }
        catch { return r; }
        try
        {
            foreach (int flow in new[] { 0, 1 })          // 0 = playback (render), 1 = microphone (capture)
            {
                if (en.EnumAudioEndpoints(flow, 1 /* DEVICE_STATE_ACTIVE */, out var coll) != 0 || coll == null) continue;
                try
                {
                    if (coll.GetCount(out uint n) != 0) continue;
                    for (uint i = 0; i < n; i++)
                    {
                        if (coll.Item(i, out var dev) != 0 || dev == null) continue;
                        try { r.Endpoints++; Scan(dev, flow, r); }
                        catch { }
                        finally { Marshal.ReleaseComObject(dev); }
                    }
                }
                finally { Marshal.ReleaseComObject(coll); }
            }
        }
        catch { }
        finally { try { Marshal.ReleaseComObject(en); } catch { } }
        return r;
    }

    static void Scan(IMMDevice dev, int flow, Result res)
    {
        var iid = typeof(IAudioSessionManager2).GUID;
        if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) != 0) return;
        if (o is not IAudioSessionManager2 mgr) return;
        bool keep = false;
        try
        {
            dev.GetId(out string id);
            if (EnableNotifications && id != null && !_registered.Contains(id))
            {
                _registered.Add(id);
                try
                {
                    int hr = mgr.RegisterSessionNotification(_notif);
                    if (hr == 0) { NotifyRegistered++; _keepAlive.Add(mgr); keep = true; }
                    else NotifyError = "RegisterSessionNotification hr=0x" + hr.ToString("X8");
                }
                catch (Exception ex) { NotifyError = ex.GetType().Name + ": " + ex.Message; }
            }

            // The notification only fires for sessions created AFTER the enumerator was requested once: do it now.
            if (mgr.GetSessionEnumerator(out var list) != 0 || list == null) return;
            try
            {
                if (list.GetCount(out int count) != 0) return;
                for (int i = 0; i < count; i++)
                {
                    if (list.GetSession(i, out var s) != 0 || s == null) continue;
                    try
                    {
                        if (s.GetProcessId(out uint pid) != 0 || pid == 0) continue;
                        res.Any.Add((int)pid);
                        if (s.GetState(out int state) == 0 && state == 1)
                        {
                            res.Active.Add((int)pid);
                            if (flow == 1) res.Capture.Add((int)pid);
                        }
                    }
                    finally { Marshal.ReleaseComObject(s); }
                }
            }
            finally { Marshal.ReleaseComObject(list); }
        }
        finally { if (!keep) Marshal.ReleaseComObject(mgr); }
    }

    // ------------------------------------------------------------------ Windows privacy consent store
    const string ConsentRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    /// <summary>Exe names (no extension) of desktop apps that use the capability RIGHT NOW
    /// (LastUsedTimeStart set, LastUsedTimeStop = 0). capability: microphone, webcam, graphicsCaptureProgrammatic,
    /// graphicsCaptureWithoutBorder. Store/UWP apps are returned as "pkg:&lt;name&gt;" (informational only).</summary>
    public static HashSet<string> ConsentInUse(string capability)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentRoot + capability);
            if (root == null) return set;
            foreach (string sub in root.GetSubKeyNames())
            {
                if (sub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var np = root.OpenSubKey(sub);
                    if (np == null) continue;
                    foreach (string app in np.GetSubKeyNames())
                    {
                        using var k = np.OpenSubKey(app);
                        if (k != null && InUse(k))
                        {
                            string file = app[(app.LastIndexOf('#') + 1)..];
                            set.Add(Path.GetFileNameWithoutExtension(file));
                        }
                    }
                }
                else
                {
                    using var k = root.OpenSubKey(sub);
                    if (k != null && InUse(k)) set.Add("pkg:" + sub);
                }
            }
        }
        catch { }
        return set;
    }

    static bool InUse(RegistryKey k)
    {
        try
        {
            long start = k.GetValue("LastUsedTimeStart") is long a ? a : 0;
            long stop = k.GetValue("LastUsedTimeStop") is long b ? b : 0;
            return start > 0 && stop == 0;
        }
        catch { return false; }
    }
}
