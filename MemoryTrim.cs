using System.Runtime;

namespace GameRamCap;

/// <summary>GameRamCap's own memory: a full, compacting garbage collection and a trim of its own working set.
/// Runs a moment after the main window is closed and every few minutes while the window is not showing, so the tray
/// icon sits at its real size instead of the high-water mark left by a scan, the prefetch snapshots or the window.</summary>
internal static class MemoryTrim
{
    static readonly object Lock = new();

    public static void Run()
    {
        lock (Lock)
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                Native.Trim(Environment.ProcessId);   // the pages go to the standby list; whatever is needed comes back at once
            }
            catch { }
        }
    }
}
