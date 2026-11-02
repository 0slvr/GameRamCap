using System.Diagnostics;

namespace GameRamCap;

internal sealed partial class Engine
{
    // Same idea as Memory Sentry's policy, written from scratch:
    // busy for 6s while at the ceiling -> +8 MB; idle for 10s with headroom -> -4 MB; 8s cooldown.
    void Adapt(Process p, Target t, Limited st)
    {
        var now = DateTime.UtcNow;
        double dt = (now - st.LastSample).TotalSeconds;
        if (dt < 0.5) return;

        TimeSpan cpu;
        long ws;
        try { p.Refresh(); cpu = p.TotalProcessorTime; ws = p.WorkingSet64; }
        catch { return; }

        double usage = (cpu - st.LastCpu).TotalSeconds / dt; // fraction of one logical core
        st.LastCpu = cpu;
        st.LastSample = now;

        bool atCeiling = ws >= st.Ceiling * 0.95;
        if (usage >= 0.10 && atCeiling) { st.HighSecs += dt; st.LowSecs = 0; }
        else if (usage <= 0.03 && st.Ceiling - ws >= 6 * MB) { st.LowSecs += dt; st.HighSecs = 0; }
        else { st.HighSecs = 0; st.LowSecs = 0; }

        if ((now - st.LastAdjust).TotalSeconds < 8) return;

        long min = t.MinCeilingMB * MB, max = Math.Max(t.MinCeilingMB, t.MaxCeilingMB) * MB;
        long next = st.Ceiling;
        if (st.HighSecs >= 6) next = Math.Min(st.Ceiling + 8 * MB, max);
        else if (st.LowSecs >= 10) next = Math.Max(st.Ceiling - 4 * MB, min);

        if (next != st.Ceiling && Native.SetCeiling(p.Id, next))
        {
            st.Ceiling = next;
            st.LastAdjust = now;
            st.HighSecs = st.LowSecs = 0;
        }
    }
}
