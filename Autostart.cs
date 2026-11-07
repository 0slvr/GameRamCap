using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Text;

namespace GameRamCap;

/// <summary>"Start with Windows" through a Scheduled Task with highest privileges
/// (the app needs admin rights, so the normal Startup folder cannot start it silently).
/// The task is registered from an XML file so it has no 72-hour limit and is not tied to the power cable, and it only
/// ever points at a program that normal programs cannot replace.</summary>
internal static class Autostart
{
    const string TaskName = "GameRamCap";
    public const string MinimizedArg = "--minimized";

    static int Run(string args) => Run(args, out _);

    static int Run(string args, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return -1;
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return -1; }
            output = outTask.Wait(2000) ? outTask.Result : "";
            return p.ExitCode;
        }
        catch (Exception ex) { Logger.Write("schtasks failed: " + ex.Message); return -1; }
    }

    static volatile int _cache = -1;   // -1 unknown, 0 off, 1 on

    /// <summary>Last known state without running schtasks (instant). Unknown counts as off until <see cref="RefreshAsync"/> finishes.</summary>
    public static bool Cached => _cache == 1;

    /// <summary>Asks Windows in the background (schtasks takes a moment, never call IsEnabled on the UI thread); then calls done on that thread.</summary>
    public static void RefreshAsync(Action<bool>? done = null) =>
        Task.Run(() => { bool on = IsEnabled(); done?.Invoke(on); });

    public static bool IsEnabled()
    {
        bool on = Run($"/Query /TN \"{TaskName}\"") == 0;
        _cache = on ? 1 : 0;
        return on;
    }

    public static bool Set(bool on)
    {
        bool ok = SetCore(on);
        if (ok) _cache = on ? 1 : 0;
        return ok;
    }

    /// <summary>True when the last "turn on" was refused for security reasons (the task was removed instead).</summary>
    public static volatile bool Blocked;

    /// <summary>The task runs with the highest rights, so it may only point at a program that normal programs cannot replace.
    /// If this copy sits in a user-writable place (Downloads, Desktop, AppData...), a protected copy is kept in
    /// Program Files and the task points there. Returns null when no safe program is available.</summary>
    static string? SecureExePath()
    {
        string? exe = Environment.ProcessPath;
        if (SecureStore.IsLocationSecure(exe, out string why)) return exe;
        Logger.Write($"Autostart: {exe} can be modified by normal programs ({why}). Using a protected copy in Program Files.");
        return InstallProtectedCopy(exe);
    }

    static string? InstallProtectedCopy(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        string srcDir = Path.GetDirectoryName(exe)!;
        string dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GameRamCap");
        string target = Path.Combine(dst, Path.GetFileName(exe));
        try { CopyProgramFiles(srcDir, dst, exe); }
        catch (Exception ex) { Logger.Write("Autostart: could not update the protected copy: " + ex.Message); }
        // Program Files only lets administrators write, so what is there cannot be swapped by a normal program.
        // If the update failed (file in use) an older protected copy is still fine.
        return SecureStore.IsLocationSecure(target, out _) ? target : null;
    }

    static readonly string[] OwnSuffixes = { ".exe", ".dll", ".deps.json", ".runtimeconfig.json", ".pdb" };

    /// <summary>The file names that belong to the program: GameRamCap.exe / .dll / .deps.json / .runtimeconfig.json plus, for a
    /// self-contained build, the libraries its own .deps.json lists. Nothing else in the folder is ever copied
    /// (Downloads and Desktop are full of unrelated files, and a stray DLL must not become "trusted" by being copied).</summary>
    static HashSet<string> ProgramFileNames(string srcDir, string exe)
    {
        string baseName = Path.GetFileNameWithoutExtension(exe);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in OwnSuffixes) names.Add(baseName + s);
        try
        {
            string deps = Path.Combine(srcDir, baseName + ".deps.json");
            if (File.Exists(deps))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(deps));
                if (doc.RootElement.TryGetProperty("targets", out var targets))
                    foreach (var tgt in targets.EnumerateObject())
                        foreach (var lib in tgt.Value.EnumerateObject())
                            foreach (var section in new[] { "runtime", "native" })
                                if (lib.Value.ValueKind == JsonValueKind.Object && lib.Value.TryGetProperty(section, out var files))
                                    foreach (var f in files.EnumerateObject())
                                    {
                                        string n = Path.GetFileName(f.Name);
                                        string ext = Path.GetExtension(n);
                                        if (n.Length > 0 && (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)))
                                            names.Add(n);
                                    }
            }
        }
        catch { /* no deps file: the plain list above is enough for a framework-dependent build */ }
        return names;
    }

    static void CopyProgramFiles(string srcDir, string dst, string exe)
    {
        Directory.CreateDirectory(dst);
        foreach (string n in ProgramFileNames(srcDir, exe))
        {
            string f = Path.Combine(srcDir, n);
            if (!File.Exists(f)) continue;
            if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) continue;   // never follow links
            string d = Path.Combine(dst, n);
            var sf = new FileInfo(f);
            if (File.Exists(d))
            {
                var df = new FileInfo(d);
                if (df.Length == sf.Length && df.LastWriteTimeUtc == sf.LastWriteTimeUtc) continue;   // already the same file: no copy
            }
            File.Copy(f, d, true);
        }
    }

    // ---------------------------------------------------------------- the task itself

    static string? BuildXml(string exe)
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrEmpty(sid)) return null;
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>GameRamCap - starts in the tray when you log on</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author""><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{MinimizedArg}</Arguments></Exec>
  </Actions>
</Task>";
    }

    /// <summary>Is the registered task already exactly what we would create? Then it is left alone.</summary>
    static bool TaskIsCurrent(string exe)
    {
        if (Run($"/Query /TN \"{TaskName}\" /XML", out string xml) != 0) return false;
        return xml.Contains(SecurityElement.Escape(exe), StringComparison.OrdinalIgnoreCase)
            && xml.Contains(MinimizedArg)
            && xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>")
            && xml.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
    }

    static bool CreateWithXml(string exe)
    {
        string? xml = BuildXml(exe);
        if (xml == null) return false;
        string file = Path.Combine(ConfigStore.Dir, "autostart-task.xml");   // protected folder: nobody can edit it between write and use
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") == 0;
        }
        catch (Exception ex) { Logger.Write("Autostart: XML task failed: " + ex.Message); return false; }
        finally { try { File.Delete(file); } catch { } }
    }

    static bool SetCore(bool on)
    {
        if (!on) return Run($"/Delete /TN \"{TaskName}\" /F") == 0;

        string? exe = SecureExePath();
        if (exe == null)
        {
            // Fail closed: never keep a highest-rights task that points at a file anybody can replace.
            Logger.Write("Autostart: no protected program available, the startup task was removed.");
            Blocked = true;
            Run($"/Delete /TN \"{TaskName}\" /F");
            return false;
        }
        Blocked = false;
        if (TaskIsCurrent(exe)) return true;
        if (CreateWithXml(exe)) return true;
        Logger.Write("Autostart: could not register the full task, using the basic one (Windows may stop it after 72 hours or on battery).");
        return Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" {MinimizedArg}\" /SC ONLOGON /RL HIGHEST /F") == 0;
    }
}
