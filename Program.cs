using System.Diagnostics;
using Timer = System.Windows.Forms.Timer;

namespace GameRamCap;

static class Program
{
    internal const string ShowEventName = "GameRamCap.ShowWindow";
    /// <summary>True when started by the Windows startup task: stay in the tray, do not open the window.</summary>
    internal static bool StartMinimized;

    [STAThread]
    static void Main(string[] args)
    {
        // Watchdog mode: a tiny hidden copy that waits for the main app to die (End Task cannot be caught
        // by the app itself) and then lifts every hard RAM ceiling that was left on other processes.
        if (args.Length >= 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int parentPid))
        {
            try { using var parent = Process.GetProcessById(parentPid); parent.WaitForExit(); } catch { }
            // If a new main instance already started (restart), it owns the ceilings now: do not lift them.
            // A restart brings the new instance within a few seconds (UAC prompt, slow disk): wait for it before lifting anything.
            for (int i = 0; i < 8; i++)
            {
                Thread.Sleep(1000);
                if (SingleInstance.IsHeldByAnother())
                {
                    Logger.Write("Watchdog: a new instance is running, nothing released.");
                    return;
                }
            }
            try { int n = Engine.ReleaseAllHardCeilings(); Logger.Write($"Watchdog: main app ended, released {n} ceiling(s)."); } catch { }
            try { WindowsUpdatePause.RecoverStale(); } catch { }
            try { PowerPlan.RecoverStale(); } catch { }
            try { int r = Suspender.RecoverStale(); if (r > 0) Logger.Write($"Watchdog: resumed {r} frozen process(es)."); } catch { }
            return;
        }

        StartMinimized = args.Any(a => string.Equals(a, Autostart.MinimizedArg, StringComparison.OrdinalIgnoreCase));
        // One instance only: an exclusive lock inside the protected data folder (a named mutex could be squatted by any program).
        if (!SingleInstance.TryAcquire())
        {
            // Already running: ask the first instance to show its window instead of exiting silently.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            return;
        }

        // Global handlers: log instead of dying silently.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Logger.Write("UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Write("Fatal exception: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Logger.Write("Task exception: " + e.Exception); e.SetObserved(); };

        try { int n = Suspender.RecoverStale(); if (n > 0) Logger.Write($"Resumed {n} process(es) left frozen by the previous run."); } catch { }
        try { WindowsUpdatePause.RecoverStale(); } catch { }
        try { PowerPlan.RecoverStale(); } catch { }
        DebugPrivilege.Enable();
        StartWatchdog();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }

    /// <summary>Starts the watchdog detached (through cmd "start") so that End Task / End process tree
    /// on this app does not kill it too.</summary>
    static void StartWatchdog()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c start \"\" \"{exe}\" --watchdog {Environment.ProcessId}")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Exception ex) { Logger.Write("Could not start watchdog: " + ex.Message); }
    }
}

sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly Timer _timer;
    readonly Engine _engine = new();
    readonly ToolStripMenuItem _enabledItem;
    readonly ToolStripMenuItem _statusItem;
    DateTime _configStamp;
    readonly List<ToolStripMenuItem> _profileItems = new();
    SettingsPanel? _settings;
    HomePanel? _home;
    bool _syncTray;
    volatile bool _busy;
    volatile bool _showRequested;
    readonly System.Collections.Concurrent.ConcurrentQueue<(string, string)> _balloons = new();
    string _lastErr = ""; DateTime _lastErrAt = DateTime.MinValue;

    public TrayContext()
    {
        LoadConfig();
        Logger.Write($"Started. Games=[{string.Join(", ", _engine.Config.Games)}] AutoDetectFullscreen={_engine.Config.AutoDetectFullscreen} Delay={_engine.Config.DelaySeconds}s");
        _engine.ClearStaleCeilings();
        _engine.ClearStaleCeilingsEverything();

        _statusItem = new ToolStripMenuItem("Idle") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        _enabledItem.CheckedChanged += (_, _) => { if (!_syncTray) SetEnabled(_enabledItem.Checked); };

        _settings = new SettingsPanel(() => _engine.Config, SaveCfg, SetProfile,
            OpenConfigFile,
            LoadConfig,
            Logger.Open,
            ResetToDefaults);

        _home = new HomePanel(() => _engine.Config, SaveCfg, () => _engine.Status, () => _engine.LimitedCount,
            SetProfile, SetMode, SetEnabled, tab => ShowMain(tab), _settings.SmartDetails, BoostNow);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        var boostItem = new ToolStripMenuItem("Boost now (free RAM)");
        boostItem.Click += (_, _) => _ = BoostNow();
        menu.Items.Add(boostItem);
        // Mode is the main choice; the intensity levels live inside Smart.
        _intensityMenu = new ToolStripMenuItem("Mode");
        _smartItem = new ToolStripMenuItem("Smart") { Tag = SmartTag };
        _smartItem.Click += (_, _) => SetModeAndLevel("Smart", null);   // clicking Smart itself keeps the current level
        for (int i = 0; i < Presets.Names.Length; i++)   // Custom is left to the window: the tray stays simple
        {
            string key = Presets.Names[i];
            var item = new ToolStripMenuItem(Presets.Labels[i]) { Tag = key };
            item.Click += (_, _) => SetModeAndLevel("Smart", key);
            _profileItems.Add(item);
            _smartItem.DropDownItems.Add(item);
        }
        var hard = _hardItem = new ToolStripMenuItem("Hard") { Tag = HardTag };
        hard.Click += (_, _) => SetModeAndLevel("Hard", null);   // keeps the current scope
        var hardProc = new ToolStripMenuItem("Per process (recommended)") { Tag = HardProcTag };
        hardProc.Click += (_, _) => SetModeAndLevel("Hard", null, "Process");
        var hardApp = new ToolStripMenuItem("Per app (total shared by its processes)") { Tag = HardAppTag };
        hardApp.Click += (_, _) => SetModeAndLevel("Hard", null, "App");
        hard.DropDownItems.Add(hardProc); hard.DropDownItems.Add(hardApp);
        _profileItems.Add(hardProc); _profileItems.Add(hardApp);
        _intensityMenu.DropDownItems.Add(_smartItem);
        _intensityMenu.DropDownItems.Add(hard);
        menu.Items.Add(_intensityMenu);
        menu.Items.Add(new ToolStripSeparator());
        RefreshProfileChecks();
        menu.Items.Add("Settings...", null, (_, _) => ShowTargets());
        var startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        startup.Click += (_, _) =>
        {
            bool want = !startup.Checked;
            Task.Run(() => Autostart.Set(want)).ContinueWith(t =>
            {
                try
                {
                    menu.BeginInvoke(() =>
                    {
                        if (t.IsCompletedSuccessfully && t.Result) startup.Checked = want;
                        else _balloons.Enqueue(("GameRamCap", want && Autostart.Blocked
                            ? "Start with Windows needs a protected copy in Program Files and it could not be made. Run GameRamCap as administrator from a normal folder and try again."
                            : "Could not change the startup task."));
                    });
                }
                catch { }
            });
        };
        // Opening must be instant: show the last known state, check with Windows in the background and fix the tick when it answers.
        menu.Opening += (_, _) =>
        {
            startup.Checked = Autostart.Cached;
            Autostart.RefreshAsync(on => { try { menu.BeginInvoke(() => startup.Checked = on); } catch { } });
        };
        Autostart.RefreshAsync();   // warm the cache at launch
        menu.Items.Add(startup);
        menu.Items.Add("Open log file", null, (_, _) => Logger.Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Tray,
            Text = "GameRamCap",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _tray.DoubleClick += (_, _) => ShowTargets();

        // The engine ticks on a background thread: balloons are queued and shown from the UI timer.
        _engine.Notify += (title, text) => _balloons.Enqueue((title, text));

        // A second launch signals this event; the UI timer opens the window.
        // The event only accepts administrators; if another program already took the name, we do not trust it.
        var showEv = SecureKernel.CreateAdminEvent(Program.ShowEventName);
        if (showEv == null) Logger.Write("Show-window event unavailable: a second launch will not bring this window to the front.");
        else
        {
            var waiter = new Thread(() => { while (true) { showEv.WaitOne(); _showRequested = true; } }) { IsBackground = true };
            waiter.Start();
        }

        // React to a window switch immediately (instead of waiting for the next 400 ms tick) and warm the new app up.
        Native.WatchForeground(() => Task.Run(() => { try { _engine.OnForeground(); } catch (Exception ex) { LogOnce("Foreground event", ex); } }));
        // Only needed to keep the Win+V host safe from the recycler (and for the diagnostics): no hook when both are off.
        // Always on: the Win+V key press itself wakes the host up (limit lifted + pages read back) before the panel even shows.
        Native.OnPanelKey = () => Task.Run(() => { try { _engine.OnPanelKeyPressed(); } catch (Exception ex) { LogOnce("Panel key", ex); } });
        Native.OnStartKey = () => Task.Run(() => { try { _engine.OnStartKeyPressed(); } catch (Exception ex) { LogOnce("Start key", ex); } });
        Native.OnAltTabKey = () => Task.Run(() => { try { _engine.OnAltTabPressed(); } catch (Exception ex) { LogOnce("Alt+Tab key", ex); } });
        Native.OnMouseDown = (x, y) => Task.Run(() => { try { _engine.OnClickAt(x, y); } catch (Exception ex) { LogOnce("Mouse click", ex); } });
        if (_engine.Config.GlobalHooks) Native.WatchInputPanelKeys();
        else Logger.Write("Global keyboard/mouse hooks are off (GlobalHooks=false in config.json); Alt+Tab is still noticed through the switcher window event.");
        Native.WatchWindowActivity(() => Task.Run(() => { try { _engine.OnPanelOpened(); } catch (Exception ex) { LogOnce("Panel event", ex); } }));
        Logger.Write("InputHost: the Win+V host (TextInputHost) is limited like a background app, and free while its panel is open.");

        _timer = new Timer { Interval = 400 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        if (!Program.StartMinimized) ShowTargets();   // open the window at a normal start; the startup task starts minimized (tray only)
        // Re-check the startup task at every start: it must point at a program normal programs cannot replace
        // (older tasks pointed at wherever the exe was). It is repointed to a protected copy, or removed when that is impossible.
        Task.Run(() =>
        {
            try
            {
                if (Autostart.IsEnabled() && !Autostart.Set(true) && Autostart.Blocked)
                    _balloons.Enqueue(("GameRamCap", "Start with Windows was turned off: this copy is in a place other programs can modify. Install it in Program Files."));
            }
            catch { }
        });
    }

    static void OpenConfigFile()
    {
        try { Process.Start(new ProcessStartInfo(ConfigStore.FilePath) { UseShellExecute = true }); }
        catch
        {
            // No program is associated with .json: fall back to Notepad.
            try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + ConfigStore.FilePath + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Write("Could not open config file: " + ex.Message); }
        }
    }

    void SaveCfg()
    {
        try { ConfigStore.Save(_engine.Config); try { _configStamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath); } catch { } } catch { }
        // re-applied with the new mode on the next tick; off the UI thread, because the engine may hold the lock for a moment
        Task.Run(() => { try { lock (_engine.Gate) _engine.RestoreAll(); } catch (Exception ex) { LogOnce("Restore after save", ex); } });
        RefreshModeChecks();
    }

    ToolStripMenuItem? _intensityMenu;
    ToolStripMenuItem? _hardItem, _smartItem;
    const string HardTag = "__hard", HardProcTag = "__hardproc", HardAppTag = "__hardapp", SmartTag = "__smart";

    /// <summary>Tray: one menu for mode + level. Smart levels set Mode=Smart and the profile; Hard sets Mode=Hard.</summary>
    void SetModeAndLevel(string mode, string? level, string? hardScope = null)
    {
        _engine.Config.Mode = mode;
        if (hardScope != null) _engine.Config.HardScope = hardScope;
        if (level != null) _engine.Config.Profile = level;
        Logger.Write(level != null ? $"Mode set to {mode}, level {level}." : $"Mode set to {mode}.");
        SaveCfg();
    }

    /// <summary>One-shot Boost, shared by the tray menu and the Home page button.</summary>
    Task<bool> BoostNow() => Task.Run(() =>
    {
        bool ok = StandbyCleaner.Boost();
        Logger.Write(ok ? "Boost: emptied working sets and cleared the modified/standby lists." : "Boost: Windows refused (run GameRamCap as administrator).");
        _balloons.Enqueue(("GameRamCap", ok ? "Boost done. Memory was freed." : "Boost failed. Run GameRamCap as administrator."));
        return ok;
    });

    void SetEnabled(bool on)
    {
        _engine.Config.Enabled = on;
        Logger.Write(on ? "Resumed." : "Paused.");
        SaveCfg();
    }

    void SetMode(string mode)
    {
        _engine.Config.Mode = mode;
        Logger.Write($"Mode set to {mode}.");
        SaveCfg();
    }

    void RefreshModeChecks()
    {
        _settings?.Sync();
        _home?.Sync();
        _syncTray = true;
        try { if (_enabledItem != null) _enabledItem.Checked = _engine.Config.Enabled; } finally { _syncTray = false; }
        RefreshProfileChecks();
        _main?.RefreshCustomState();
    }

    void SetProfile(string key)
    {
        _engine.Config.Profile = key;
        try { ConfigStore.Save(_engine.Config); _configStamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath); }
        catch (Exception ex) { Logger.Write("Could not save config: " + ex.Message); }
        _main?.RefreshCustomState();
        Logger.Write($"Intensity set to {key}.");
        RefreshProfileChecks();
        _settings?.Sync();
        _home?.Sync();
    }

    void RefreshProfileChecks()
    {
        bool smart = _engine.Config.IsSmart();
        var cur = Presets.Resolve(_engine.Config).Name;
        foreach (var it in _profileItems)
        {
            string? tag = (string?)it.Tag;
            bool perApp = _engine.Config.HardPerApp();
            it.Checked = tag == HardProcTag ? !smart && !perApp
                       : tag == HardAppTag ? perApp
                       : smart && string.Equals(tag, cur, StringComparison.OrdinalIgnoreCase);
        }
        if (_hardItem != null)
        {
            _hardItem.Checked = !smart;
            _hardItem.Text = smart ? "Hard" : _engine.Config.HardPerApp() ? "Hard - per app" : "Hard - per process";
        }
        if (_smartItem != null) _smartItem.Checked = smart;
        if (_smartItem != null) _smartItem.Text = smart ? $"Smart - {cur}" : "Smart";
    }

    MainWindow? _main;

    void ShowMain(int tab)
    {
        if (_main == null || _main.IsDisposed) _main = new MainWindow(_home!, _settings!);
        _main.Show();
        _main.WindowState = FormWindowState.Normal;
        _main.SelectTab(tab);
        _main.Activate();
    }

    void ShowTargets() => ShowMain(MainWindow.TabHome);

    /// <summary>Settings > Maintenance > Reset to defaults: every setting goes back to its built-in value. Your own lists
    /// (Targets, Games, Exceptions, Fullscreen ignore, Rules, unprotected built-ins) are kept.</summary>
    void ResetToDefaults()
    {
        var ask = MessageBox.Show("Reset all settings to their defaults?\n\nYour own lists (apps, games, exceptions, rules) are kept.",
            "GameRamCap", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;
        try
        {
            var old = _engine.Config;
            var fresh = new Config
            {
                Targets = old.Targets,
                Games = old.Games,
                Exceptions = old.Exceptions,
                FullscreenIgnore = old.FullscreenIgnore,
                Rules = old.Rules,
                UnprotectedBuiltIn = old.UnprotectedBuiltIn,
            };
            ConfigStore.Save(fresh);
            Logger.Write("Settings reset to defaults.");
            LoadConfig();
            _settings?.Sync();
        }
        catch (Exception ex) { Logger.Write("Reset to defaults failed: " + ex.Message); }
    }

    void LoadConfig()
    {
        lock (_engine.Gate)
        {
            _engine.RestoreAll(); // targets may have changed: release old limits, they are re-applied next tick
            _engine.Config = ConfigStore.Load();
        }
        try { _configStamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath); } catch { }
        RefreshProfileChecks();
        RefreshModeChecks();
        _main?.RefreshCustomState();
    }

    volatile bool _reloading, _uiRefreshPending;

    /// <summary>Reads config.json and swaps it in without touching the UI thread (the engine may hold its lock for a moment).</summary>
    void ReloadConfigInBackground()
    {
        if (_reloading) return;
        _reloading = true;
        Task.Run(() =>
        {
            try
            {
                var cfg = ConfigStore.Load();
                lock (_engine.Gate)
                {
                    _engine.RestoreAll();   // targets may have changed: release old limits, they are re-applied next tick
                    _engine.Config = cfg;
                }
                try { _configStamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath); } catch { }
                _uiRefreshPending = true;   // the UI thread refreshes the menus on its next tick
            }
            catch (Exception ex) { LogOnce("Config reload", ex); }
            finally { _reloading = false; }
        });
    }

    void OnTick()
    {
        try
        {
            if (_uiRefreshPending) { _uiRefreshPending = false; RefreshProfileChecks(); RefreshModeChecks(); }
            if (_showRequested) { _showRequested = false; ShowTargets(); }
            while (_balloons.TryDequeue(out var b))
                try { _tray.ShowBalloonTip(4000, b.Item1, b.Item2, ToolTipIcon.Info); } catch { }

            // Hot-reload when the config file is saved.
            var stamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath);
            if (stamp != _configStamp && !_reloading) ReloadConfigInBackground();

            string text = "GameRamCap: " + _engine.Status;
            _statusItem.Text = _engine.Status;
            _tray.Text = text.Length > 63 ? text[..63] : text;
        }
        catch (Exception ex) { LogOnce("UI tick", ex); }

        // The process scan is heavy: run it off the UI thread, never two at once.
        if (_busy) return;
        _busy = true;
        Task.Run(() =>
        {
            try { _engine.Tick(); }
            catch (Exception ex) { LogOnce("Engine tick", ex); }
            finally { _busy = false; }
        });
    }

    void LogOnce(string where, Exception ex)
    {
        if (ex is InvalidOperationException && ex.Message.Contains("modified", StringComparison.OrdinalIgnoreCase)) return;   // a list was edited in the window mid-scan: the next tick is fine
        string msg = $"{where}: {ex.GetType().Name}: {ex.Message}";
        if (msg == _lastErr && (DateTime.UtcNow - _lastErrAt).TotalSeconds < 60) return;
        _lastErr = msg; _lastErrAt = DateTime.UtcNow;
        Logger.Write(msg + Environment.NewLine + ex.StackTrace);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        Native.StopWatchingForeground();
        Native.StopWatchingInputPanelKeys();
        Native.StopWatchingWindowActivity();
        _main?.Shutdown();
        lock (_engine.Gate) _engine.Dispose(); // restores every limited process (waits for a running tick)
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
