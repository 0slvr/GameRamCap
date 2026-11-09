using Timer = System.Windows.Forms.Timer;

namespace GameRamCap;

/// <summary>The main page, top to bottom: is it working -> which mode -> that mode's own settings -> a few switches.
/// The other mode's settings are hidden. Everything else is in the other tabs.</summary>
internal sealed class HomePanel : UserControl
{
    readonly Func<Config> _cfg;
    readonly Action _save;
    readonly Func<string> _status;
    readonly Func<int> _limitedCount;

    readonly Label _state = new() { AutoSize = true, Font = Ui.Hero };
    readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(Ui.ContentWidth, 0) };
    readonly Label _stats = new() { AutoSize = true, Tag = "muted" };
    readonly ModernButton _boost = new() { AutoSize = true, MinimumSize = new Size(132, 38), Text = "Boost now" };
    readonly Label _boostMsg = new() { AutoSize = true, Tag = "muted", Margin = new Padding(8, 9, 3, 0) };
    // Windows memory compression card
    readonly Label _mcState = new() { AutoSize = true };
    readonly ModernButton _mcBtn = new() { AutoSize = true, MinimumSize = new Size(132, 38), Text = "Checking...", Enabled = false };
    readonly Label _mcMsg = new() { AutoSize = true, Tag = "muted", Margin = new Padding(8, 9, 3, 0) };
    bool? _mcOn;
    readonly ModernButton _toggle = new() { AutoSize = true, MinimumSize = new Size(132, 38), Tag = "primary" };

    readonly ModernRadio _smart = new() { AutoSize = true, Text = Ui.SmartText };
    readonly ModernRadio _hard = new() { AutoSize = true, Text = Ui.HardText };

    // Smart only
    readonly FlowLayoutPanel _smartBox = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    // The intensity slider: gentlest -> strongest, then Custom
    static readonly string[] SliderNames  = { "Balanced", "Performance", "UltraPerformance", "Custom" };
    static readonly string[] SliderLabels = { "Balanced", "Performance", "Ultra Performance", "Custom" };
    static readonly string[] SliderDesc =
    {
        "A good middle: frees a fair amount of RAM without making apps slow to come back. A good start.",
        "Strong. Frees a lot of RAM; an app you switch back to may need a moment to wake up.",
        "The strongest preset. Frees the most RAM; background apps are kept very small and may feel slow when you return to them.",
        "Your own numbers. Use this to go lower than Ultra Performance, or to tune it yourself.",
    };
    readonly ModernSteps _level = new() { Width = Ui.ContentWidth, Height = 68, Margin = new Padding(3, 0, 3, 4) };
    readonly Label _levelDesc = new() { AutoSize = true, MaximumSize = new Size(Ui.ContentWidth, 0), Margin = new Padding(3, 2, 3, 2) };
    readonly Label _levelNums = new() { AutoSize = true, Tag = "muted", MaximumSize = new Size(Ui.ContentWidth, 0), Margin = new Padding(3, 0, 3, 6) };

    // Custom level: four plain numbers, and the rest behind "More numbers"
    readonly ToolTip _tip = new();
    readonly FlowLayoutPanel _customBox = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
    readonly FlowLayoutPanel _advBox = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false };
    readonly ModernNumeric _cCeil  = new() { Minimum = Presets.AbsMinMB, Maximum = 2000, Width = 80 };
    readonly ModernNumeric _cScan  = new() { Minimum = 0.5m, Maximum = 10, DecimalPlaces = 1, Increment = 0.5m, Width = 80 };
    readonly ModernNumeric _cGrace = new() { Minimum = 0, Maximum = 30, DecimalPlaces = 1, Increment = 0.5m, Width = 80 };
    readonly ModernNumeric _cCool  = new() { Minimum = 0, Maximum = 30, DecimalPlaces = 1, Increment = 0.5m, Width = 80 };
    readonly ModernNumeric _cScale = new() { Minimum = 0.1m, Maximum = 10, DecimalPlaces = 2, Increment = 0.05m, Width = 80 };
    readonly ModernNumeric _cMinProc = new() { Minimum = 1, Maximum = 1024, Width = 80 };
    readonly ModernNumeric _cCrit  = new() { Minimum = 0.1m, Maximum = 1, DecimalPlaces = 2, Increment = 0.05m, Width = 80 };
    readonly ModernNumeric _cFloor = new() { Minimum = Presets.AbsMinMB, Maximum = 1024, Width = 80 };
    readonly ModernNumeric _cKeep  = new() { Minimum = 1, Maximum = 100, Width = 80 };
    readonly Label _cWarn = new() { AutoSize = true, Tag = "warn", MaximumSize = new Size(Ui.ContentWidth, 0), Margin = new Padding(3, 4, 3, 4), Visible = false };
    readonly List<(ModernNumeric Box, Func<Preset, decimal> Show)> _cNums = new();
    readonly ModernButton _advBtn = new() { AutoSize = true };
    bool _advOpen;

    // Hard (and Custom level): limit for apps that are not in the list
    readonly FlowLayoutPanel _limitBox = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly ModernNumeric _defCeil = new() { Minimum = 20, Maximum = 8000, Value = 300, Width = 70 };
    readonly ModernCheck _defFixed = new() { Text = "fixed (never grows)", AutoSize = true, Margin = new Padding(8, 5, 3, 3) };

    // Hard only: scope of the limit
    readonly FlowLayoutPanel _scopeBox = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly ModernRadio _scopeProc = new() { AutoSize = true, Text = Ui.ScopeProcText };
    readonly ModernRadio _scopeApp = new() { AutoSize = true, Text = Ui.ScopeAppText };
    readonly Label _limitLabel = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

    // both modes
    readonly ModernRadio _whenAlways = new() { AutoSize = true, Text = "All the time (even when no game is running)" };
    readonly ModernCheck _startup = new() { AutoSize = true, Text = "Start with Windows (minimized to the tray)", Margin = new Padding(3, 8, 3, 3) };
    readonly ModernRadio _whenGame = new() { AutoSize = true, Text = "Only while a game is running" };

    readonly ModernButton _more = new() { AutoSize = true };
    Control _smartDetails = null!;
    bool _moreOpen;

    readonly Timer _timer = new() { Interval = 1000 };
    bool _syncing;

    public HomePanel(Func<Config> cfg, Action save, Func<string> status, Func<int> limitedCount,
                     Action<string> setProfile, Action<string> setMode, Action<bool> setEnabled, Action<int> goTab,
                     Control smartDetails, Func<Task<bool>> boost)
    {
        _cfg = cfg; _save = save; _status = status; _limitedCount = limitedCount;
        Dock = DockStyle.Fill;

        var flow = Ui.PageFlow();

        // 1. Title + status card
        flow.Controls.Add(new Label { Text = "GameRamCap", AutoSize = true, Font = Ui.Title, Margin = new Padding(3, 0, 3, 2) });
        flow.Controls.Add(Spaced(Ui.Note("Caps the RAM of background apps so your games stay fast. The app you are using is never limited.")));
        var statusCard = Ui.MakeCard();
        statusCard.Controls.Add(_state);
        statusCard.Controls.Add(_detail);
        statusCard.Controls.Add(_stats);
        _toggle.Margin = new Padding(3, 12, 3, 0);
        _toggle.Click += (_, _) => { if (!_syncing) setEnabled(!_cfg().Enabled); };
        _boost.Margin = new Padding(3, 12, 3, 0);
        _tip.SetToolTip(_boost, "Frees RAM right now: empties app working sets and clears the standby cache. Needs administrator rights. Apps may be a bit slower for a few seconds.");
        _boost.Click += async (_, _) =>
        {
            _boost.Enabled = false;
            _boostMsg.Text = "Boosting...";
            bool ok = false;
            try { ok = await boost(); } catch { }
            if (IsDisposed) return;
            _boostMsg.Text = ok ? "Memory freed." : "Failed. Run as administrator.";
            _boostMsg.ForeColor = ok ? Ui.Good : Ui.Muted;
            _boost.Enabled = true;
            Refresh2();
        };
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        actions.Controls.Add(_toggle);
        actions.Controls.Add(_boost);
        actions.Controls.Add(_boostMsg);
        statusCard.Controls.Add(actions);
        flow.Controls.Add(statusCard);

        // 1b. Windows memory compression (the "Memory Compression" process): switch it off or on
        var mcCard = Ui.MakeCard();
        mcCard.Controls.Add(Ui.Heading("Windows memory compression", true));
        mcCard.Controls.Add(Ui.Note("Windows keeps idle memory compressed in RAM (the \"Memory Compression\" process). Turning it off makes Windows use the page file sooner. " +
                                    "Applies after a restart (Restart, not Shut down)."));
        mcCard.Controls.Add(_mcState);
        _mcBtn.Margin = new Padding(3, 12, 3, 0);
        _tip.SetToolTip(_mcBtn, "Same as PowerShell Disable-MMAgent / Enable-MMAgent -MemoryCompression. Needs administrator rights. GameRamCap never changes it by itself.");
        _mcBtn.Click += async (_, _) =>
        {
            bool turnOn = _mcOn == false;
            if (!turnOn && MessageBox.Show(this, "Turn off Windows memory compression?\n\nIdle memory will go to the page file on disk instead of being compressed in RAM. " +
                    "The change applies after you restart Windows, and you can turn it back on here at any time.",
                    "GameRamCap", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _mcBtn.Enabled = false;
            _mcMsg.Text = "Working...";
            string? err = await Task.Run(() => MemoryCompression.Set(turnOn));
            if (IsDisposed) return;
            _mcMsg.ForeColor = err == null ? Ui.Good : Ui.Muted;
            _mcMsg.Text = err ?? "Done. Restart Windows to apply.";
            RefreshMemComp(keepMsg: true);
        };
        var mcRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        mcRow.Controls.Add(_mcBtn);
        mcRow.Controls.Add(_mcMsg);
        mcCard.Controls.Add(mcRow);
        flow.Controls.Add(mcCard);

        // 2. Mode card (+ the chosen mode's own settings)
        var modeCard = Ui.MakeCard();
        modeCard.Controls.Add(Ui.Heading("Mode", true));
        _smart.CheckedChanged += (_, _) => { if (!_syncing && _smart.Checked) setMode("Smart"); };
        _hard.CheckedChanged += (_, _) => { if (!_syncing && _hard.Checked) setMode("Hard"); };
        modeCard.Controls.Add(_smart);
        modeCard.Controls.Add(_hard);

        // 3a. Smart: intensity (slider + a plain sentence; Custom opens four numbers)
        _smartBox.Controls.Add(Ui.Heading("Intensity"));
        _level.Labels = SliderLabels;
        _level.ValueChanged += (_, _) => { if (!_syncing) setProfile(SliderNames[_level.Value]); };
        _smartBox.Controls.Add(_level);
        _smartBox.Controls.Add(_levelDesc);
        _smartBox.Controls.Add(_levelNums);
        BuildCustomBox();
        _smartBox.Controls.Add(_customBox);
        // Smart's own fine-tuning lives here, inside Smart (collapsed until you ask for it)
        _smartDetails = smartDetails;
        var moreRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _more.Click += (_, _) => { _moreOpen = !_moreOpen; ApplyMore(); };
        moreRow.Controls.Add(_more);
        _smartBox.Controls.Add(moreRow);
        _smartBox.Controls.Add(_smartDetails);
        ApplyMore();
        modeCard.Controls.Add(_smartBox);

        // 3b. Hard (and Custom): the limit
        _limitBox.Controls.Add(Ui.Heading("Limit"));
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        row.Controls.Add(_limitLabel);
        row.Controls.Add(_defCeil);
        row.Controls.Add(_defFixed);
        _limitBox.Controls.Add(row);
        _limitBox.Controls.Add(Ui.Note("You pick apps; how the limit is applied depends on the scope below (Hard). Limits for specific apps: the Apps tab."));
        void SaveLimit() { if (_syncing) return; _cfg().SetDefaultLimit((int)_defCeil.Value, _defFixed.Checked); _save(); }
        _defCeil.ValueChanged += (_, _) => SaveLimit();
        _defFixed.CheckedChanged += (_, _) => SaveLimit();
        _scopeBox.Controls.Add(Ui.Heading("Hard limit scope"));
        _scopeBox.Controls.Add(_scopeProc);
        _scopeBox.Controls.Add(_scopeApp);
        _scopeProc.CheckedChanged += (_, _) => { if (!_syncing && _scopeProc.Checked) { _cfg().HardScope = "Process"; _save(); } };
        _scopeApp.CheckedChanged  += (_, _) => { if (!_syncing && _scopeApp.Checked)  { _cfg().HardScope = "App"; _save(); } };
        modeCard.Controls.Add(_scopeBox);
        modeCard.Controls.Add(_limitBox);
        flow.Controls.Add(modeCard);

        // 4. Options card
        var optCard = Ui.MakeCard();

        optCard.Controls.Add(Ui.Heading("When should limits apply?", true));
        var when = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        when.Controls.Add(_whenAlways);
        when.Controls.Add(_whenGame);
        optCard.Controls.Add(when);
        _whenAlways.CheckedChanged += (_, _) => { if (!_syncing && _whenAlways.Checked) { _cfg().AlwaysLimit = true; _save(); } };
        _whenGame.CheckedChanged   += (_, _) => { if (!_syncing && _whenGame.Checked)   { _cfg().AlwaysLimit = false; _save(); } };
        optCard.Controls.Add(Ui.Heading("Startup"));
        optCard.Controls.Add(_startup);
        _startup.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            bool want = _startup.Checked;
            _startup.Enabled = false;
            Task.Run(() => Autostart.Set(want)).ContinueWith(t =>
            {
                bool ok = t.IsCompletedSuccessfully && t.Result;
                try
                {
                    BeginInvoke(() =>
                    {
                        if (!ok) { _syncing = true; _startup.Checked = !want; _syncing = false; }
                        _startup.Enabled = true;
                        if (!ok && want)
                            MessageBox.Show(this, Autostart.Blocked
                                ? "Start with Windows was not turned on: it needs a copy of GameRamCap in Program Files (a place normal programs cannot change) and that copy could not be made."
                                : "Windows did not accept the startup task. See the log file for the reason.",
                                "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch { }
            });
        };
        // Read the current state without blocking the window (schtasks can take a moment).
        Task.Run(Autostart.IsEnabled).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            void Apply() { _syncing = true; _startup.Checked = t.Result; _syncing = false; }
            try { if (IsHandleCreated) BeginInvoke(Apply); else HandleCreated += (_, _) => Apply(); } catch { }
        });
        flow.Controls.Add(optCard);

        // 5. Buttons
        var nav = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        nav.Controls.Add(Ui.Btn("Apps that are never limited...", () =>
        {
            using var f = new ExceptionsForm(ConfigStore.Load());
            f.ShowDialog(FindForm());
        }));
        flow.Controls.Add(nav);

        flow.Controls.Add(Ui.Note("Closing this window keeps GameRamCap running in the tray. Use Exit in the tray menu to quit."));

        Controls.Add(flow);
        Ui.Polish(this);

        _timer.Tick += (_, _) => { if (Visible) Refresh2(); };
        _timer.Start();
        Sync();
        RefreshMemComp(keepMsg: false);
    }

    /// <summary>Reads the memory compression state once (when the window opens and after a change), not every second.</summary>
    async void RefreshMemComp(bool keepMsg)
    {
        bool? on = await Task.Run(MemoryCompression.IsEnabled);
        if (IsDisposed) return;
        _mcOn = on;
        bool pending = MemoryCompression.RestartPending();
        _mcState.Text = on == null ? "State: unknown (needs administrator rights)"
            : "State: " + (on.Value ? "ON" : "OFF") + (pending ? " (changed, restart Windows to apply)" : "");
        _mcBtn.Text = on == false ? "Turn on" : "Turn off";
        _mcBtn.Enabled = on != null;
        if (!keepMsg) _mcMsg.Text = "";
    }

    static Label Spaced(Label l) { l.Margin = new Padding(3, 0, 3, 18); return l; }

    // ---------------- Custom level ----------------

    void AddCustomRow(FlowLayoutPanel parent, string label, string tip, ModernNumeric box,
                      Func<Preset, decimal> show, Action<PresetOverride, decimal> set)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        var lbl = new Label { Text = label, AutoSize = false, Width = 420, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3) };
        _tip.SetToolTip(lbl, tip);
        _tip.SetToolTip(box, tip);
        box.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            var c = _cfg();
            set(c.CustomLevel ??= new PresetOverride(), box.Value);
            _save();
            UpdateLevelText(c);
        };
        row.Controls.Add(lbl);
        row.Controls.Add(box);
        parent.Controls.Add(row);
        _cNums.Add((box, show));
    }

    void BuildCustomBox()
    {
        _customBox.Controls.Add(Ui.Heading("Your custom numbers", true));
        AddCustomRow(_customBox, "RAM ceiling per background process (MB)",
            "The most RAM one process of a background app may keep. Lower = more RAM for your game. Presets go down to 8 MB; here you can go lower.",
            _cCeil, p => p.HoldMB, (o, v) => o.HoldMB = (int)v);
        AddCustomRow(_customBox, "Check every (seconds)",
            "How often the RAM state is checked. Lower reacts faster.",
            _cScan, p => (decimal)p.Scan, (o, v) => o.Scan = (double)v);
        AddCustomRow(_customBox, "Leave an app alone after I switch away from it (seconds)",
            "An app you just left stays untouched for this long, so a quick Alt+Tab back is instant.",
            _cGrace, p => (decimal)p.Grace, (o, v) => o.Grace = (double)v);
        AddCustomRow(_customBox, "Wait between two trims of the same app (seconds)",
            "Minimum time between two cap passes of the same app.",
            _cCool, p => (decimal)p.Cooldown, (o, v) => o.Cooldown = (double)v);
        _customBox.Controls.Add(_cWarn);

        // the rest, hidden until asked for
        AddCustomRow(_advBox, "Budget scale (lower = squeeze harder)",
            "Multiplies each app's RAM target (ceiling x its process count).",
            _cScale, p => (decimal)p.BudgetScale, (o, v) => o.BudgetScale = (double)v);
        AddCustomRow(_advBox, "Leave processes smaller than this alone (MB)",
            "Processes below this size are not touched. Follows the ceiling unless you set it.",
            _cMinProc, p => p.MinProcMB, (o, v) => o.MinProcMB = (int)v);
        AddCustomRow(_advBox, "Critical factor (when Windows is almost out of RAM)",
            "When free RAM is critical the target and the existing ceilings are multiplied by this (0.1 - 1).",
            _cCrit, p => (decimal)p.CritFactor, (o, v) => o.CritFactor = (double)v);
        AddCustomRow(_advBox, "Never squeeze a process below (MB)",
            "A hard floor so a process is never squeezed smaller than this.",
            _cFloor, p => p.HoldFloorMB, (o, v) => o.FloorMB = (int)v);
        AddCustomRow(_advBox, "Keep at least this % of a process's current size",
            "No process is cut below this share of what it uses now.",
            _cKeep, p => p.HoldKeepPct, (o, v) => o.KeepPct = (int)v);

        var menu = new ContextMenuStrip();
        for (int i = 0; i < Presets.Names.Length; i++)
        {
            string key = Presets.Names[i];
            var item = new ToolStripMenuItem(Presets.Labels[i]);
            item.Click += (_, _) => CopyLevelIntoCustom(key);
            menu.Items.Add(item);
        }
        Button? fromBtn = null;
        fromBtn = Ui.Btn("Start from a level  \u25BE", () => menu.Show(fromBtn!, new Point(0, fromBtn!.Height)));
        _advBtn.Click += (_, _) => { _advOpen = !_advOpen; ApplyAdvanced(); };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(fromBtn);
        buttons.Controls.Add(_advBtn);
        buttons.Controls.Add(Ui.Btn("Reset", () =>
        {
            _cfg().CustomLevel = new PresetOverride();
            _save();
            Sync();
        }));
        _customBox.Controls.Add(buttons);
        _customBox.Controls.Add(_advBox);
        ApplyAdvanced();
    }

    void ApplyAdvanced()
    {
        _advBox.Visible = _advOpen;
        _advBtn.Text = _advOpen ? "Fewer numbers  \u25B4" : "More numbers  \u25BE";
    }

    /// <summary>Copies a level's current numbers (with the user's edits of that level) into Custom, to edit from there.</summary>
    void CopyLevelIntoCustom(string name)
    {
        var c = _cfg();
        var p = Presets.Resolve(new Config { Profile = name, PresetOverrides = c.PresetOverrides });
        c.CustomLevel = new PresetOverride
        {
            Scan = p.Scan, HoldMB = p.HoldMB, Cooldown = p.Cooldown, Grace = p.Grace, BudgetScale = p.BudgetScale,
            MinProcMB = p.MinProcMB, CritFactor = p.CritFactor, FloorMB = p.HoldFloorMB, KeepPct = p.HoldKeepPct,
        };
        _save();
        Sync();
    }

    /// <summary>The sentence and the two key numbers under the slider (and the low-ceiling warning).</summary>
    void UpdateLevelText(Config c)
    {
        var p = Presets.Resolve(c);
        int idx = Array.FindIndex(SliderNames, n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase));
        _levelDesc.Text = idx >= 0 ? SliderDesc[idx] : "";
        _levelNums.Text = $"Ceiling per background process: {p.HoldMB} MB     Checks every {p.Scan:0.#} s";
        bool low = Presets.IsCustomLevel(c) && p.HoldMB < 8;
        _cWarn.Visible = low;
        if (low) _cWarn.Text = "Below 8 MB some apps may freeze or stutter. An app is given room back automatically if it starts to struggle.";
    }

    /// <summary>Live status, refreshed every second while the page is visible.</summary>
    void Refresh2()
    {
        var c = _cfg();
        bool on = c.Enabled;
        _state.Text = on ? "●  Active" : "●  Paused";
        _state.ForeColor = on ? Ui.Good : Ui.Muted;
        _detail.Text = _status();
        double free = Native.FreeRamPercent();
        _stats.Text = (free >= 0 ? $"Free RAM  {free:0}%      \u2022      " : "") + $"Limited processes  {_limitedCount()}";
    }

    void ApplyMore()
    {
        _smartDetails.Visible = _moreOpen;
        _more.Text = _moreOpen ? "Hide fine-tuning  \u25B4" : "Fine-tune Smart mode  \u25BE";
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
            _limitBox.Visible = !smart || Presets.IsCustom(c);
            _scopeBox.Visible = !smart;
            bool perApp = c.HardPerApp();
            _scopeProc.Checked = !perApp;
            _scopeApp.Checked = perApp;
            _limitLabel.Text = Ui.LimitLabel(perApp);
            string cur = Presets.Resolve(c).Name;
            int idx = Array.FindIndex(SliderNames, n => string.Equals(n, cur, StringComparison.OrdinalIgnoreCase));
            _level.Value = idx >= 0 ? idx : 0;
            _customBox.Visible = smart && Presets.IsCustomLevel(c);
            if (Presets.IsCustomLevel(c))
            {
                var p = Presets.Resolve(c);
                foreach (var (box, show) in _cNums) box.Value = Math.Clamp(show(p), box.Minimum, box.Maximum);
            }
            UpdateLevelText(c);
            _defCeil.Value = Math.Clamp(c.DefaultCeilingMB, (int)_defCeil.Minimum, (int)_defCeil.Maximum);
            _defFixed.Checked = !c.DefaultAdaptive;
            _whenAlways.Checked = c.AlwaysLimit;
            _whenGame.Checked = !c.AlwaysLimit;
            Refresh2();
        }
        finally { _syncing = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
