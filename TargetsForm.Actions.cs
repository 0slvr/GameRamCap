using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameRamCap;

/// <summary>Apps tab, part 4: the right-click actions (end, restart, protect, priority, cores, freeze, VirusTotal, start task).</summary>
internal sealed partial class TargetsForm
{
    // ---------------- Task Manager style actions ----------------

    List<string> SelectedNames() =>
        _running.SelectedItems.Cast<ListViewItem>().Where(i => i.Tag is string).Select(i => (string)i.Tag!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>End task: ends every process of the selected apps but NOT their child processes (like Task Manager's End task).
    /// With tree = true ("End process tree") the child processes are ended too.
    /// System, security and recording processes are refused: ending them can crash Windows or the recording.</summary>
    void EndSelected(bool tree = false) => RunSafe(() => EndSelectedCore(tree), "End task");

    /// <summary>Runs an async action from a menu or button. Any exception is logged and shown instead of vanishing.</summary>
    async void RunSafe(Func<Task> work, string what)
    {
        try { await work(); }
        catch (Exception ex)
        {
            Logger.Write($"{what} failed: {ex}");
            if (IsDisposed) return;
            try { MessageBox.Show(this, $"{what} failed:\n\n{ex.Message}", "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
        }
    }

    async Task EndSelectedCore(bool tree)
    {
        var names = SelectedNames();
        if (names.Count == 0) return;
        var blocked = names.Where(n => Engine.Protected.Contains(n)).ToList();
        names = names.Where(n => !Engine.Protected.Contains(n)).ToList();
        if (blocked.Count > 0)
            MessageBox.Show(this, "These are protected (system, security or recording) and cannot be ended from here:\n\n" + string.Join(", ", blocked),
                "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (names.Count == 0) return;

        string what = tree ? "End the process tree of " : "End ";
        string extra = tree ? "\n\nEvery program started by them is ended too." : "";
        var ask = MessageBox.Show(this, what + string.Join(", ", names) + "?\n\nUnsaved data in these apps will be lost." + extra,
            tree ? "End process tree" : "End task", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ask != DialogResult.Yes) return;

        int failed = 0;
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(tree); }
                catch { failed++; }
                finally { p.Dispose(); }
            }
        }
        await Task.Delay(700);
        if (IsDisposed) return;
        RefreshRunning();
        if (failed > 0)
            MessageBox.Show(this, $"{failed} process(es) could not be ended (access denied, or already closing).",
                "End task", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Restart: ends the selected apps (not their children) and starts each one again from its file, with the normal
    /// user's rights (a program that needs admin shows its own UAC prompt). Command-line arguments are not kept.
    /// Explorer is restarted by Windows itself, so it is only ended.</summary>
    void RestartSelected() => RunSafe(RestartSelectedCore, "Restart");

    async Task RestartSelectedCore()
    {
        var names = SelectedNames();
        if (names.Count == 0) return;
        var blocked = names.Where(n => Engine.Protected.Contains(n)).ToList();
        names = names.Where(n => !Engine.Protected.Contains(n)).ToList();
        if (blocked.Count > 0)
            MessageBox.Show(this, "These are protected and cannot be restarted from here:\n\n" + string.Join(", ", blocked),
                "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (names.Count == 0) return;
        if (MessageBox.Show(this, "Restart " + string.Join(", ", names) + "?\n\nUnsaved data in these apps will be lost. They start again without their command-line arguments and without administrator rights.",
                "Restart", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        var failed = new List<string>();
        var toStart = new List<string>();
        foreach (var name in names)
        {
            var paths = new List<string>();   // every distinct program file the app runs from (not just the first process)
            bool killed = false, packaged = false, unknown = false;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string? ip = Native.GetImagePath(p.Id);
                    if (ip != null && ip.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0) { packaged = true; continue; }   // Store apps cannot be started from their exe
                    // only end a process whose program file we know and can start again: never end something we cannot bring back
                    if (string.IsNullOrEmpty(ip) || !File.Exists(ip)) { unknown = true; continue; }
                    if (!paths.Contains(ip, StringComparer.OrdinalIgnoreCase)) paths.Add(ip);
                    p.Kill(false);
                    killed = true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (!killed)
            {
                failed.Add(name + (packaged ? " (Store app: close it and open it again yourself)"
                                 : unknown ? " (its file location cannot be read, so it was not ended)" : ""));
                continue;
            }
            if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase)) continue;   // Windows brings the shell back by itself
            toStart.AddRange(paths);
        }
        await Task.Delay(1000);
        if (IsDisposed) return;
        foreach (var path in toStart)
        {
            try { UserLaunch.Start(path); Logger.Write("Restart (as normal user): " + path); }
            catch (Exception ex) { failed.Add(Path.GetFileName(path) + " (" + ex.Message + ")"); }
        }
        await Task.Delay(700);
        if (IsDisposed) return;
        RefreshRunning();
        if (failed.Count > 0)
            MessageBox.Show(this, "Could not restart: " + string.Join(", ", failed), "Restart", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------------- Protect / unprotect ----------------

    /// <summary>Protected apps are never limited, frozen or ended from here. Protect adds the selected apps to the user's own list
    /// (Config.Exceptions); Unprotect removes them from it, and turns off the built-in protection of the ones that come with
    /// GameRamCap.</summary>
    void SetProtection(bool protect)
    {
        var names = SelectedNames();
        if (names.Count == 0) return;
        try
        {
            var cur = ConfigStore.Load();   // the file as it is now: change only these two lists, keep what was added elsewhere
            foreach (var n in names)
            {
                if (protect)
                {
                    cur.UnprotectedBuiltIn.RemoveAll(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
                    if (!cur.Exceptions.Contains(n, StringComparer.OrdinalIgnoreCase) && !Engine.BaseProtected.Contains(n)) cur.Exceptions.Add(n);
                }
                else
                {
                    cur.Exceptions.RemoveAll(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
                    if (Engine.BaseProtected.Contains(n) && !cur.UnprotectedBuiltIn.Contains(n, StringComparer.OrdinalIgnoreCase)) cur.UnprotectedBuiltIn.Add(n);
                }
            }
            ConfigStore.Save(cur);
            Engine.RebuildProtected(cur);   // effective at once; the tray app reloads the file within a second
        }
        catch (Exception ex) { Logger.Write("Could not save protection: " + ex.Message); }
        Render();
    }

    // ---------------- CPU priority and cores (this run, or remembered as a rule) ----------------

    /// <summary>Selected apps minus the protected ones (system, security, recording). Tells the user about the refused ones.</summary>
    List<string> AllowedSelected()
    {
        var names = SelectedNames();
        var blocked = names.Where(n => Engine.Protected.Contains(n)).ToList();
        if (blocked.Count > 0)
            MessageBox.Show(this, "These are protected (system, security or recording) and are left alone:\n\n" + string.Join(", ", blocked),
                "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return names.Where(n => !Engine.Protected.Contains(n)).ToList();
    }

    /// <summary>Re-reads the saved rules before they are edited, so a rule changed elsewhere is not overwritten by an old list.</summary>
    void ReloadRules()
    {
        try { var disk = ConfigStore.Load().Rules ?? new List<ProcessRule>(); _rules.Clear(); _rules.AddRange(disk); } catch { }
    }

    void SaveRules()
    {
        // Start from the file as it is NOW, so everything changed meanwhile (tray, other tabs) is kept.
        var cur = ConfigStore.Load();
        cur.Rules = _rules;
        ConfigStore.Save(cur);
    }

    ProcessRule GetOrAddRule(string exe)
    {
        var r = FindRule(exe);
        if (r == null) { r = new ProcessRule { Exe = exe }; _rules.Add(r); }
        return r;
    }

    void ReportApplied(int ok, int failed)
    {
        if (failed > 0)
            MessageBox.Show(this, $"{failed} process(es) could not be changed (access denied, or already closing)." +
                (ok > 0 ? $"\n{ok} worked." : ""), "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void SetPriority(string priority, bool remember)
    {
        var names = AllowedSelected();
        if (names.Count == 0) return;
        if (remember) ReloadRules();
        int ok = 0, failed = 0;
        foreach (var n in names)
        {
            var (o, f) = ProcessRules.SetNow(n, priority, 0);
            ok += o; failed += f;
            if (remember) GetOrAddRule(n).Priority = priority;
        }
        if (remember) SaveRules();
        ReportApplied(ok, failed);
        RefreshRunning();
    }

    /// <summary>Removes the saved priority and/or cores of the selected apps (the running processes keep what they have now).</summary>
    void ForgetRule(bool priority, bool cores)
    {
        bool changed = false;
        ReloadRules();
        foreach (var n in SelectedNames())
        {
            var r = FindRule(n);
            if (r == null) continue;
            if (priority && r.Priority.Length > 0) { r.Priority = ""; changed = true; }
            if (cores && r.AffinityMask != 0) { r.AffinityMask = 0; changed = true; }
            if (r.Priority.Length == 0 && r.AffinityMask == 0) _rules.Remove(r);
        }
        if (changed) SaveRules();
        Render();
    }

    // ---------------- Freeze (suspend), manual only ----------------

    void FreezeSelected()
    {
        var names = AllowedSelected();
        if (names.Count == 0) return;
        int ok = 0, failed = 0;
        foreach (var n in names) { var (o, f) = Suspender.Suspend(n); ok += o; failed += f; }
        ReportApplied(ok, failed);
        RefreshRunning();
    }

    void ResumeSelected()
    {
        foreach (var n in SelectedNames()) Suspender.Resume(n);
        RefreshRunning();
    }

    /// <summary>Choose which logical cores the selected apps may use.</summary>
    void PickCores()
    {
        var names = AllowedSelected();
        if (names.Count == 0) return;
        long all = ProcessRules.AllCoresMask();

        // start from the cores the first process uses now
        long start = all;
        foreach (var p in Process.GetProcessesByName(names[0]))
        {
            try { start = (long)p.ProcessorAffinity & all; if (start == 0) start = all; break; }
            catch { }
            finally { p.Dispose(); }
        }

        using var dlg = new CoresDialog(names, start);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        long mask = dlg.Mask;
        if (mask == 0) return;

        int okCount = 0, failed = 0;
        if (dlg.Remember) ReloadRules();
        foreach (var n in names)
        {
            var (o, fl) = ProcessRules.SetNow(n, "", mask);
            okCount += o; failed += fl;
            if (dlg.Remember)
            {
                var r = GetOrAddRule(n);
                r.AffinityMask = mask == all ? 0 : mask;   // all cores = no restriction = no rule
                if (r.Priority.Length == 0 && r.AffinityMask == 0) _rules.Remove(r);
            }
        }
        if (dlg.Remember) SaveRules();
        ReportApplied(okCount, failed);
        RefreshRunning();
    }

    bool OpenLocation()
    {
        var names = SelectedNames();
        if (names.Count == 0) return false;
        var failed = new List<string>();
        foreach (var name in names)
        {
            bool done = false;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (!done && !string.IsNullOrEmpty(path)) { Process.Start("explorer.exe", $"/select,\"{path}\""); done = true; }
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (!done) failed.Add(name);
        }
        if (failed.Count > 0)
            MessageBox.Show(this, "Windows does not let us read the location of: " + string.Join(", ", failed), "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return failed.Count < names.Count;   // true when at least one location was opened
    }

    /// <summary>Hashes the exe of the selected app (SHA-256) and opens its VirusTotal page in the browser.
    /// Only the hash goes into the address: the file is not uploaded and no API key is used.</summary>
    void CheckVirusTotal() => RunSafe(CheckVirusTotalCore, "VirusTotal check");

    async Task CheckVirusTotalCore()
    {
        var names = SelectedNames();
        if (names.Count == 0) return;
        if (names.Count > 3 && MessageBox.Show(this, $"This opens {names.Count} VirusTotal pages in your browser. Continue?", "VirusTotal",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var failed = new List<string>();
        UseWaitCursor = true;
        try
        {
            foreach (var name in names)
            {
                string? path = null;
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { path ??= Native.GetImagePath(p.Id); }
                    finally { p.Dispose(); }
                }
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { failed.Add(name); continue; }
                string file = path, hash;
                try { hash = await Task.Run(() => VirusTotal.Sha256(file)); }
                catch (Exception ex) { failed.Add(name + " (" + ex.Message + ")"); continue; }
                try { Clipboard.SetText(hash); } catch { }   // the last hash is also on the clipboard
                Logger.Write($"VirusTotal: {name} SHA-256 {hash}");
                if (!VirusTotal.Open(hash)) failed.Add(name + " (could not open the browser; the hash is on the clipboard)");
            }
        }
        finally { UseWaitCursor = false; }
        if (failed.Count > 0)
            MessageBox.Show(this, "Could not read the file of: " + string.Join(", ", failed), "VirusTotal", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Start new task: type a program, folder, document or address, or browse for it.
    /// GameRamCap runs as administrator, but what is started here runs with the normal user's rights (UserLaunch),
    /// so this button can never be used to get administrator rights without the UAC prompt.</summary>
    void StartTask(bool minimizeAfter = false) => RunSafe(() => StartTaskCore(minimizeAfter), "Start task");

    async Task StartTaskCore(bool minimizeAfter)
    {
        using var dlg = new StartTaskDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string target = dlg.Command;
        if (target.Trim('"').Length == 0) return;
        try { UserLaunch.Start(target); Logger.Write("Start task (as normal user): " + target); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not start \"" + target + "\":\n\n" + ex.Message, "Start task", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (minimizeAfter) MinimizeIfWanted();   // only after something was really started
        await Task.Delay(1000);
        if (IsDisposed) return;
        RefreshRunning();
    }
}
