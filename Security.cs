using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace GameRamCap;

/// <summary>Everything GameRamCap does as administrator must only trust places that normal programs cannot change.
/// This class answers "can a non-admin program modify this?" and owns the protected data folder.</summary>
internal static class SecureStore
{
    static readonly SecurityIdentifier SidSystem = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier SidAdmins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier SidCreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    static readonly SecurityIdentifier SidTrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    static bool IsTrustedSid(SecurityIdentifier sid) =>
        sid.Equals(SidSystem) || sid.Equals(SidAdmins) || sid.Equals(SidTrustedInstaller);

    // FileSystemRights bits: WriteData 0x2, AppendData 0x4, WriteEA 0x10, DeleteChild 0x40, WriteAttributes 0x100,
    // Delete 0x10000, ChangePermissions 0x40000, TakeOwnership 0x80000, GenericAll 0x10000000, GenericWrite 0x40000000.
    const int ModifyFileMask = 0x2 | 0x4 | 0x10 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;
    // For a folder on the way to a file: only rights that let someone delete / replace the entries (creating NEW entries is harmless).
    const int ReplaceEntryMask = 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x10000000;

    /// <summary>True when a rule gives one of the mask's rights to somebody who is not SYSTEM / Administrators / TrustedInstaller.</summary>
    static bool GrantsToUntrusted(FileSystemSecurity sec, int mask)
    {
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            if ((r.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;   // only for children, not this object
            if (((int)r.FileSystemRights & mask) == 0) continue;
            var sid = (SecurityIdentifier)r.IdentityReference;
            if (IsTrustedSid(sid) || sid.Equals(SidCreatorOwner)) continue;
            return true;
        }
        return false;
    }

    static bool OwnerTrusted(FileSystemSecurity sec) =>
        sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier o && IsTrustedSid(o);

    /// <summary>Is this program file in a place that only administrators can change (file, owner, every folder above it, no links)?
    /// Program Files and Windows pass; Downloads, Desktop, AppData, Temp, a user folder do not.</summary>
    public static bool IsLocationSecure(string? exe, out string reason)
    {
        reason = "";
        try
        {
            if (string.IsNullOrEmpty(exe) || !Path.IsPathRooted(exe)) { reason = "unknown location"; return false; }
            string full = Path.GetFullPath(exe);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) { reason = "network location"; return false; }
            var fi = new FileInfo(full);
            if (!fi.Exists) { reason = "file not found"; return false; }
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) { reason = "the file is a link"; return false; }
            var fsec = fi.GetAccessControl();
            if (!OwnerTrusted(fsec)) { reason = "the file is owned by a normal user"; return false; }
            if (GrantsToUntrusted(fsec, ModifyFileMask)) { reason = "normal users can modify the file"; return false; }
            for (var dir = fi.Directory; dir != null; dir = dir.Parent)
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) { reason = dir.FullName + " is a link"; return false; }
                var dsec = dir.GetAccessControl();
                if (!OwnerTrusted(dsec)) { reason = dir.FullName + " is owned by a normal user"; return false; }
                if (GrantsToUntrusted(dsec, ReplaceEntryMask)) { reason = "normal users can change " + dir.FullName; return false; }
            }
            return true;
        }
        catch (Exception ex) { reason = ex.Message; return false; }   // cannot prove it is safe = not safe
    }

    // ---------------------------------------------------------------- protected data folder

    static string? _dir;
    static readonly object Gate = new();

    /// <summary>%ProgramData%\GameRamCap, writable only by SYSTEM and Administrators. Throws when it cannot be made safe
    /// (callers already treat a failing folder as "no saved data"). Never logs from here: Logger itself needs this folder.</summary>
    public static string Dir
    {
        get
        {
            lock (Gate)
            {
                if (_dir != null) return _dir;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameRamCap");
                Ensure(dir);
                MigrateLegacyConfig(dir);
                _dir = dir;
                return dir;
            }
        }
    }

    static bool IsTrustedDir(string dir)
    {
        var di = new DirectoryInfo(dir);
        if (!di.Exists || (di.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        var sec = di.GetAccessControl();
        return OwnerTrusted(sec) && !GrantsToUntrusted(sec, ModifyFileMask);
    }

    static void Ensure(string dir)
    {
        if (Directory.Exists(dir) || File.Exists(dir))
        {
            if (Directory.Exists(dir) && IsTrustedDir(dir)) return;
            // Someone else created this folder first, or loosened it: it cannot be trusted. Move it aside (nothing in it is
            // ever read again, but nothing is lost either) and create a clean protected one. A link is moved itself, never followed.
            string aside = dir + ".untrusted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { if (File.Exists(dir)) File.Move(dir, aside); else Directory.Move(dir, aside); } catch { }
            // Could not move it: remove it (a link is removed itself, never followed).
            try { if (File.Exists(dir)) File.Delete(dir); else if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            if (Directory.Exists(dir) || File.Exists(dir)) throw new UnauthorizedAccessException("Data folder is not trustworthy: " + dir);
        }
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);   // no inherited rules
        var flags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(SidSystem, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(SidAdmins, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).Create(sec);   // the ACL is applied at creation: there is no unprotected moment
        if (!IsTrustedDir(dir)) throw new UnauthorizedAccessException("Could not protect the data folder: " + dir);
    }

    /// <summary>Old versions kept config.json in %APPDATA% (any program could edit it). The first time only, the settings
    /// are carried over; they pass the same validation as any other config. Other old files (suspended.json, ...) are NOT imported.</summary>
    static void MigrateLegacyConfig(string dir)
    {
        try
        {
            string target = Path.Combine(dir, "config.json");
            if (File.Exists(target)) return;
            string legacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameRamCap");
            string legacy = Path.Combine(legacyDir, "config.json");
            if (!File.Exists(legacy)) return;
            if ((File.GetAttributes(legacyDir) & FileAttributes.ReparsePoint) != 0) return;
            if ((File.GetAttributes(legacy) & FileAttributes.ReparsePoint) != 0) return;
            if (new FileInfo(legacy).Length > 1024 * 1024) return;
            File.Copy(legacy, target, false);
        }
        catch { }
    }

    static void RefuseLink(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to write through a link: " + path);
    }

    /// <summary>Writes through a temp file and replaces the target, so a half-written file is never seen.</summary>
    public static void WriteAllText(string path, string text)
    {
        RefuseLink(path);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }
}

/// <summary>One running copy only, without a name that another program could squat: an exclusive lock on a file inside the
/// protected data folder. Programs that are not administrators cannot open that file, so they cannot hold or fake the lock.</summary>
internal static class SingleInstance
{
    static FileStream? _lock;   // kept for the life of the process

    static string LockPath => Path.Combine(ConfigStore.Dir, "instance.lock");

    static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    public static bool TryAcquire()
    {
        // A few tries: the watchdog of a previous run opens the file for a moment to look for a live instance.
        for (int i = 0; i < 6; i++)
        {
            try
            {
                _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException ex) when (IsSharingViolation(ex)) { if (i < 5) Thread.Sleep(200); }   // maybe another instance
            catch { return true; }   // folder unavailable: run anyway rather than refuse to start
        }
        return false;   // another instance holds it
    }

    /// <summary>For the watchdog: is a main instance alive right now?</summary>
    public static bool IsHeldByAnother()
    {
        try
        {
            using var f = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException ex) when (IsSharingViolation(ex)) { return true; }
        catch { return false; }
    }
}

/// <summary>Named kernel objects that only SYSTEM and Administrators may open.</summary>
internal static class SecureKernel
{
    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, IntPtr size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES sa, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    const string AdminsOnly = "D:P(A;;GA;;;SY)(A;;GA;;;BA)";
    const int ErrorAlreadyExists = 183;

    /// <summary>Auto-reset event with an Administrators-only ACL. Returns null if it could not be created, or if the name was
    /// already taken (then another program made it first, so it is not ours and not trusted).</summary>
    public static AutoResetEvent? CreateAdminEvent(string name)
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(AdminsOnly, 1, out sd, IntPtr.Zero)) return null;
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd, bInheritHandle = 0 };
            IntPtr h = CreateEventW(ref sa, false, false, name);
            int err = Marshal.GetLastWin32Error();
            if (h == IntPtr.Zero) return null;
            var ev = new AutoResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(h, true) };
            if (err == ErrorAlreadyExists) { ev.Dispose(); return null; }
            return ev;
        }
        catch { return null; }
        finally { if (sd != IntPtr.Zero) LocalFree(sd); }
    }
}

