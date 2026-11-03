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
    readonly MainForm _main;
    DateTime _configStamp;

    public TrayContext()
    {
        LoadConfig();
        Logger.Write($"Started. Experiment={_engine.Config.Experiment} VisibleMode={_engine.Config.VisibleMode}");
        Engine.ReleaseAllHardCeilings();   // clean up anything a crashed previous run left behind
        Engine.LabResumeLeftovers();

        _main = new MainForm(_engine, SaveCfg);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open window", null, (_, _) => ShowMain());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "GameRamCap Lab",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMain();

        _timer = new Timer { Interval = 400 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        ShowMain();
    }

    void ShowMain()
    {
        if (!_main.Visible) _main.Show();
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
    }

    void SaveCfg()
    {
        try { ConfigStore.Save(_engine.Config); try { _configStamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath); } catch { } } catch { }
        _engine.RestoreAll(); // re-applied with the new settings on the next tick
    }

    void LoadConfig()
    {
        _engine.RestoreAll(); // settings may have changed: undo, they are re-applied next tick
        _engine.Config = ConfigStore.Load();
        try { _configStamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath); } catch { }
    }

    void OnTick()
    {
        try
        {
            // Hot-reload when the config file is saved (for example from the Apps tab).
            var stamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath);
            if (stamp != _configStamp) LoadConfig();

            _engine.Tick();
            string t = "GameRamCap Lab: " + _engine.Status;
            _tray.Text = t.Length > 63 ? t[..63] : t;
        }
        catch { /* never crash the tray because of one bad tick */ }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _engine.Dispose(); // restores everything the experiment changed
        _main.ReallyExit = true;
        _main.Close();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
