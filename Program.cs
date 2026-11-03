using System.Diagnostics;
using Timer = System.Windows.Forms.Timer;

namespace GameRamCap;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Watchdog mode: a tiny hidden copy that waits for the main app to die (End Task cannot be caught
        // by the app itself) and then lifts every hard RAM ceiling that was left on other processes.
        if (args.Length >= 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int parentPid))
        {
            try { using var parent = Process.GetProcessById(parentPid); parent.WaitForExit(); } catch { }
            try { int n = Engine.ReleaseAllHardCeilings(); Logger.Write($"Watchdog: main app ended, released {n} ceiling(s)."); } catch { }
            try { Engine.LabResumeLeftovers(); } catch { }
            return;
        }

        using var mutex = new Mutex(true, "GameRamCap.SingleInstance", out bool first);
        if (!first) return;

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
    TargetsForm? _targetsForm;
    readonly List<ToolStripMenuItem> _profileItems = new();
    readonly List<ToolStripMenuItem> _modeItems = new();
    readonly List<ToolStripMenuItem> _labItems = new();
    readonly List<ToolStripMenuItem> _delayItems = new();
    readonly List<ToolStripMenuItem> _visItems = new();
    ToolStripMenuItem _holdItem = null!, _everythingItem = null!, _focusItem = null!,
                      _fullscreenItem = null!, _pressureItem = null!, _autoItem = null!,
                      _audioItem = null!, _prioItem = null!;
    bool _syncing;

    public TrayContext()
    {
        LoadConfig();
        Logger.Write($"Started. Games=[{string.Join(", ", _engine.Config.Games)}] AutoDetectFullscreen={_engine.Config.AutoDetectFullscreen} Delay={_engine.Config.DelaySeconds}s");
        _engine.ClearStaleCeilings();
        _engine.ClearStaleCeilingsEverything();
        Engine.LabResumeLeftovers();

        _statusItem = new ToolStripMenuItem("Idle") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        _enabledItem.CheckedChanged += (_, _) => _engine.Enabled = _enabledItem.Checked;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        var intensity = new ToolStripMenuItem("Intensity");
        for (int i = 0; i < Presets.Names.Length; i++)
        {
            string key = Presets.Names[i];
            var item = new ToolStripMenuItem(Presets.Labels[i]) { Tag = key };
            item.Click += (_, _) => SetProfile(key);
            _profileItems.Add(item);
            intensity.DropDownItems.Add(item);
        }
        var customItem = new ToolStripMenuItem("Custom (use config.json numbers)") { Tag = "Custom" };
        customItem.Click += (_, _) => SetProfile("Custom");
        _profileItems.Add(customItem);
        intensity.DropDownItems.Add(new ToolStripSeparator());
        intensity.DropDownItems.Add(customItem);
        intensity.DropDownItems.Add("Edit levels...", null, (_, _) => ShowIntensity());
        menu.Items.Add(intensity);
        RefreshProfileChecks();
        // ---- Modes: the user decides how the tool behaves ----
        var modeMenu = new ToolStripMenuItem("Mode");
        foreach (var (key, label) in new[] { ("Smart", "Smart (gentle, adaptive)"), ("Hard", "Hard (fixed ceilings)") })
        {
            var mi = new ToolStripMenuItem(label) { Tag = key };
            mi.Click += (_, _) => { SetMode(key); };
            _modeItems.Add(mi);
            modeMenu.DropDownItems.Add(mi);
        }
        menu.Items.Add(modeMenu);

        // ---- LAB: one experiment at a time ----
        var labMenu = new ToolStripMenuItem("Lab experiment");
        foreach (var (key, label) in new[]
        {
            ("Off", "Off (normal GameRamCap)"), ("E0", "E0  Baseline (measure only)"),
            ("E1", "E1  Memory Priority + audio protection"), ("E2s", "E2  Working Set SOFT (idle children)"),
            ("E2h", "E2  Working Set HARD (idle children)"), ("E5", "E5  Thread memory priority"),
            ("E6", "E6  Adaptive controller"), ("E8", "E8  Suspend + trim (RISKY)"),
        })
        {
            var mi = new ToolStripMenuItem(label) { Tag = key };
            mi.Click += (_, _) => { _engine.Config.Experiment = key; Logger.Write($"Lab experiment set to {key}."); SaveCfg(); };
            _labItems.Add(mi);
            labMenu.DropDownItems.Add(mi);
        }
        labMenu.DropDownItems.Add(new ToolStripSeparator());
        var delayMenu = new ToolStripMenuItem("Delay before reducing");
        foreach (var sec in new[] { 5, 30, 120 })
        {
            var di = new ToolStripMenuItem($"{sec} seconds") { Tag = sec };
            di.Click += (_, _) => { _engine.Config.UnfocusDelaySeconds = sec; SaveCfg(); };
            _delayItems.Add(di);
            delayMenu.DropDownItems.Add(di);
        }
        labMenu.DropDownItems.Add(delayMenu);
        var visMenu = new ToolStripMenuItem("Visible but not focused app");
        foreach (var (key, label) in new[] { ("Background", "Treat as background (reduce it)"), ("Foreground", "Treat as foreground (leave it)") })
        {
            var vi = new ToolStripMenuItem(label) { Tag = key };
            vi.Click += (_, _) => { _engine.Config.VisibleMode = key; SaveCfg(); };
            _visItems.Add(vi);
            visMenu.DropDownItems.Add(vi);
        }
        labMenu.DropDownItems.Add(visMenu);
        labMenu.DropDownItems.Add("Open lab folder (CSV files)", null, (_, _) =>
        {
            try { Directory.CreateDirectory(LabCsv.Dir); Process.Start(new ProcessStartInfo(LabCsv.Dir) { UseShellExecute = true }); } catch { }
        });
        menu.Items.Add(labMenu);

        var options = new ToolStripMenuItem("Options");
        ToolStripMenuItem Toggle(string text, Action<bool> apply)
        {
            var t = new ToolStripMenuItem(text) { CheckOnClick = true };
            t.CheckedChanged += (_, _) => { if (!_syncing) { apply(t.Checked); SaveCfg(); } };
            options.DropDownItems.Add(t);
            return t;
        }
        _focusItem      = Toggle("Focus mode (never limit the app I'm using)", v => _engine.Config.FocusMode = v);
        _everythingItem = Toggle("Limit all apps, not just my list",           v => _engine.Config.LimitEverything = v);
        _holdItem       = Toggle("Hold background apps (hard ceiling, Smart)",  v => _engine.Config.HoldBackground = v);
        _autoItem       = Toggle("Auto-squeeze (lowest RAM without stutter)", v => _engine.Config.AutoSqueeze = v);
        _pressureItem   = Toggle("Only limit when RAM is low (Custom profile)", v => _engine.Config.LimitWhenRamFree = !v);
        _fullscreenItem = Toggle("Auto-detect fullscreen games",                v => _engine.Config.AutoDetectFullscreen = v);
        _audioItem      = Toggle("Protect only the process that plays sound / uses the mic",  v => _engine.Config.AudioProtect = v);
        _prioItem       = Toggle("Low memory priority for background apps",     v => _engine.Config.LowerMemoryPriority = v);
        menu.Items.Add(options);
        RefreshModeChecks();
        menu.Items.Add("Choose apps...", null, (_, _) => ShowTargets());
        menu.Items.Add("Open config file", null, (_, _) =>
            Process.Start(new ProcessStartInfo(ConfigStore.FilePath) { UseShellExecute = true }));
        menu.Items.Add("Reload config", null, (_, _) => LoadConfig());
        menu.Items.Add("Forget learned limits", null, (_, _) => { LearnedStore.Clear(); Logger.Write("Learned limits cleared."); });
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

        _engine.Notify += (title, text) =>
        {
            try { _tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Info); } catch { }
        };

        _timer = new Timer { Interval = 400 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    void SaveCfg()
    {
        try { ConfigStore.Save(_engine.Config); try { _configStamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath); } catch { } } catch { }
        _engine.RestoreAll(); // re-applied with the new mode on the next tick
        RefreshModeChecks();
    }

    void SetMode(string mode)
    {
        _engine.Config.Mode = mode;
        Logger.Write($"Mode set to {mode}.");
        SaveCfg();
    }

    void RefreshModeChecks()
    {
        if (_focusItem == null || _everythingItem == null || _holdItem == null
            || _pressureItem == null || _fullscreenItem == null || _autoItem == null
            || _audioItem == null || _prioItem == null) return; // menu not built yet (LoadConfig runs first)
        _syncing = true;
        try
        {
            var c = _engine.Config;
            foreach (var it in _labItems) it.Checked = string.Equals((string?)it.Tag, c.Experiment, StringComparison.OrdinalIgnoreCase);
            foreach (var it in _delayItems) it.Checked = Math.Abs((int)it.Tag! - c.UnfocusDelaySeconds) < 0.01;
            foreach (var it in _visItems) it.Checked = string.Equals((string?)it.Tag, c.VisibleMode, StringComparison.OrdinalIgnoreCase);
            foreach (var it in _modeItems)
                it.Checked = string.Equals((string?)it.Tag, c.IsSmart() ? "Smart" : "Hard", StringComparison.OrdinalIgnoreCase);
            _focusItem.Checked = c.FocusMode;
            _everythingItem.Checked = c.LimitEverything;
            _holdItem.Checked = c.HoldBackground;
            _holdItem.Enabled = c.IsSmart();
            _autoItem.Checked = c.AutoSqueeze;
            _autoItem.Enabled = c.IsSmart();
            _pressureItem.Checked = !c.LimitWhenRamFree;
            _fullscreenItem.Checked = c.AutoDetectFullscreen;
            _audioItem.Checked = c.AudioProtect;
            _audioItem.Enabled = c.IsSmart();
            _prioItem.Checked = c.LowerMemoryPriority;
            _prioItem.Enabled = c.IsSmart();
        }
        finally { _syncing = false; }
    }

    void SetProfile(string key)
    {
        _engine.Config.Profile = key;
        try { ConfigStore.Save(_engine.Config); } catch { }
        Logger.Write($"Intensity set to {key}.");
        RefreshProfileChecks();
    }

    void RefreshProfileChecks()
    {
        var cur = Presets.Resolve(_engine.Config).Name;
        foreach (var it in _profileItems)
            it.Checked = string.Equals((string?)it.Tag, cur, StringComparison.OrdinalIgnoreCase);
    }

    IntensityForm? _intensityForm;
    void ShowIntensity()
    {
        if (_intensityForm is { IsDisposed: false }) { _intensityForm.Activate(); return; }
        _intensityForm = new IntensityForm();
        _intensityForm.Show();
    }

    void ShowTargets()
    {
        if (_targetsForm is { IsDisposed: false }) { _targetsForm.Activate(); return; }
        _targetsForm = new TargetsForm();
        _targetsForm.Show();
    }

    void LoadConfig()
    {
        _engine.RestoreAll(); // targets may have changed: release old limits, they are re-applied next tick
        _engine.Config = ConfigStore.Load();
        try { _configStamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath); } catch { }
        RefreshProfileChecks();
        RefreshModeChecks();
    }

    void OnTick()
    {
        try
        {
            // Hot-reload when the config file is saved.
            var stamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath);
            if (stamp != _configStamp) LoadConfig();

            _engine.Tick();
            _statusItem.Text = _engine.Status;
            _tray.Text = ("GameRamCap: " + _engine.Status).Length > 63
                ? ("GameRamCap: " + _engine.Status)[..63]
                : "GameRamCap: " + _engine.Status;
        }
        catch { /* never crash the tray because of one bad tick */ }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _engine.Dispose(); // restores every limited process
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
