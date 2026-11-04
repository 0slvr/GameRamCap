namespace GameRamCap;

// Game profile: while a game runs, Windows Update can be paused (Config.PauseWindowsUpdate) and is restored
// when the game ends. Nothing is frozen automatically: Freeze / Resume is manual only (Apps tab, right-click).
internal sealed partial class Engine
{
    bool _profileOn;
    DateTime _profileLastGaming = DateTime.MinValue;
    const double ProfileEndDelaySeconds = 8;   // a short loss of the game (Alt+Tab, detection flicker) does not end the profile

    void TickProfile()
    {
        bool active = Enabled && Config.Enabled && Config.PauseWindowsUpdate;
        bool gaming = active && IsGaming();
        var now = DateTime.UtcNow;
        if (gaming) _profileLastGaming = now;
        bool keep = gaming || (_profileOn && active && (now - _profileLastGaming).TotalSeconds < ProfileEndDelaySeconds);

        if (keep)
        {
            if (_profileOn) return;
            _profileOn = true;
            bool paused = WindowsUpdatePause.Pause();
            Logger.Write(paused ? "Game profile: Windows Update paused." : "Game profile: Windows Update not paused (already disabled, an update is installing, or no permission).");
        }
        else if (_profileOn)
        {
            _profileOn = false;
            WindowsUpdatePause.Restore();
            Logger.Write("Game profile: back to normal.");
        }
    }
}
