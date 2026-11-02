using System.Diagnostics;
using Timer = System.Windows.Forms.Timer;

namespace GameRamCap;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "GameRamCap.SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
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

    public TrayContext()
    {
        LoadConfig();
        Logger.Write($"Started. Games=[{string.Join(", ", _engine.Config.Games)}] AutoDetectFullscreen={_engine.Config.AutoDetectFullscreen} Delay={_engine.Config.DelaySeconds}s");
        _engine.ClearStaleCeilings();
        _engine.ClearStaleCeilingsEverything();

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
        menu.Items.Add(intensity);
        RefreshProfileChecks();
        menu.Items.Add("Choose apps...", null, (_, _) => ShowTargets());
        menu.Items.Add("Open config file", null, (_, _) =>
            Process.Start(new ProcessStartInfo(ConfigStore.FilePath) { UseShellExecute = true }));
        menu.Items.Add("Reload config", null, (_, _) => LoadConfig());
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