/// <summary>Starts things with the rights of the normal desktop user (what a double-click in Explorer does), never with
/// GameRamCap's administrator rights. Otherwise "Start task" would be a way around the UAC prompt.</summary>
internal static class UserLaunch
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? app, string? cmdLine, uint flags,
        IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    const uint PROCESS_QUERY_INFORMATION = 0x0400, TOKEN_DUPLICATE = 0x0002, TOKEN_ALL_ACCESS = 0xF01FF, CREATE_NO_WINDOW = 0x08000000;
    const int SecurityImpersonation = 2, TokenPrimary = 1, STARTF_USESHOWWINDOW = 1;

    /// <summary>Opens a file, folder, web address or program name like the Windows Run box, without administrator rights.
    /// Throws with a readable message when it cannot.</summary>
    public static void Start(string typed)
    {
        string text = typed.Trim();
        string bare = text.Trim('"');
        if (bare.Length == 0) return;

        bool existing = File.Exists(bare) || Directory.Exists(bare);
        bool web = Uri.TryCreate(bare, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        if (existing || web)
        {
            // The running (normal) Explorer shell opens it: a program that needs admin rights shows its own UAC prompt.
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + bare + "\"") { UseShellExecute = false });
            return;
        }
        // A name or a command line ("notepad", "cmd /k dir", "services.msc"): through cmd "start" with the shell's own token.
        StartWithShellToken("cmd.exe /d /c start \"\" " + text);
    }

    static void StartWithShellToken(string commandLine)
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) throw new InvalidOperationException("The Windows shell (Explorer) is not running.");
        GetWindowThreadProcessId(shell, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr hTok = IntPtr.Zero, hPrimary = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_DUPLICATE, out hTok)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!DuplicateTokenEx(hTok, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hPrimary))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = STARTF_USESHOWWINDOW, wShowWindow = 0 };   // hide the cmd window itself
            string cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!CreateProcessWithTokenW(hPrimary, 0, null, commandLine, CREATE_NO_WINDOW, IntPtr.Zero, cwd, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
        }
        finally
        {
            if (hPrimary != IntPtr.Zero) CloseHandle(hPrimary);
            if (hTok != IntPtr.Zero) CloseHandle(hTok);
            CloseHandle(hProc);
        }
    }
}

