namespace GameRamCap;

/// <summary>One window. Two tabs: Main (status and the few switches that matter) and Advanced
/// (Apps, Mode, General, Games &amp; lists, Tools, Intensity levels in one row of sub-tabs).
/// Closing the window only hides it; the tray icon keeps running.</summary>
internal sealed class MainWindow : Form
{
    // Logical places other code can ask for.
    public const int TabHome = 0, TabApps = 1, TabSettings = 2, TabAdvanced = 3;   // TabAdvanced = Intensity levels
    public const int AdvLevels = 0;

    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly TabControl _adv = new() { Dock = DockStyle.Fill };
    readonly TabPage _homePage = new("Main"), _advancedPage = new("Advanced");
    readonly TabPage _appsPage = new("Apps"), _levelsPage = new("Intensity levels");
    readonly TabPage _firstSettingsPage;
    readonly TargetsForm _targets = new();
    readonly IntensityForm _levels = new();
    bool _exiting;

    public MainWindow(Control home, SettingsPanel settings)
    {
        Text = "GameRamCap v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");
        Font = Ui.Base;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 700);
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _homePage.Controls.Add(home);

        Embed(_appsPage, _targets);
        Embed(_levelsPage, _levels);
        _adv.TabPages.Add(_appsPage);
        foreach (var p in settings.Pages) _adv.TabPages.Add(p);   // Mode, General, Games & lists, Tools
        _firstSettingsPage = settings.Pages[0];
        _adv.TabPages.Add(_levelsPage);
        _advancedPage.Controls.Add(_adv);

        _tabs.TabPages.AddRange(new[] { _homePage, _advancedPage });
        Controls.Add(_tabs);
        Ui.Polish(this);
        _tabs.SelectedIndexChanged += (_, _) => RefreshCustomState();
        _adv.SelectedIndexChanged += (_, _) => RefreshCustomState();
        RefreshCustomState();
    }

    /// <summary>Apps numbers only matter with Custom / Hard; the Intensity levels sub-tab only exists in Smart mode.</summary>
    public void RefreshCustomState()
    {
        _targets.RefreshCustomState();
        bool smart = ConfigStore.Load().IsSmart();
        bool shown = _adv.TabPages.Contains(_levelsPage);
        if (smart && !shown) _adv.TabPages.Add(_levelsPage);
        else if (!smart && shown) _adv.TabPages.Remove(_levelsPage);
    }

    public void SelectTab(int place, int inner = 0)
    {
        if (place == TabHome) { _tabs.SelectedTab = _homePage; return; }
        _tabs.SelectedTab = _advancedPage;
        var target = place switch
        {
            TabApps => _appsPage,
            TabAdvanced => _adv.TabPages.Contains(_levelsPage) ? _levelsPage : _appsPage,
            _ => _firstSettingsPage,
        };
        _adv.SelectedTab = target;
    }

    /// <summary>Embeds a form as the content of a tab. Its own Close button just returns to the Main tab.</summary>
    void Embed(TabPage page, Form f)
    {
        f.TopLevel = false;
        f.FormBorderStyle = FormBorderStyle.None;
        f.MinimumSize = Size.Empty;
        f.Dock = DockStyle.Fill;
        f.FormClosing += (_, e) => { if (!_exiting) { e.Cancel = true; _tabs.SelectedTab = _homePage; } };
        page.Controls.Add(f);
        f.Show();
    }

    public void Shutdown()
    {
        _exiting = true;
        Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }
}
