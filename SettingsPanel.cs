namespace GameRamCap;

/// <summary>The "Settings" tab: everything that used to live in the tray's Mode / Options submenus,
/// plus the maintenance actions (config file, reload, learned limits, log). Behaviour is unchanged;
/// only the place moved so the tray menu stays short.</summary>
internal sealed class SettingsPanel : UserControl
{
    readonly Func<Config> _cfg;
    readonly Action _save;
    readonly List<(CheckBox Box, Func<Config, bool> Get, bool SmartOnly)> _boxes = new();
    readonly ToolTip _tip = new();
    readonly ModernNumeric _delay = new() { Minimum = 0, Maximum = 120, Width = 60 };
    readonly List<(NumericUpDown Box, Func<Config, decimal> Get, bool CustomOnly)> _nums = new();
    readonly ModernTextBox _games = new() { Multiline = true, Width = 300, Height = 90, ScrollBars = ScrollBars.Vertical };
    readonly ModernTextBox _fsIgnore = new() { Multiline = true, Width = 300, Height = 90, ScrollBars = ScrollBars.Vertical };
    FlowLayoutPanel _smartPanel = null!;
    /// <summary>The Smart-mode fine-tuning options. The Main tab shows them inside the Smart mode card.</summary>
    public Control SmartDetails => _smartPanel;
    /// <summary>The Settings tab: one page of cards (game detection, games &amp; lists, maintenance).</summary>
    public readonly List<TabPage> Pages = new();
    bool _syncing;

    public SettingsPanel(Func<Config> cfg, Action save, Action<string> setProfile, Action openConfig, Action reload, Action forgetLearned, Action openLog)
    {
        _cfg = cfg;
        _save = save;
        Dock = DockStyle.Fill;

        FlowLayoutPanel? shared = null;
        // A card on the shared "Settings" page, or (own = tab name) on a page of its own in the tab strip.
        FlowLayoutPanel Page(string title, string? own = null)
        {
            FlowLayoutPanel flow;
            if (own != null)
            {
                var page = new TabPage(own);
                Ui.Flat(page);
                flow = Ui.PageFlow();
                page.Controls.Add(flow);
                Pages.Add(page);
            }
            else
            {
                if (shared == null)
                {
                    var page = new TabPage("Settings");
                    Ui.Flat(page);
                    shared = Ui.PageFlow();
                    page.Controls.Add(shared);
                    Pages.Insert(0, page);
                }
                flow = shared;
            }
            var card = Ui.MakeCard();
            card.Controls.Add(new Label { Text = title, AutoSize = true, UseMnemonic = false, Font = Ui.Hero, Margin = new Padding(3, 0, 3, 8) });
            flow.Controls.Add(card);
            return card;
        }
        static Label Note(string text) => Ui.Note(text);

        FlowLayoutPanel Group()
        {
            return new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        }

        // ---- Smart settings ----
        _smartPanel = Group();
        var sm = _smartPanel;
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

        // ================= 2. General (both modes) =================
        var gen = Page("Game detection");
        Add(gen, "Auto-detect fullscreen games",                             c => c.AutoDetectFullscreen,(c, v) => c.AutoDetectFullscreen = v,false);
        Add(gen, "Keep limits while the game runs (even after Alt+Tab)",     c => c.KeepLimitsWhileGameRuns, (c, v) => c.KeepLimitsWhileGameRuns = v, false);
        Add(gen, "Pause Windows Update while a game runs (restored after)", c => c.PauseWindowsUpdate, (c, v) => c.PauseWindowsUpdate = v, false);
        Add(gen, "Clear the standby list when free RAM is low while a game runs", c => c.StandbyClean, (c, v) => c.StandbyClean = v, false);
        AddNum(gen, "   ...only when free RAM is below (MB)", c => c.StandbyFreeBelowMB, (c, v) => c.StandbyFreeBelowMB = (int)v, 128, 32768, 0, false,
            "StandbyFreeBelowMB - free + zeroed memory, not counting the standby cache.");
        AddNum(gen, "   ...at most once every (seconds)", c => c.StandbyMinIntervalSeconds, (c, v) => c.StandbyMinIntervalSeconds = (int)v, 10, 3600, 0, false,
            "StandbyMinIntervalSeconds");
        var delayRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        delayRow.Controls.Add(new Label { Text = "Game must be in front for (s) before limiting:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _delay.ValueChanged += (_, _) => { if (_syncing) return; _cfg().DelaySeconds = (int)_delay.Value; _save(); };
        delayRow.Controls.Add(_delay);
        gen.Controls.Add(delayRow);
        AddNum(gen, "Delay before the limit returns after Alt+Tab (s)", c => (decimal)c.RelimitDelaySeconds, (c, v) => c.RelimitDelaySeconds = (double)v, 0, 60, 1, false,
            "RelimitDelaySeconds - avoids flicker when you switch windows.");

        // ================= 4. Games & lists =================
        var lst = Page("Games & lists", "Games and lists");
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
                              "Add your own with Exceptions (Main tab or Apps tab)."));
        lst.Controls.Add(Btn("Show protected apps...", ShowProtected));

        // ================= 5. Maintenance =================
        var tl = Page("Maintenance");
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Ui.ContentWidth, 0) };
        buttons.Controls.Add(Btn("Open config file", openConfig));
        buttons.Controls.Add(Btn("Reload config", reload));
        buttons.Controls.Add(Btn("Forget learned limits", forgetLearned));
        buttons.Controls.Add(Btn("Open log file", openLog));
        tl.Controls.Add(buttons);

        Sync();
    }

    static Label Heading(string text) => Ui.Heading(text);

    static Button Btn(string text, Action click) => Ui.Btn(text, click);

    void Add(FlowLayoutPanel flow, string text, Func<Config, bool> get, Action<Config, bool> set, bool smartOnly)
    {
        var box = new ModernCheck { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
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
        f.Controls.Add(new ModernTextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Text = names });
        f.Font = Ui.Base;
        Ui.Polish(f);
        f.ShowDialog(this);
    }

    void AddNum(FlowLayoutPanel flow, string text, Func<Config, decimal> get, Action<Config, decimal> set,
                decimal min, decimal max, int decimals, bool customOnly, string tip)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var lbl = new Label { Text = text, AutoSize = false, Width = 380, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 3, 3, 3) };
        var num = new ModernNumeric
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

    /// <summary>Re-reads the config into the controls (after a reload, a mode change, or a tray action).</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            var c = _cfg();
            _delay.Value = Math.Clamp(c.DelaySeconds, 0, 120);
            _delay.Enabled = !c.AlwaysLimit;
            foreach (var (box, get, smartOnly) in _boxes)
            {
                box.Checked = get(c);
                string? why = null;
                if (smartOnly && !c.IsSmart()) why = "Only works in Smart mode (the Settings tab).";
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
