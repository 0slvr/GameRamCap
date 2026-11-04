namespace GameRamCap;

/// <summary>The "Settings" tab: everything that used to live in the tray's Mode / Options submenus,
/// plus the maintenance actions (config file, reload, learned limits, log). Behaviour is unchanged;
/// only the place moved so the tray menu stays short.</summary>
internal sealed class SettingsPanel : UserControl
{
    readonly Func<Config> _cfg;
    readonly Action _save;
    readonly List<(CheckBox Box, Func<Config, bool> Get, bool SmartOnly)> _boxes = new();
    readonly RadioButton _smart = new() { AutoSize = true, Text = Ui.SmartText };
    readonly RadioButton _hard = new() { AutoSize = true, Text = Ui.HardText };
    readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    readonly ToolTip _tip = new();
    readonly NumericUpDown _delay = new() { Minimum = 0, Maximum = 120, Width = 60 };
    readonly List<(NumericUpDown Box, Func<Config, decimal> Get, bool CustomOnly)> _nums = new();
    readonly TextBox _games = new() { Multiline = true, Width = 300, Height = 90, ScrollBars = ScrollBars.Vertical };
    readonly TextBox _fsIgnore = new() { Multiline = true, Width = 300, Height = 90, ScrollBars = ScrollBars.Vertical };
    FlowLayoutPanel _smartPanel = null!, _hardPanel = null!, _customPanel = null!;
    readonly NumericUpDown _defCeil = new() { Minimum = 20, Maximum = 8000, Value = 300, Width = 70 };
    readonly CheckBox _defFixed = new() { Text = "fixed (never grows)", AutoSize = true, Margin = new Padding(8, 5, 3, 3) };
    FlowLayoutPanel _defaultsPanel = null!;
    readonly RadioButton _scopeProc = new() { AutoSize = true, Text = Ui.ScopeProcText };
    readonly RadioButton _scopeApp = new() { AutoSize = true, Text = Ui.ScopeAppText };
    readonly Label _defLabel = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    /// <summary>The settings sub-pages (Mode, General, Games &amp; lists, Tools). MainWindow shows them in its Advanced tab.</summary>
    public readonly List<TabPage> Pages = new();
    readonly RadioButton _whenAlways = new() { AutoSize = true, Text = "All the time (even when no game is running)" };
    readonly RadioButton _whenGame = new() { AutoSize = true, Text = "Only while a game is running" };
    bool _syncing;