/// <summary>Protection by name is not enough for the core Windows names: any program can call itself "lsass".
/// A process that carries one of these names but does not run from the Windows folder is not trusted for it.</summary>
internal static class ProcessIdentity
{
    static readonly HashSet<string> WindowsOwn = new(StringComparer.OrdinalIgnoreCase)
        { "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm", "svchost", "dwm", "fontdrvhost" };

    static readonly string[] UserWritableUnderWindows =
        { "Temp", "Tasks", "Tracing", "debug", "Registration\\CRMLog", "System32\\spool\\drivers\\color", "System32\\Com\\dmp", "System32\\FxsTmp", "System32\\Tasks\\Microsoft\\Windows\\PLA" };

    static readonly Dictionary<int, (DateTime at, bool fake)> Cache = new();
    static readonly object Lock = new();

    public static bool IsImpostor(string name, int pid)
    {
        if (pid <= 4 || !WindowsOwn.Contains(name)) return false;
        lock (Lock)
        {
            if (Cache.TryGetValue(pid, out var c) && (DateTime.UtcNow - c.at).TotalSeconds < 60) return c.fake;
            if (Cache.Count > 2000) Cache.Clear();
        }
        bool fake = false;
        try
        {
            string? path = Native.GetImagePath(pid);
            if (!string.IsNullOrEmpty(path))   // unreadable path: stay on the safe side and keep it protected
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
                fake = !path.StartsWith(win, StringComparison.OrdinalIgnoreCase);
                if (!fake)   // a few folders under Windows can be written by normal users: a copy placed there is not Windows
                    foreach (var sub in UserWritableUnderWindows)
                        if (path.StartsWith(win + sub + "\\", StringComparison.OrdinalIgnoreCase)) { fake = true; break; }
            }
        }
        catch { }
        lock (Lock) Cache[pid] = (DateTime.UtcNow, fake);
        return fake;
    }
}
