using System.Text.Json;

namespace GameRamCap;

/// <summary>The "pause Windows Update while a game runs" feature was removed. This only repairs the leftovers of an older
/// version: if that version crashed while Windows Update was paused, winupdate.json still holds the original service state,
/// and the next start (or the watchdog) puts the wuauserv service back exactly as it was.</summary>
internal static class WindowsUpdatePause
{
    const string Svc = "wuauserv";

    sealed class State
    {
        public int Start { get; set; } = ServiceApi.StartManual;
        public bool Delayed { get; set; }
        public bool WasRunning { get; set; }
    }

    static string FilePath => Path.Combine(ConfigStore.Dir, "winupdate.json");

    static void Apply(State s)
    {
        var cur = ServiceApi.StartType(Svc);
        if (cur == null) return;
        // Put the start type back only if it is still the Disabled we set. If you changed it yourself meanwhile, your choice stays.
        if (cur.Value.start == ServiceApi.StartDisabled)
        {
            ServiceApi.SetStartType(Svc, s.Start, s.Delayed);
            if (s.WasRunning) ServiceApi.Start(Svc);
        }
    }

    /// <summary>After a crash / End Task of an older version: restores what it changed (also used by the watchdog).</summary>
    public static void RecoverStale()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var s = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath));
            if (s != null) Apply(s);
            File.Delete(FilePath);
        }
        catch { }
    }
}
