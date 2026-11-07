namespace GameRamCap;

/// <summary>Turns on SeDebugPrivilege for this process (needs administrator rights, which GameRamCap already runs with).
/// Without it Windows refuses to open many system processes (dwm, services...), so their RAM shows "?" and
/// End task / priority / limits fail on them. With it, most of them can be read and changed.</summary>
internal static class DebugPrivilege
{
    public static void Enable()
    {
        bool ok = Privilege.Enable("SeDebugPrivilege");
        Logger.Write(ok ? "SeDebugPrivilege enabled: system processes can be read." : "SeDebugPrivilege not available (run GameRamCap as administrator).");
    }
}