    public SettingsPanel(Func<Config> cfg, Action save, Action<string> setProfile, Action openConfig, Action reload, Action forgetLearned, Action openLog)
    {
        _cfg = cfg;
        _save = save;
        Dock = DockStyle.Fill;

        FlowLayoutPanel Page(string title)
        {
            var page = new TabPage(title);
            Ui.Flat(page);
            var f = Ui.PageFlow();
            page.Controls.Add(f);
            Pages.Add(page);
            return f;
        }
        static Label Note(string text) => Ui.Note(text);

        FlowLayoutPanel Group()
        {
            return new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        }

        // ================= 1. Mode (each mode shows only its own settings) =================
        var modePage = Page("Mode");
        modePage.Controls.Add(Heading("Mode"));
        _smart.CheckedChanged += (_, _) => { if (!_syncing && _smart.Checked) SetMode("Smart"); };
        _hard.CheckedChanged += (_, _) => { if (!_syncing && _hard.Checked) SetMode("Hard"); };
        modePage.Controls.Add(_smart);
        modePage.Controls.Add(_hard);

        // ---- Smart settings ----
        _smartPanel = Group();
        var sm = _smartPanel;
        sm.Controls.Add(Heading("Smart mode - intensity"));
        foreach (var label in Presets.AllLabels) _level.Items.Add(label);
        _level.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _level.SelectedIndex < 0) return;
            setProfile(Presets.AllNames[_level.SelectedIndex]);
        };
        sm.Controls.Add(_level);
        sm.Controls.Add(Note("The five levels use the table in the \"Intensity levels\" tab. Custom uses its own numbers (below) and the " +
                             "Limit / Min / Max of each app from Advanced > Apps."));

        sm.Controls.Add(Heading("When to trim"));
        Add(sm, "Only limit when RAM is low (below the level's pressure %)", c => !c.LimitWhenRamFree,  (c, v) => c.LimitWhenRamFree = !v,  true);

        sm.Controls.Add(Heading("Trimming and holding"));
        Add(sm, "Gentle trim before capping (five levels)",                  c => c.SoftTrim,            (c, v) => c.SoftTrim = v,            true);
        Add(sm, "Hold background apps (hard ceiling)",                       c => c.HoldBackground,      (c, v) => c.HoldBackground = v,      true);
        Add(sm, "Remember safe limits per app (learned.json)",               c => c.LearnFloors,         (c, v) => c.LearnFloors = v,         true);

        sm.Controls.Add(Heading("Auto-squeeze (lowest RAM without stutter)"));
        Add(sm, "Auto-squeeze (lowest RAM without stutter)",                 c => c.AutoSqueeze,         (c, v) => c.AutoSqueeze = v,         true);
        AddNum(sm, "Never go below (MB per process)",                        c => c.AutoMinProcessMB, (c, v) => c.AutoMinProcessMB = (int)v, 8, 1024, 0, false, "AutoMinProcessMB");
        AddNum(sm, "Page faults per second that count as stutter",           c => c.AutoFaultRate,    (c, v) => c.AutoFaultRate = (int)v,    10, 5000, 0, false, "AutoFaultRate");
        AddNum(sm, "Quiet seconds before lowering one more step",            c => c.AutoProbeSeconds, (c, v) => c.AutoProbeSeconds = (int)v, 3, 600, 0, false, "AutoProbeSeconds");

        sm.Controls.Add(Heading("Memory priority"));
        Add(sm, "Low memory priority for background apps",                   c => c.LowerMemoryPriority, (c, v) => c.LowerMemoryPriority = v, true);
        Add(sm, "Also lower thread memory priority (experimental)",          c => c.ThreadMemoryPriority,(c, v) => c.ThreadMemoryPriority = v,true);
        AddNum(sm, "Priority of background apps (1 = lowest, 4)",            c => c.BackgroundMemoryPriority, (c, v) => c.BackgroundMemoryPriority = (int)v, 1, 4, 0, false,
            "BackgroundMemoryPriority - needs \"Low memory priority\".");

        sm.Controls.Add(Heading("Sound protection"));
        Add(sm, "Protect the app that plays sound / uses the mic",           c => c.AudioProtect,        (c, v) => c.AudioProtect = v,        true);
        AddNum(sm, "Keep protecting after the sound stops (s)", c => (decimal)c.AudioProtectGraceSeconds, (c, v) => c.AudioProtectGraceSeconds = (double)v, 0, 120, 1, false,
            "AudioProtectGraceSeconds");

        // Custom numbers: shown only while Intensity = Custom
        _customPanel = Group();
        var cus = _customPanel;
        cus.Controls.Add(Heading("Custom level numbers"));
        AddNum(cus, "Start trimming below free RAM %",                       c => c.PressureFreePercent, (c, v) => c.PressureFreePercent = (int)v, 1, 90, 0, false, "PressureFreePercent");
        AddNum(cus, "Trim harder below free RAM %",                          c => c.CriticalFreePercent, (c, v) => c.CriticalFreePercent = (int)v, 1, 90, 0, false, "CriticalFreePercent");
        AddNum(cus, "Check RAM every (s)",                                   c => (decimal)c.SmartScanSeconds, (c, v) => c.SmartScanSeconds = (double)v, 0.5m, 60, 1, false, "SmartScanSeconds");
        AddNum(cus, "Minimum seconds between trims of one app",              c => (decimal)c.TrimCooldownSeconds, (c, v) => c.TrimCooldownSeconds = (double)v, 0, 600, 1, false, "TrimCooldownSeconds");
        AddNum(cus, "App untouched after you leave it (s)",                  c => (decimal)c.GraceAfterFocusSeconds, (c, v) => c.GraceAfterFocusSeconds = (double)v, 0, 120, 1, false, "GraceAfterFocusSeconds");
        AddNum(cus, "Smallest ceiling per process (MB)",                     c => c.HoldMinProcessMB, (c, v) => c.HoldMinProcessMB = (int)v, 8, 1024, 0, false, "HoldMinProcessMB");
        AddNum(cus, "Keep at least this % of current size",                  c => c.HoldKeepPercent,  (c, v) => c.HoldKeepPercent = (int)v,  0, 100, 0, false, "HoldKeepPercent");
        sm.Controls.Add(cus);
        modePage.Controls.Add(sm);

        // ---- Hard settings ----
        _hardPanel = Group();
        var hd = _hardPanel;
        hd.Controls.Add(Heading("Hard mode"));
        hd.Controls.Add(_scopeProc);
        hd.Controls.Add(_scopeApp);
        _scopeProc.CheckedChanged += (_, _) => { if (!_syncing && _scopeProc.Checked) { _cfg().HardScope = "Process"; _save(); } };
        _scopeApp.CheckedChanged  += (_, _) => { if (!_syncing && _scopeApp.Checked)  { _cfg().HardScope = "App"; _save(); } };
        hd.Controls.Add(Note("A fixed RAM ceiling that stays until you focus the app. Numbers per app: Advanced > Apps (Limit MB, Min / Max MB, " +
                             "Adaptive - per process only). Apps not in the list use the limit below. The Smart options do not apply here, so they are hidden."));
        modePage.Controls.Add(hd);

        // ---- Limit for apps that are not in the Apps list: used by Hard mode and by the Custom level only ----
        _defaultsPanel = Group();
        _defaultsPanel.Controls.Add(Heading("Apps not in the list"));
        var defRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        defRow.Controls.Add(_defLabel);
        defRow.Controls.Add(_defCeil);
        defRow.Controls.Add(_defFixed);
        _defaultsPanel.Controls.Add(defRow);
        _defaultsPanel.Controls.Add(Note("Applies to every app you did not add in Advanced > Apps (needs \"Limit all apps\" in General)."));
        void SaveDefaults()
        {
            if (_syncing) return;
            _cfg().SetDefaultLimit((int)_defCeil.Value, _defFixed.Checked);
            _save();
        }
        _defCeil.ValueChanged += (_, _) => SaveDefaults();
        _defFixed.CheckedChanged += (_, _) => SaveDefaults();
        modePage.Controls.Add(_defaultsPanel);

        // ================= 2. General (both modes) =================
        var gen = Page("General");
        gen.Controls.Add(Heading("When should limits apply?"));
        var when = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        when.Controls.Add(_whenAlways);
        when.Controls.Add(_whenGame);
        gen.Controls.Add(when);
        _whenAlways.CheckedChanged += (_, _) => { if (!_syncing && _whenAlways.Checked) { _cfg().AlwaysLimit = true; _save(); } };
        _whenGame.CheckedChanged   += (_, _) => { if (!_syncing && _whenGame.Checked)   { _cfg().AlwaysLimit = false; _save(); } };

        gen.Controls.Add(Heading("Game detection"));
        Add(gen, "Auto-detect fullscreen games",                             c => c.AutoDetectFullscreen,(c, v) => c.AutoDetectFullscreen = v,false);
        Add(gen, "Keep limits while the game runs (even after Alt+Tab)",     c => c.KeepLimitsWhileGameRuns, (c, v) => c.KeepLimitsWhileGameRuns = v, false);
        var delayRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        delayRow.Controls.Add(new Label { Text = "Game must be in front for (s) before limiting:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _delay.ValueChanged += (_, _) => { if (_syncing) return; _cfg().DelaySeconds = (int)_delay.Value; _save(); };
        delayRow.Controls.Add(_delay);
        gen.Controls.Add(delayRow);
        AddNum(gen, "Delay before the limit returns after Alt+Tab (s)", c => (decimal)c.RelimitDelaySeconds, (c, v) => c.RelimitDelaySeconds = (double)v, 0, 60, 1, false,
            "RelimitDelaySeconds - avoids flicker when you switch windows.");

        // ================= 4. Games & lists =================
        var lst = Page("Games & lists");
        lst.Controls.Add(Heading("Games (always treated as a game)"));
        lst.Controls.Add(Note("One name per line, no .exe."));
        lst.Controls.Add(_games);
        lst.Controls.Add(Heading("Fullscreen apps that are NOT games"));
        lst.Controls.Add(Note("Browsers, video players... one name per line."));
        lst.Controls.Add(_fsIgnore);
        var saveLists = Btn("Save lists", () =>
        {
            _cfg().Games = Lines(_games.Text);
            _cfg().FullscreenIgnore = Lines(_fsIgnore.Text);
            _save();
        });
        saveLists.Margin = new Padding(3, 8, 3, 3);
        lst.Controls.Add(saveLists);
        lst.Controls.Add(Heading("Never touched (built in)"));
        lst.Controls.Add(Note("System processes, security / anti-cheat services, the shell and recording tools are always protected. " +
                              "Add your own with Exceptions (Main tab or Advanced > Apps)."));
        lst.Controls.Add(Btn("Show protected apps...", ShowProtected));

        // ================= 5. Tools =================
        var tl = Page("Tools");
        tl.Controls.Add(Heading("Windows tools"));
        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Ui.ContentWidth, 0) };
        tools.Controls.Add(Btn("Toggle MemCompression", ToggleMemCompression));
        tools.Controls.Add(Btn("Toggle PageCombining", () => RunTool(() => SystemTools.Ps(SystemTools.MmTogglePc))));
        tools.Controls.Add(Btn("iGPU route...", RouteIgpu));
        tl.Controls.Add(tools);
        tl.Controls.Add(Heading("Maintenance"));
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Ui.ContentWidth, 0) };
        buttons.Controls.Add(Btn("Open config file", openConfig));
        buttons.Controls.Add(Btn("Reload config", reload));
        buttons.Controls.Add(Btn("Forget learned limits", forgetLearned));
        buttons.Controls.Add(Btn("Open log file", openLog));
        tl.Controls.Add(buttons);

        Sync();
    }

    /// <summary>Runs a tool off the UI thread, then shows and logs its result.</summary>
    void ToggleMemCompression()
    {
        // Show the current state first and ask: turning compression OFF raises RAM pressure (the opposite of this app's goal).
        Task.Run(() => SystemTools.Ps("(Get-MMAgent).MemoryCompression")).ContinueWith(t =>
        {
            if (IsDisposed) return;
            string cur = t.IsFaulted ? "" : t.Result.Trim();
            bool? on = cur.Equals("True", StringComparison.OrdinalIgnoreCase) ? true : cur.Equals("False", StringComparison.OrdinalIgnoreCase) ? false : null;
            string state = on == true ? "ON" : on == false ? "OFF" : "unknown";
            string action = on == true ? "turn it OFF (uses MORE RAM)" : "turn it ON";
            BeginInvoke(() =>
            {
                var r = MessageBox.Show(this, $"Memory compression is currently {state}.\n\nDo you want to {action}?\nA reboot is required for the change to apply.",
                    "Toggle MemCompression", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r == DialogResult.Yes) RunTool(() => SystemTools.Ps(SystemTools.MmToggleMc));
            });
        });
    }

    void RunTool(Func<string> work)
    {
        Task.Run(work).ContinueWith(t =>
        {
            string r = t.IsFaulted ? "Error: " + t.Exception?.GetBaseException().Message : t.Result;
            Logger.Write("Tool: " + r.Replace("\r", " ").Replace("\n", " | "));
            if (!IsDisposed) BeginInvoke(() => MessageBox.Show(this, r, "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information));
        });
    }

    void RouteIgpu()
    {
        using var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Pick the app to route" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var r = MessageBox.Show(this, "Yes = route to the power-saving GPU (iGPU)\nNo = remove the override", "iGPU route", MessageBoxButtons.YesNoCancel);
        if (r == DialogResult.Cancel) return;
        string res = SystemTools.GpuPref(dlg.FileName, r == DialogResult.Yes ? 1 : 0);
        Logger.Write("Tool: " + res);
        MessageBox.Show(this, res, "GameRamCap");
    }

    static Label Heading(string text) => Ui.Heading(text);

    static Button Btn(string text, Action click) => Ui.Btn(text, click);

    void Add(FlowLayoutPanel flow, string text, Func<Config, bool> get, Action<Config, bool> set, bool smartOnly)
    {
        var box = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
        box.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            set(_cfg(), box.Checked);
            _save();
        };
        _boxes.Add((box, get, smartOnly));
        flow.Controls.Add(box);
    }

    static List<string> Lines(string text) =>
        text.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x)
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    void ShowProtected()
    {
        var names = string.Join(Environment.NewLine, Engine.Protected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        using var f = new Form { Text = "Protected apps (never limited)", ClientSize = new Size(360, 460), StartPosition = FormStartPosition.CenterParent };
        f.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Text = names });
        f.ShowDialog(this);
    }

    void AddNum(FlowLayoutPanel flow, string text, Func<Config, decimal> get, Action<Config, decimal> set,
                decimal min, decimal max, int decimals, bool customOnly, string tip)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var lbl = new Label { Text = text, AutoSize = false, Width = 380, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 3, 3, 3) };
        var num = new NumericUpDown
        {
            Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals > 0 ? 0.5m : 1m, Width = 70,
        };
        _tip.SetToolTip(lbl, tip);
        _tip.SetToolTip(num, tip);
        num.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            set(_cfg(), num.Value);
            _save();
        };
        row.Controls.Add(lbl);
        row.Controls.Add(num);
        flow.Controls.Add(row);
        _nums.Add((num, get, customOnly));
    }

    void SetMode(string mode)
    {
        _cfg().Mode = mode;
        Logger.Write($"Mode set to {mode}.");
        _save();
    }

    /// <summary>Re-reads the config into the controls (after a reload, a mode change, or a tray action).</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            var c = _cfg();
            string cur = Presets.Resolve(c).Name;
            int idx = Array.FindIndex(Presets.AllNames, n => string.Equals(n, cur, StringComparison.OrdinalIgnoreCase));
            _level.SelectedIndex = idx;
            _smart.Checked = c.IsSmart();
            _hard.Checked = !c.IsSmart();
            _smartPanel.Visible = c.IsSmart();
            _hardPanel.Visible = !c.IsSmart();
            _scopeProc.Checked = !c.HardPerApp();
            _scopeApp.Checked = c.HardPerApp();
            _defLabel.Text = Ui.LimitLabel(c.HardPerApp());
            _customPanel.Visible = c.IsSmart() && Presets.IsCustom(c);
            _defaultsPanel.Visible = !c.IsSmart() || Presets.IsCustom(c);
            _defCeil.Value = Math.Clamp(c.DefaultCeilingMB, (int)_defCeil.Minimum, (int)_defCeil.Maximum);
            _defFixed.Checked = !c.DefaultAdaptive;
            _delay.Value = Math.Clamp(c.DelaySeconds, 0, 120);
            _delay.Enabled = !c.AlwaysLimit;
            _whenAlways.Checked = c.AlwaysLimit;
            _whenGame.Checked = !c.AlwaysLimit;
            foreach (var (box, get, smartOnly) in _boxes)
            {
                box.Checked = get(c);
                string? why = null;
                if (smartOnly && !c.IsSmart()) why = "Only works in Smart mode (Advanced > Mode).";
                else if (box.Text.StartsWith("Auto-squeeze") && !c.HoldBackground) why = "Needs \"Hold background apps\" turned on.";
                else if (box.Text.StartsWith("Also lower thread") && !c.LowerMemoryPriority) why = "Needs \"Low memory priority for background apps\" turned on.";
                box.Enabled = why == null;
                _tip.SetToolTip(box, why ?? "");
            }
            foreach (var (num, get, customOnly) in _nums)
            {
                num.Value = Math.Clamp(get(c), num.Minimum, num.Maximum);
                num.Enabled = !customOnly || !c.IsSmart() || Presets.IsCustom(c);
            }
            _games.Text = string.Join(Environment.NewLine, c.Games);
            _fsIgnore.Text = string.Join(Environment.NewLine, c.FullscreenIgnore);
        }
        finally { _syncing = false; }
    }
}
