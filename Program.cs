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

    public TrayContext()
    {
        LoadConfig();
        _engine.ClearStaleCeilings();

        _statusItem = new ToolStripMenuItem("Idle") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        _enabledItem.CheckedChanged += (_, _) => _engine.Enabled = _enabledItem.Checked;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add("Open config file", null, (_, _) =>
            Process.Start(new ProcessStartInfo(ConfigStore.FilePath) { UseShellExecute = true }));
        menu.Items.Add("Reload config", null, (_, _) => LoadConfig());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "GameRamCap",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _timer = new Timer { Interval = 1000 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    void LoadConfig()
    {
        _engine.Config = ConfigStore.Load();
        try { _configStamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath); } catch { }
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
