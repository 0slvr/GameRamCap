using System.Diagnostics;
using Timer = System.Windows.Forms.Timer;

namespace GameRamCap;

static class Program
{
    internal const string ShowEventName = "GameRamCap.ShowWindow";

    [STAThread]
    static void Main(string[] args)
    {
        // Watchdog mode: a tiny hidden copy that waits for the main app to die (End Task cannot be caught
        // by the app itself) and then lifts every hard RAM ceiling that was left on other processes.
        if (args.Length >= 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int parentPid))
        {
            try { using var parent = Process.GetProcessById(parentPid); parent.WaitForExit(); } catch { }
            // If a new main instance already started (restart), it owns the ceilings now: do not lift them.
            Thread.Sleep(1000);
            if (Mutex.TryOpenExisting("GameRamCap.SingleInstance", out var live))
            {
                live.Dispose();
                Logger.Write("Watchdog: a new instance is running, nothing released.");
                return;
            }
            try { int n = Engine.ReleaseAllHardCeilings(); Logger.Write($"Watchdog: main app ended, released {n} ceiling(s)."); } catch { }
            return;
        }

        using var mutex = new Mutex(true, "GameRamCap.SingleInstance", out bool first);
        if (!first)
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
            () => { LearnedStore.Clear(); Logger.Write("Learned limits cleared."); },
            Logger.Open);

        _home = new HomePanel(() => _engine.Config, SaveCfg, () => _engine.Status, () => _engine.LimitedCount,
            SetProfile, SetMode, SetEnabled, tab => ShowMain(tab));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
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
            if (Autostart.Set(want)) startup.Checked = want;
            else _balloons.Enqueue(("GameRamCap", "Could not change the startup task."));
        };
        menu.Opening += (_, _) => startup.Checked = Autostart.IsEnabled();
        menu.Items.Add(startup);
        menu.Items.Add("Open log file", null, (_, _) => Logger.Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "GameRamCap",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _tray.DoubleClick += (_, _) => ShowTargets();

        // The engine ticks on a background thread: balloons are queued and shown from the UI timer.
        _engine.Notify += (title, text) => _balloons.Enqueue((title, text));

        // A second launch signals this event; the UI timer opens the window.
        var showEv = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        var waiter = new Thread(() => { while (true) { showEv.WaitOne(); _showRequested = true; } }) { IsBackground = true };
        waiter.Start();

        _timer = new Timer { Interval = 400 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        ShowTargets();   // open the window right at startup (the tray icon is still there)
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
        lock (_engine.Gate) _engine.RestoreAll(); // re-applied with the new mode on the next tick
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

    void ShowMain(int tab, int inner = 0)
    {
        if (_main == null || _main.IsDisposed) _main = new MainWindow(_home!, _settings!);
        _main.Show();
        _main.WindowState = FormWindowState.Normal;
        _main.SelectTab(tab, inner);
        _main.Activate();
    }

    void ShowIntensity() => ShowMain(MainWindow.TabAdvanced, MainWindow.AdvLevels);
    void ShowTargets() => ShowMain(MainWindow.TabHome);

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

    void OnTick()
    {
        try
        {
            if (_showRequested) { _showRequested = false; ShowTargets(); }
            while (_balloons.TryDequeue(out var b))
                try { _tray.ShowBalloonTip(4000, b.Item1, b.Item2, ToolTipIcon.Info); } catch { }

            // Hot-reload when the config file is saved.
            var stamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath);
            if (stamp != _configStamp) LoadConfig();

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
        string msg = $"{where}: {ex.GetType().Name}: {ex.Message}";
        if (msg == _lastErr && (DateTime.UtcNow - _lastErrAt).TotalSeconds < 60) return;
        _lastErr = msg; _lastErrAt = DateTime.UtcNow;
        Logger.Write(msg + Environment.NewLine + ex.StackTrace);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _main?.Shutdown();
        lock (_engine.Gate) _engine.Dispose(); // restores every limited process (waits for a running tick)
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
