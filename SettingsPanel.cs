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
    readonly List<(NumericUpDown Box, Func<Config, decimal> Get, bool CustomOnly)> _nums = new();
    FlowLayoutPanel _smartPanel = null!;
    /// <summary>The Smart-mode fine-tuning options. The Main tab shows them inside the Smart mode card.</summary>
    public Control SmartDetails => _smartPanel;
    /// <summary>The Settings tab: one page of cards (game detection, faster wake-up, maintenance).</summary>
    public readonly List<TabPage> Pages = new();
    bool _syncing;

    public SettingsPanel(Func<Config> cfg, Action save, Action<string> setProfile, Action openConfig, Action reload, Action openLog, Action resetDefaults)
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
        Add(sm, "Measure what each background process really needs",         c => c.MeasureNeed,         (c, v) => c.MeasureNeed = v,         true);

        AddNumRow(sm, "Big apps:", new (string, Func<Config, decimal>, Action<Config, decimal>, decimal, decimal, string)[]
        {
            ("never squeeze above (MB, 0 = off)", c => c.BigProcMB, (c, v) => c.BigProcMB = (int)v, 0, 8192,
                "BigProcMB - protects huge processes (for example Spotify at 1.3 GB) from being squeezed to a few MB."),
            ("keep at least (%)", c => c.BigProcKeepPercent, (c, v) => c.BigProcKeepPercent = (int)v, 0, 100,
                "BigProcKeepPercent - for example 20 keeps a 1.3 GB process at 260 MB or more."),
            ("may grow back to (%, 0 = no limit)", c => c.BigProcMaxGrowPercent, (c, v) => c.BigProcMaxGrowPercent = (int)v, 0, 1000,
                "BigProcMaxGrowPercent - 200 lets a 192 MB ceiling grow to 384 MB at most when the app page-faults. 100 = fixed ceiling."),
        });

        Add(sm, "Give room back only when the disk is really read (ignore soft faults)", c => c.SmartFaultCheck, (c, v) => c.SmartFaultCheck = v, true);
        AddNum(sm, "   ...disk page reads per second that count as real", c => c.HardFaultPagesPerSec, (c, v) => c.HardFaultPagesPerSec = (int)v, 1, 5000, 0, false,
            "HardFaultPagesPerSec - system-wide Pages Input/sec. Below this, an app's page faults are served from RAM and are ignored.");

        sm.Controls.Add(Heading("Sound protection"));
        Add(sm, "Protect only the process that plays sound / uses the mic",  c => c.AudioProtect,        (c, v) => c.AudioProtect = v,        true);
        AddNum(sm, "Keep protecting after the sound stops (s)", c => (decimal)c.AudioProtectGraceSeconds, (c, v) => c.AudioProtectGraceSeconds = (double)v, 0, 120, 1, false,
            "AudioProtectGraceSeconds");

        // ================= 2. General (both modes) =================
        var look = Page("Appearance");
        look.Controls.Add(Note("Accent colour for buttons, tabs and switches."));
        var swatches = new ModernSwatches { Value = Ui.AccentName, Margin = new Padding(3, 2, 3, 4) };
        swatches.ValueChanged += (_, _) => Ui.SetAccent(swatches.Value);
        look.Controls.Add(swatches);
        look.Controls.Add(Note("The Apps list: use its View button to choose the columns and the row height. Right-click its header row for update speed, always on top and the other window options."));

        var gen = Page("Game detection");
        Add(gen, "Clear the standby list when free RAM is low while a game runs", c => c.StandbyClean, (c, v) => c.StandbyClean = v, false);
        AddNum(gen, "   ...only when free RAM is below (MB)", c => c.StandbyFreeBelowMB, (c, v) => c.StandbyFreeBelowMB = (int)v, 128, 32768, 0, false,
            "StandbyFreeBelowMB - free + zeroed memory, not counting the standby cache.");
        AddNum(gen, "   ...at most once every (seconds)", c => c.StandbyMinIntervalSeconds, (c, v) => c.StandbyMinIntervalSeconds = (int)v, 10, 3600, 0, false,
            "StandbyMinIntervalSeconds");
        AddNum(gen, "Delay before the limit returns after Alt+Tab (s)", c => (decimal)c.RelimitDelaySeconds, (c, v) => c.RelimitDelaySeconds = (double)v, 0, 60, 1, false,
            "RelimitDelaySeconds - avoids flicker when you switch windows.");

        // ---- Faster wake-up (Win+V and apps) ----
        var wake = Page("Faster wake-up");
        wake.Controls.Add(Note("An app that was squeezed opens a bit slower. This feature reloads it in the background just before you switch to it."));
        wake.Controls.Add(Note("Turn it ON if you have an SSD and switch between apps often.\nTurn it OFF if you have a slow hard drive (HDD), or you want the least extra disk activity."));
        Add(wake, "Faster wake-up: reload apps just before I switch to them (Win+V, Start / Search, Alt+Tab, mouse click)",
            c => c.WakeWinVOnKey && c.WakeStartOnKey && c.WakeAltTabOnKey && c.WakeClickOnMouse && c.PrefetchApps,
            (c, v) => { c.WakeWinVOnKey = v; c.WakeStartOnKey = v; c.WakeAltTabOnKey = v; c.WakeClickOnMouse = v; c.PrefetchApps = v; }, false);
        AddNum(wake, "Max MB reloaded per app (lower = lighter on disk)",                           c => c.PrefetchMaxMB, (c, v) => c.PrefetchMaxMB = (int)v, 16, 1024, 0, false,
            "PrefetchMaxMB - lower is lighter on disk and RAM; higher wakes big apps more fully.");
        AddNum(wake, "Skip it when free RAM is below (%)  (protects games)",            c => c.PrefetchMinFreePercent, (c, v) => c.PrefetchMinFreePercent = (int)v, 0, 50, 0, false,
            "PrefetchMinFreePercent - protects a full machine (for example while a game runs).");

        // ================= 4. Maintenance =================
        var tl = Page("Maintenance");
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Ui.ContentWidth, 0) };
        buttons.Controls.Add(Btn("Open config file", openConfig));
        buttons.Controls.Add(Btn("Reload config", reload));
        buttons.Controls.Add(Btn("Open log file", openLog));
        buttons.Controls.Add(Btn("Reset to defaults", resetDefaults));
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

    /// <summary>One line holding several related number boxes (label, then each box with its own small caption).</summary>
    void AddNumRow(FlowLayoutPanel flow, string title,
                   (string Text, Func<Config, decimal> Get, Action<Config, decimal> Set, decimal Min, decimal Max, string Tip)[] items)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(Ui.ContentWidth, 0) };
        row.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(3, 6, 6, 3) });
        foreach (var it in items)
        {
            var set = it.Set;
            var lbl = new Label { Text = it.Text, AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
            var num = new ModernNumeric { Minimum = it.Min, Maximum = it.Max, DecimalPlaces = 0, Increment = 1m, Width = 70 };
            _tip.SetToolTip(lbl, it.Tip);
            _tip.SetToolTip(num, it.Tip);
            num.ValueChanged += (_, _) =>
            {
                if (_syncing) return;
                set(_cfg(), num.Value);
                _save();
            };
            row.Controls.Add(lbl);
            row.Controls.Add(num);
            _nums.Add((num, it.Get, false));
        }
        flow.Controls.Add(row);
    }

    /// <summary>Re-reads the config into the controls (after a reload, a mode change, or a tray action).</summary>
    public void Sync()
    {
        _syncing = true;
        try
        {
            var c = _cfg();
            foreach (var (box, get, smartOnly) in _boxes)
            {
                box.Checked = get(c);
                string? why = null;
                if (smartOnly && !c.IsSmart()) why = "Only works in Smart mode (the Settings tab).";
                box.Enabled = why == null;
                _tip.SetToolTip(box, why ?? "");
            }
            foreach (var (num, get, customOnly) in _nums)
            {
                num.Value = Math.Clamp(get(c), num.Minimum, num.Maximum);
                num.Enabled = !customOnly || !c.IsSmart() || Presets.IsCustom(c);
            }
        }
        finally { _syncing = false; }
    }
}
