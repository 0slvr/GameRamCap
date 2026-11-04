namespace GameRamCap;

/// <summary>One window, one row of tabs: Main (status and the few switches that matter), Apps, Settings
/// (one scrolling page of cards). The Intensity levels table opens on demand from the Main tab.
/// Closing the window only hides it; the tray icon keeps running.</summary>
internal sealed class MainWindow : Form
{
    // Logical places other code can ask for.
    public const int TabHome = 0, TabApps = 1, TabSettings = 2;

    readonly ModernTabs _tabs = new(false) { Dock = DockStyle.Fill };
    readonly TabPage _homePage = new("Main"), _appsPage = new("Apps");
    readonly TabPage _settingsPage, _listsPage;
    readonly TargetsForm _targets = new();
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
        _settingsPage = settings.Pages[0];   // one scrolling page: Mode, General, Games & lists

        _listsPage = settings.Pages[1];      // Games & lists: its own tab
        _tabs.TabPages.AddRange(new[] { _homePage, _appsPage, _listsPage, _settingsPage });
        Controls.Add(_tabs);
        Ui.Polish(this);
        _tabs.SelectedIndexChanged += (_, _) => RefreshCustomState();
        RefreshCustomState();
    }

    /// <summary>Apps numbers only matter in Hard mode / own-limit rows.</summary>
    public void RefreshCustomState() => _targets.RefreshCustomState();

    public void SelectTab(int place)
    {
        _tabs.SelectedTab = place switch
        {
            TabHome => _homePage,
            TabApps => _appsPage,
            _ => _settingsPage,
        };
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
