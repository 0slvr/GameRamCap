using System.Diagnostics;
using System.Text.Json;

namespace GameRamCap;

/// <summary>Pauses Windows Update while a game runs: the wuauserv service is stopped and set to Disabled so nothing can
/// restart it mid-game, then put back exactly as it was (start type, and running again if it was running) when the game ends.
/// The original state is written to winupdate.json first, so a crash or End Task of GameRamCap cannot leave Windows Update
/// disabled: the next start (or the watchdog) restores it. Only wuauserv is touched. It is never paused while an update is
/// being installed (TrustedInstaller / TiWorker running).</summary>
internal static class WindowsUpdatePause
{
    const string Svc = "wuauserv";
    static readonly object Lock = new();
    static State? _saved;

    public sealed class State
    {
        public int Start { get; set; } = ServiceApi.StartManual;
        public bool Delayed { get; set; }
        public bool WasRunning { get; set; }
    }

    static string FilePath => Path.Combine(ConfigStore.Dir, "winupdate.json");

    static bool Installing() =>
        Process.GetProcessesByName("TiWorker").Length > 0 || Process.GetProcessesByName("TrustedInstaller").Length > 0;

    /// <summary>Returns true when Windows Update is now paused (or already was). false = left alone (already disabled,
    /// not present, an update is installing, or no permission).</summary>
    public static bool Pause()
    {
        lock (Lock)
        {
            if (_saved != null) return true;
            try { if (Installing()) return false; } catch { }
            var st = ServiceApi.StartType(Svc);
            if (st == null || st.Value.start == ServiceApi.StartDisabled) return false;   // missing, or the user disabled it already
            uint? state = ServiceApi.State(Svc);
            var s = new State { Start = st.Value.start, Delayed = st.Value.delayed, WasRunning = state == ServiceApi.StateRunning || state == 2 };
            try { Directory.CreateDirectory(ConfigStore.Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(s)); } catch { }
            if (ServiceApi.SetStartType(Svc, ServiceApi.StartDisabled, false) != null)
            {
                try { File.Delete(FilePath); } catch { }
                return false;
            }
            ServiceApi.Stop(Svc);   // returns at once; the service finishes stopping in the background
            _saved = s;
            return true;
        }
    }

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

    public static void Restore()
    {
        lock (Lock)
        {
            if (_saved == null) return;
            try { Apply(_saved); } catch { }
            _saved = null;
            try { File.Delete(FilePath); } catch { }
        }
    }

    /// <summary>After a crash / End Task: restores what the previous run changed (also used by the watchdog).</summary>
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
