using System.Diagnostics;

namespace GameRamCap;

/// <summary>Main window: choose the experiment, visible-mode and apps, see the live status and log.</summary>
internal sealed class MainForm : Form
{
    readonly Engine _engine;
    readonly Action _save;
    readonly Dictionary<string, RadioButton> _exp = new();
    readonly Dictionary<string, RadioButton> _vis = new();
    readonly Button _toggle = new() { Height = 44, Dock = DockStyle.Top, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    readonly Label _status = new() { Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(0, 8, 0, 0) };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    string _startKey = "E0";
    bool _syncing;
    string _lastLog = "";

    /// <summary>Set by the tray before it really exits; otherwise closing the window only hides it.</summary>
    public bool ReallyExit;

    static readonly (string Key, string Label)[] Experiments =
    {
        ("E0", "E0   Baseline - measure only, change nothing"),
        ("E1", "E1   Memory Priority + audio protection"),
        ("E2s", "E2   Working Set SOFT (idle child processes)"),
        ("E2h", "E2   Working Set HARD (idle child processes)"),
        ("E5", "E5   Thread memory priority"),
        ("E6", "E6   Adaptive controller"),
        ("E8", "E8   Suspend + trim  (RISKY)"),
    };

    Config Cfg => _engine.Config;
    bool Running => !string.Equals(Cfg.Experiment, "Off", StringComparison.OrdinalIgnoreCase);

    public MainForm(Engine engine, Action save)
    {
        _engine = engine;
        _save = save;
        Text = "GameRamCap Lab";
        ClientSize = new Size(760, 600);
        MinimumSize = new Size(700, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tabMain = new TabPage("Experiment");
        var tabApps = new TabPage("Apps");
        tabApps.Controls.Add(new TargetsPanel());
        tabs.TabPages.Add(tabMain);
        tabs.TabPages.Add(tabApps);
        Controls.Add(tabs);

        // ---- experiments (left)
        var expBox = new GroupBox { Text = "Experiment (one at a time)", Dock = DockStyle.Fill };
        var expFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8, 6, 8, 6) };
        foreach (var (key, label) in Experiments)
        {
            var rb = new RadioButton { Text = label, AutoSize = true, Margin = new Padding(3, 5, 3, 5) };
            if (key == "E8") rb.ForeColor = Color.Firebrick;
            string k = key;
            rb.CheckedChanged += (_, _) =>
            {
                if (_syncing || !rb.Checked) return;
                _startKey = k;
                if (Running) { Cfg.Experiment = k; _save(); }
            };
            _exp[key] = rb;
            expFlow.Controls.Add(rb);
        }
        expBox.Controls.Add(expFlow);

        // ---- settings (right)
        var visBox = new GroupBox { Text = "App visible but not focused", Dock = DockStyle.Top, Height = 84 };
        var visFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 2, 6, 2) };
        foreach (var (key, label) in new[] { ("Background", "Treat as background (reduce it)"), ("Foreground", "Treat as foreground (leave it)") })
        {
            var rb = new RadioButton { Text = label, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
            string k = key;
            rb.CheckedChanged += (_, _) => { if (_syncing || !rb.Checked) return; Cfg.VisibleMode = k; _save(); };
            _vis[key] = rb;
            visFlow.Controls.Add(rb);
        }
        visBox.Controls.Add(visFlow);

        _toggle.Click += (_, _) =>
        {
            Cfg.Experiment = Running ? "Off" : _startKey;
            Logger.Write($"Lab experiment set to {Cfg.Experiment}.");
            _save();
            SyncFromConfig();
        };

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 0, 0) };
        right.Controls.Add(_status);
        right.Controls.Add(_toggle);
        right.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
        right.Controls.Add(visBox);

        // ---- bottom: log + shortcuts
        var shortcuts = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        shortcuts.Controls.Add(MakeButton("Open lab folder (CSV)", () => { try { Directory.CreateDirectory(LabCsv.Dir); Process.Start(new ProcessStartInfo(LabCsv.Dir) { UseShellExecute = true }); } catch { } }));
        shortcuts.Controls.Add(MakeButton("Open log", Logger.Open));
        shortcuts.Controls.Add(MakeButton("Open config file", () => { try { Process.Start(new ProcessStartInfo(ConfigStore.FilePath) { UseShellExecute = true }); } catch { } }));
        var logBox = new GroupBox { Text = "Log (latest events)", Dock = DockStyle.Fill };
        logBox.Controls.Add(_log);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 270));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(expBox, 0, 0);
        root.Controls.Add(right, 1, 0);
        root.Controls.Add(logBox, 0, 1);
        root.SetColumnSpan(logBox, 2);
        root.Controls.Add(shortcuts, 0, 2);
        root.SetColumnSpan(shortcuts, 2);
        tabMain.Controls.Add(root);

        var cur = Cfg.Experiment;
        if (Experiments.Any(e => string.Equals(e.Key, cur, StringComparison.OrdinalIgnoreCase)))
            _startKey = Experiments.First(e => string.Equals(e.Key, cur, StringComparison.OrdinalIgnoreCase)).Key;

        SyncFromConfig();
        _timer.Tick += (_, _) => { if (Visible) { SyncFromConfig(); RefreshLog(); } };
        _timer.Start();
        Shown += (_, _) => RefreshLog();
    }

    static Button MakeButton(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => click();
        return b;
    }

    void SyncFromConfig()
    {
        _syncing = true;
        try
        {
            string cur = Cfg.Experiment;
            foreach (var (key, rb) in _exp)
                rb.Checked = string.Equals(key, Running ? cur : _startKey, StringComparison.OrdinalIgnoreCase);
            foreach (var (key, rb) in _vis) rb.Checked = string.Equals(key, Cfg.VisibleMode, StringComparison.OrdinalIgnoreCase);
            _toggle.Text = Running ? "■  Stop experiment" : "▶  Start experiment";
            _toggle.BackColor = Running ? Color.FromArgb(255, 205, 205) : Color.FromArgb(205, 240, 205);
            _status.Text = Running ? _engine.Status : "Stopped - nothing is being changed.";
        }
        finally { _syncing = false; }
    }

    void RefreshLog()
    {
        try
        {
            if (!File.Exists(Logger.FilePath)) return;
            var lines = new List<string>();
            using (var fs = new FileStream(Logger.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
            {
                string? l;
                while ((l = sr.ReadLine()) != null) lines.Add(l);
            }
            string text = string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - 30)));
            if (text == _lastLog) return;
            _lastLog = text;
            _log.Text = text;
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ReallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();   // the experiment keeps running from the tray icon
            return;
        }
        _timer.Stop();
        base.OnFormClosing(e);
    }
}
