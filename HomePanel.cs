using Timer = System.Windows.Forms.Timer;

namespace GameRamCap;

/// <summary>The main page, top to bottom: is it working -> which mode -> that mode's own settings -> a few switches.
/// The other mode's settings are hidden. Everything else is under Advanced.</summary>
internal sealed class HomePanel : UserControl
{
    readonly Func<Config> _cfg;
    readonly Action _save;
    readonly Func<string> _status;
    readonly Func<int> _limitedCount;

    readonly Label _state = new() { AutoSize = true, Font = Ui.Hero };
    readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(Ui.ContentWidth, 0) };
    readonly Label _stats = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly Button _toggle = new() { AutoSize = true, MinimumSize = new Size(110, 30) };

    readonly RadioButton _smart = new() { AutoSize = true, Text = Ui.SmartText };
    readonly RadioButton _hard = new() { AutoSize = true, Text = Ui.HardText };

    // Smart only
    readonly FlowLayoutPanel _smartBox = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    readonly CheckBox _audio = new() { Text = "Protect the app that plays sound / uses the mic", AutoSize = true };

    // Hard (and Custom level): limit for apps that are not in the list
    readonly FlowLayoutPanel _limitBox = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly NumericUpDown _defCeil = new() { Minimum = 20, Maximum = 8000, Value = 300, Width = 70 };
    readonly CheckBox _defFixed = new() { Text = "fixed (never grows)", AutoSize = true, Margin = new Padding(8, 5, 3, 3) };

    // Hard only: scope of the limit
    readonly FlowLayoutPanel _scopeBox = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly RadioButton _scopeProc = new() { AutoSize = true, Text = Ui.ScopeProcText };
    readonly RadioButton _scopeApp = new() { AutoSize = true, Text = Ui.ScopeAppText };
    readonly Label _limitLabel = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

    // both modes
    readonly CheckBox _everything = new() { Text = "Limit all apps, not just my list", AutoSize = true };
    readonly RadioButton _whenAlways = new() { AutoSize = true, Text = "All the time (even when no game is running)" };
    readonly RadioButton _whenGame = new() { AutoSize = true, Text = "Only while a game is running" };

    readonly Timer _timer = new() { Interval = 1000 };
    bool _syncing;

    public HomePanel(Func<Config> cfg, Action save, Func<string> status, Func<int> limitedCount,
                     Action<string> setProfile, Action<string> setMode, Action<bool> setEnabled, Action<int> goTab)
    {
        _cfg = cfg; _save = save; _status = status; _limitedCount = limitedCount;
        Dock = DockStyle.Fill;

        var flow = Ui.PageFlow();

        // 1. Title + status
        flow.Controls.Add(new Label { Text = "GameRamCap", AutoSize = true, Font = Ui.Title });
        flow.Controls.Add(Ui.Note("Caps the RAM of background apps so your games stay fast. The app you are using is never limited."));
        flow.Controls.Add(_state);
        flow.Controls.Add(_detail);
        flow.Controls.Add(_stats);
        _toggle.Margin = new Padding(3, 10, 3, 0);
        _toggle.Click += (_, _) => { if (!_syncing) setEnabled(!_cfg().Enabled); };
        flow.Controls.Add(_toggle);

        // 2. Mode
        flow.Controls.Add(Ui.Heading("Mode"));
        _smart.CheckedChanged += (_, _) => { if (!_syncing && _smart.Checked) setMode("Smart"); };
        _hard.CheckedChanged += (_, _) => { if (!_syncing && _hard.Checked) setMode("Hard"); };
        flow.Controls.Add(_smart);
        flow.Controls.Add(_hard);

        // 3a. Smart: intensity
        _smartBox.Controls.Add(Ui.Heading("Intensity"));
        foreach (var l in Presets.AllLabels) _level.Items.Add(l);
        _level.SelectedIndexChanged += (_, _) => { if (!_syncing && _level.SelectedIndex >= 0) setProfile(Presets.AllNames[_level.SelectedIndex]); };
        _smartBox.Controls.Add(_level);
        _smartBox.Controls.Add(Ui.Note("Ultra Performance frees the most RAM; Ultra Safe is the gentlest. Balanced is a good start."));
        flow.Controls.Add(_smartBox);

        // 3b. Hard (and Custom): the limit
        _limitBox.Controls.Add(Ui.Heading("Limit"));
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        row.Controls.Add(_limitLabel);
        row.Controls.Add(_defCeil);
        row.Controls.Add(_defFixed);
        _limitBox.Controls.Add(row);
        _limitBox.Controls.Add(Ui.Note("You pick apps; how the limit is applied depends on the scope below (Hard). Limits for specific apps: Advanced > Apps."));
        void SaveLimit() { if (_syncing) return; _cfg().SetDefaultLimit((int)_defCeil.Value, _defFixed.Checked); _save(); }
        _defCeil.ValueChanged += (_, _) => SaveLimit();
        _defFixed.CheckedChanged += (_, _) => SaveLimit();
        _scopeBox.Controls.Add(Ui.Heading("Hard limit scope"));
        _scopeBox.Controls.Add(_scopeProc);
        _scopeBox.Controls.Add(_scopeApp);
        _scopeProc.CheckedChanged += (_, _) => { if (!_syncing && _scopeProc.Checked) { _cfg().HardScope = "Process"; _save(); } };
        _scopeApp.CheckedChanged  += (_, _) => { if (!_syncing && _scopeApp.Checked)  { _cfg().HardScope = "App"; _save(); } };
        flow.Controls.Add(_scopeBox);
        flow.Controls.Add(_limitBox);

        // 4. Switches
        flow.Controls.Add(Ui.Heading("Options"));
        Bind(_everything, c => c.LimitEverything, (c, v) => c.LimitEverything = v);
        Bind(_audio, c => c.AudioProtect, (c, v) => c.AudioProtect = v);
        flow.Controls.Add(_everything);
        flow.Controls.Add(_audio);

        flow.Controls.Add(Ui.Heading("When should limits apply?"));
        var when = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        when.Controls.Add(_whenAlways);
        when.Controls.Add(_whenGame);
        flow.Controls.Add(when);
        _whenAlways.CheckedChanged += (_, _) => { if (!_syncing && _whenAlways.Checked) { _cfg().AlwaysLimit = true; _save(); } };
        _whenGame.CheckedChanged   += (_, _) => { if (!_syncing && _whenGame.Checked)   { _cfg().AlwaysLimit = false; _save(); } };

        // 5. Buttons
        var nav = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        nav.Controls.Add(Ui.Btn("Apps that are never limited...", () =>
        {
            using var f = new ExceptionsForm(ConfigStore.Load());
            f.ShowDialog(FindForm());
        }));
        nav.Controls.Add(Ui.Btn("Advanced settings...", () => goTab(MainWindow.TabSettings)));
        flow.Controls.Add(nav);

        flow.Controls.Add(Ui.Note("Closing this window keeps GameRamCap running in the tray. Use Exit in the tray menu to quit."));

        Controls.Add(flow);
        Ui.Polish(this);

        _timer.Tick += (_, _) => { if (Visible) Refresh2(); };
        _timer.Start();
        Sync();
    }

    void Bind(CheckBox box, Func<Config, bool> get, Action<Config, bool> set)
    {
        box.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            set(_cfg(), box.Checked);
            _save();
        };
        box.Tag = get;
    }

    /// <summary>Live status, refreshed every second while the page is visible.</summary>
    void Refresh2()
    {
        var c = _cfg();
        bool on = c.Enabled;
        _state.Text = on ? "● Active" : "● Paused";
        _state.ForeColor = on ? Color.FromArgb(0, 140, 70) : SystemColors.GrayText;
        _detail.Text = _status();
        double free = Native.FreeRamPercent();
        _stats.Text = (free >= 0 ? $"Free RAM: {free:0}%     " : "") + $"Limited processes: {_limitedCount()}";
    }

    /// <summary>Re-reads the config into the controls and shows only what the chosen mode uses.</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            var c = _cfg();
            bool smart = c.IsSmart();
            _toggle.Text = c.Enabled ? "Pause" : "Resume";
            _smart.Checked = smart;
            _hard.Checked = !smart;
            _smartBox.Visible = smart;
            _audio.Visible = smart;
            _limitBox.Visible = !smart || Presets.IsCustom(c);
            _scopeBox.Visible = !smart;
            bool perApp = c.HardPerApp();
            _scopeProc.Checked = !perApp;
            _scopeApp.Checked = perApp;
            _limitLabel.Text = Ui.LimitLabel(perApp);
            string cur = Presets.Resolve(c).Name;
            _level.SelectedIndex = Array.FindIndex(Presets.AllNames, n => string.Equals(n, cur, StringComparison.OrdinalIgnoreCase));
            _defCeil.Value = Math.Clamp(c.DefaultCeilingMB, (int)_defCeil.Minimum, (int)_defCeil.Maximum);
            _defFixed.Checked = !c.DefaultAdaptive;
            _whenAlways.Checked = c.AlwaysLimit;
            _whenGame.Checked = !c.AlwaysLimit;
            foreach (var box in new[] { _everything, _audio })
                box.Checked = ((Func<Config, bool>)box.Tag!)(c);
            Refresh2();
        }
        finally { _syncing = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
