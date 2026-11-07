using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameRamCap;

/// <summary>Lets the user pick which running apps get a RAM ceiling and edit each ceiling.</summary>
internal sealed partial class TargetsForm : Form
{
    readonly Config _cfg = ConfigStore.Load();
    readonly ModernTextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Search running apps..." };
    readonly ListView _running = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, Dock = DockStyle.Fill,
        HeaderStyle = ColumnHeaderStyle.None,   // the header is our own control, ModernListHeader
    };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ScrollBars = ScrollBars.Vertical,
    };
    readonly ModernTextBox _manual = new() { Width = 150, PlaceholderText = "app name (no .exe)", Margin = new Padding(3, 7, 3, 3) };
    ModernListHeader _header = null!;
    readonly ConcurrentDictionary<string, Bitmap> _icons = new(StringComparer.OrdinalIgnoreCase);   // filled on the scan thread
    readonly ModernCheck _everything = new() { Text = "Limit all apps, not just my list", AutoSize = true, Margin = new Padding(3, 7, 20, 3) };
    bool _syncing;
    bool _dirty, _loadingGrid;                                   // unsaved edits in the limits grid
    string _loadedSig = "";                                       // Targets as last loaded into the grid (to notice outside changes)
    List<Target> _hiddenTargets = new();                          // protected apps' targets: not shown, but never deleted by Save
    Config? _lastCfg; DateTime _lastStamp;                        // RefreshCustomState re-reads the file only when it changed
    bool _closePdh, _pdhClosed;
    string _lastScanErr = "";
    readonly System.Windows.Forms.Timer _relayout = new() { Interval = 40 };
    Dictionary<int, (int Parent, string Name)> _map = new();      // process tree, read on the scan thread
    double? _diskPct;

    // ---- live columns (CPU %, I/O) and sorting of the running list ----
    sealed class Grp { public long Ws; public int N; public bool Denied; public long CpuD, IoD; public double Gpu, Net; public List<(int Pid, long Ws)> Procs = new(); }
    sealed record Row(string Name, long Ws, int N, bool Denied, double? Cpu, double? Io, double? Gpu, double? Net, List<(int Pid, long Ws)> Procs);
    // apps whose processes are shown under them (click the arrow, or Right / Left arrow keys)
    readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<int, (double cpu, double io)> _procRate = new();   // replaced as a whole by the scan thread, never edited in place
    Dictionary<int, double>? _netRate;   // pid -> network bytes/s (null while the trace warms up or is unavailable)
    sealed record ChildTag(string App, int Pid);
    static readonly string[] ColNames = { "Running app", "Memory", "CPU", "GPU", "Disk", "Network", "Proc.", "Status" };
    List<Row> _rows = new();
    Dictionary<string, (double cpu, double io)> _rate = new(StringComparer.OrdinalIgnoreCase);   // replaced as a whole by the scan thread
    Dictionary<int, (long cpu, long io)> _prevPid = new();
    DateTime _prevAt = DateTime.MinValue;
    int _sortCol = 1;          // default: RAM, biggest first
    bool _sortAsc = false;
    bool _menuOpen;
    Action? _showEmpty;   // shows / hides the "no apps yet" hint over the right-hand grid
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    readonly List<ProcessRule> _rules;
    readonly Label _customNote = new() { AutoSize = true, Tag = "muted", Margin = new Padding(3, 3, 3, 6) };

    public TargetsForm()
    {
        Icon = AppIcon.Window;
        Text = "GameRamCap - apps, limits and exceptions";
        ClientSize = new Size(1000, 560);
        MinimumSize = new Size(900, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Base;

        _rules = _cfg.Rules ?? new List<ProcessRule>();
        _running.Columns.Add(ColNames[0], 170);
        _running.Columns.Add(ColNames[1], 72);
        _running.Columns.Add(ColNames[2], 56);
        _running.Columns.Add(ColNames[3], 56);
        _running.Columns.Add(ColNames[4], 76);
        _running.Columns.Add(ColNames[5], 76);
        _running.Columns.Add(ColNames[6], 52);
        _running.Columns.Add(ColNames[7], 110);
        _running.Columns.Add("", 10).Tag = "filler";   // takes the leftover width so no real column has to stretch; rows still paint edge to edge
        for (int i = 1; i <= 6; i++) _running.Columns[i].TextAlign = HorizontalAlignment.Right;
        // a 1 x 28 px image list makes every row 28 px tall: more air between the lines
        _running.SmallImageList = new ImageList { ImageSize = new Size(1, Ui.S(this, 28)) };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Exe", HeaderText = "App", ReadOnly = true, FillWeight = 130, MinimumWidth = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ceiling", HeaderText = "Limit", ToolTipText = "RAM limit in MB", FillWeight = 70, MinimumWidth = 56 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Min", HeaderText = "Min", ToolTipText = "Lowest limit in MB", FillWeight = 60, MinimumWidth = 50 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Max", HeaderText = "Max", ToolTipText = "Highest limit in MB", FillWeight = 60, MinimumWidth = 50 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Adaptive", HeaderText = "Adaptive", FillWeight = 70, MinimumWidth = 74 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Own", HeaderText = "Own limit", ToolTipText = "Use this app's numbers instead of the intensity level", FillWeight = 70, MinimumWidth = 78 });

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Budget", HeaderText = "Budget", ToolTipText = "In MB. Smart mode: total RAM of all this app's processes while in the background. 0 = use Max MB", FillWeight = 65, MinimumWidth = 64 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Also", HeaderText = "Focus also", ToolTipText = "Other process names that count as this app being focused (comma separated)", FillWeight = 90, MinimumWidth = 70 });

        // Grid: one line headers, numbers and tick boxes centred
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (col.Name == "Exe" || col.Name == "Also") continue;
            col.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }
        var empty = new Label
        {
            Text = "No apps here yet.\nDouble-click an app on the left to give it its own limit.",
            Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Tag = "muted", Padding = new Padding(0, 34, 0, 0),
        };
        _grid.Controls.Add(empty);
        void ShowEmpty() => empty.Visible = _grid.Rows.Count == 0;
        _grid.RowsAdded += (_, _) => ShowEmpty();
        _grid.RowsRemoved += (_, _) => ShowEmpty();
        _showEmpty = ShowEmpty;

        // Left: running apps
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0, 0, 14, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(Ui.Heading("Running apps (double-click to add)"), 0, 0);
        // search box with Refresh next to it, so the action buttons below fit on one row
        var refresh = new ModernButton { Text = "Refresh", AutoSize = true, Margin = new Padding(6, 3, 3, 3), Glyph = "\uE72C" };
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.Controls.Add(_search, 0, 0);
        searchRow.Controls.Add(refresh, 1, 0);
        left.Controls.Add(searchRow, 0, 1);
        _header = new ModernListHeader(_running) { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        left.Controls.Add(_header, 0, 2);
        left.Controls.Add(_running, 0, 3);
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var add = new ModernButton { Text = "Add  →", AutoSize = true, Tag = "primary" };
        leftButtons.Controls.Add(add);
        var startTask = new ModernButton { Text = "Start task...", AutoSize = true, Glyph = "\uE768", Tag = "ghost" };
        leftButtons.Controls.Add(startTask);
        var history = new ModernButton { Text = "History...", AutoSize = true, Glyph = "\uE81C", Tag = "ghost" };
        leftButtons.Controls.Add(history);
        left.Controls.Add(leftButtons, 0, 4);

        // Right: chosen apps
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(14, 0, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(Ui.Heading("Apps with their own limit (MB, edit the numbers)"), 0, 0);
        right.Controls.Add(_grid, 0, 1);
        var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var remove = new ModernButton { Text = "Remove selected", AutoSize = true, Glyph = "\uE74D", Tag = "ghost" };
        var addManual = new ModernButton { Text = "Add by name", AutoSize = true };
        rightButtons.Controls.Add(remove);
        rightButtons.Controls.Add(_manual);
        rightButtons.Controls.Add(addManual);
        right.Controls.Add(rightButtons, 0, 2);

        // Bottom: note on top; below it the app-wide option + Exceptions on the left, Save / Close on the right
        var defaults = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        defaults.Controls.Add(_customNote);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        var exceptions = new ModernButton { Text = "Exceptions...", AutoSize = true };
        options.Controls.Add(_everything);
        options.Controls.Add(exceptions);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        var save = new ModernButton { Text = "Save", AutoSize = true, Tag = "primary" };
        new ToolTip().SetToolTip(save, "Save the table (Ctrl+S)");
        var close = new ModernButton { Text = "Close", AutoSize = true };
        buttons.Controls.Add(close);
        buttons.Controls.Add(save);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 10, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(defaults, 0, 0);
        bottom.SetColumnSpan(defaults, 2);
        bottom.Controls.Add(options, 0, 1);
        bottom.Controls.Add(buttons, 1, 1);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = Ui.PagePadding };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // a draggable divider: make the running list wider or the limits table wider
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 14,
            FixedPanel = FixedPanel.None, Cursor = Cursors.VSplit, BackColor = Ui.Bg,
        };
        left.Margin = Padding.Empty; right.Margin = Padding.Empty;
        left.Cursor = Cursors.Default; right.Cursor = Cursors.Default;
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        bool splitSet = false;
        split.SizeChanged += (_, _) =>
        {
            if (splitSet || split.Width < 800) return;
            splitSet = true;
            try
            {
                // minimum sizes are set only now: while the control is still tiny they would throw
                split.Panel1MinSize = 420;
                split.Panel2MinSize = 300;
                split.SplitterDistance = (int)(split.Width * 0.55);
            }
            catch { }
        };
        root.Controls.Add(split, 0, 0);
        root.SetColumnSpan(split, 2);
        root.Controls.Add(bottom, 0, 1);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);
        Ui.Polish(this);
        split.BackColor = Ui.Bg;
        empty.BackColor = _grid.BackgroundColor;   // Polish gave it the grid's own (light) back colour
        ApplyColumns();
        SetRowHeight(UiPrefs.Current.RowHeight);
        _running.Tag = "fluid";
        // while the window is being dragged, lay the columns out once things settle (every 40 ms at most) instead of on every pixel
        _relayout.Tick += (_, _) => { _relayout.Stop(); LayoutColumns(); };
        _running.ClientSizeChanged += (_, _) => { LayoutColumns(); _relayout.Stop(); _relayout.Start(); };
        _running.HandleCreated += (_, _) => { AttachScrollHook(); ApplyColumnOrder(); LayoutColumns(); };
        AttachScrollHook();
        Shown += (_, _) => LayoutColumns();
        LayoutColumns();

        _grid.DataError += (_, e) => e.ThrowException = false;   // a bad cell value never throws; Save reports it
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, _) => { if (!_loadingGrid) _dirty = true; };
        _grid.RowsAdded += (_, _) => { if (!_loadingGrid) _dirty = true; };
        _grid.RowsRemoved += (_, _) => { if (!_loadingGrid) _dirty = true; };
        LoadGrid(_cfg);
        UpdateHeaders();
        RefreshRunning();
        RefreshCustomState();

        refresh.Click += (_, _) => RefreshRunning();
        _search.TextChanged += (_, _) => Render();   // typing only filters; no new process scan
        _header.ColumnClicked += col =>
        {
            if (col == ColNames.Length - 1) return;   // Status is not sortable
            if (_sortCol == col) _sortAsc = !_sortAsc;
            else { _sortCol = col; _sortAsc = col == 0; }
            UpdateHeaders();
            Render();
        };
        _header.AllowReorder = true;
        ApplyColumnOrder();
        _header.ColumnsReordered += () =>
        {
            UiPrefs.Current.ColumnOrder = _running.Columns.Cast<ColumnHeader>().Select(h => h.DisplayIndex).ToList();
            UiPrefs.Save();
        };
        _header.ColumnResizing += OnHeaderResizing;
        _header.ResizeDone += () => { UiPrefs.Current.ColumnPx = Widths().ToList(); UiPrefs.Save(); };
        _timer.Tick += (_, _) => { if (Visible && IsHandleCreated && !_menuOpen && ParentForm?.WindowState != FormWindowState.Minimized) RefreshRunning(); };
        ApplyUpdateSpeed();
        add.Click += (_, _) => AddSelected();
        startTask.Click += (_, _) => StartTask();
        history.Click += (_, _) => InfoForms.ShowHistory(this);
        _running.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) EndSelected();
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Left)
            {
                var names = SelectedNames();
                if (names.Count == 0) return;
                foreach (var n in names) { if (e.KeyCode == Keys.Right) _expanded.Add(n); else _expanded.Remove(n); }
                Render();
                e.Handled = true;
            }
        };
        _running.MouseDown += (_, e) =>   // click on the arrow at the start of a row
        {
            if (e.Button != MouseButtons.Left) return;
            var hit = _running.GetItemAt(e.X, e.Y);
            if (hit?.Tag is not string n) return;
            if (e.X > hit.Bounds.Left + 22) return;
            if (!_expanded.Remove(n)) _expanded.Add(n);
            Render();
        };
        // click on empty space: drop the selection (like Task Manager / Explorer)
        _running.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || (ModifierKeys & (Keys.Control | Keys.Shift)) != 0) return;
            if (_running.GetItemAt(e.X, e.Y) == null && _running.SelectedItems.Count > 0) _running.SelectedItems.Clear();
        };
        _grid.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || (ModifierKeys & (Keys.Control | Keys.Shift)) != 0) return;
            if (_grid.HitTest(e.X, e.Y).Type == DataGridViewHitTestType.None) { _grid.ClearSelection(); _grid.CurrentCell = null; }
        };
        void ClearBoth(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _running.SelectedItems.Clear();
            _grid.ClearSelection();
            try { _grid.CurrentCell = null; } catch { }
        }
        foreach (Control bg in new Control[] { root, left, right, bottom, split.Panel1, split.Panel2 }) bg.MouseDown += ClearBoth;
        var menu = new ContextMenuStrip();
        menu.Items.Add("End task", null, (_, _) => EndSelected());
        menu.Items.Add("End process tree", null, (_, _) => EndSelected(tree: true));
        menu.Items.Add("Restart", null, (_, _) => RestartSelected());
        menu.Items.Add("Open file location", null, (_, _) => { if (OpenLocation()) MinimizeIfWanted(); });
        menu.Items.Add("Check on VirusTotal (SHA-256, no upload)", null, (_, _) => CheckVirusTotal());
        menu.Items.Add("Start new task...", null, (_, _) => StartTask(minimizeAfter: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Protect (never limit)", null, (_, _) => SetProtection(true));
        menu.Items.Add("Unprotect", null, (_, _) => SetProtection(false));
        menu.Items.Add(new ToolStripSeparator());
        var nowMenu = new ToolStripMenuItem("Priority (this run only)");
        var keepMenu = new ToolStripMenuItem("Priority (always, remembered)");
        foreach (var pn in ProcessRules.PriorityNames)
        {
            var name = pn;
            nowMenu.DropDownItems.Add(ProcessRules.Label(pn), null, (_, _) => SetPriority(name, remember: false));
            keepMenu.DropDownItems.Add(ProcessRules.Label(pn), null, (_, _) => SetPriority(name, remember: true));
        }
        keepMenu.DropDownItems.Add(new ToolStripSeparator());
        keepMenu.DropDownItems.Add("Forget saved priority", null, (_, _) => ForgetRule(priority: true, cores: false));
        menu.Items.Add(nowMenu);
        menu.Items.Add(keepMenu);
        menu.Items.Add("CPU cores...", null, (_, _) => PickCores());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Freeze (suspend) now", null, (_, _) => FreezeSelected());
        menu.Items.Add("Resume", null, (_, _) => ResumeSelected());
        menu.Items.Add("Resume everything frozen", null, (_, _) => { Suspender.ResumeAll(); RefreshRunning(); });
        // Resource values (right-click the header row): show Memory / Disk / Network as a percent or as real values, like Task Manager
        var headerMenu = new ContextMenuStrip();
        headerMenu.Items.Add("Start new task...", null, (_, _) => StartTask(minimizeAfter: true));
        headerMenu.Items.Add(new ToolStripSeparator());
        var resMenu = new ToolStripMenuItem("Resource values");
        var resChecks = new List<Action>();
        void AddResource(string title, Func<bool> get, Action<bool> set)
        {
            var sub = new ToolStripMenuItem(title);
            var pct = new ToolStripMenuItem("Percents");
            var val = new ToolStripMenuItem("Values");
            pct.Click += (_, _) => { set(true); UiPrefs.Save(); Render(); };
            val.Click += (_, _) => { set(false); UiPrefs.Save(); Render(); };
            sub.DropDownItems.Add(pct);
            sub.DropDownItems.Add(val);
            resChecks.Add(() => { pct.Checked = get(); val.Checked = !get(); });
            resMenu.DropDownItems.Add(sub);
        }
        AddResource("Memory", () => UiPrefs.Current.MemoryAsPercent, v => UiPrefs.Current.MemoryAsPercent = v);
        AddResource("Disk", () => UiPrefs.Current.DiskAsPercent, v => UiPrefs.Current.DiskAsPercent = v);
        AddResource("Network", () => UiPrefs.Current.NetAsPercent, v => UiPrefs.Current.NetAsPercent = v);
        headerMenu.Items.Add(resMenu);
        headerMenu.Items.Add(new ToolStripSeparator());
        // window options (they used to live in Settings > Window)
        var speedMenu = new ToolStripMenuItem("Update speed");
        foreach (var sp in UiPrefs.Speeds)
        {
            var name = sp;
            var it = new ToolStripMenuItem(name);
            it.Click += (_, _) => { UiPrefs.Current.UpdateSpeed = name; UiPrefs.Save(); UiPrefs.NotifyWindowPrefs(); };
            speedMenu.DropDownItems.Add(it);
        }
        resChecks.Add(() =>
        {
            string cur = UiPrefs.Speeds.Contains(UiPrefs.Current.UpdateSpeed) ? UiPrefs.Current.UpdateSpeed : "Normal";
            foreach (ToolStripMenuItem it in speedMenu.DropDownItems) it.Checked = it.Text == cur;
        });
        headerMenu.Items.Add(speedMenu);
        void AddToggle(string text, Func<bool> get, Action<bool> set)
        {
            var it = new ToolStripMenuItem(text);
            it.Click += (_, _) => { set(!get()); UiPrefs.Save(); UiPrefs.NotifyWindowPrefs(); };
            resChecks.Add(() => it.Checked = get());
            headerMenu.Items.Add(it);
        }
        AddToggle("Always on top", () => UiPrefs.Current.AlwaysOnTop, v => UiPrefs.Current.AlwaysOnTop = v);
        var viewItems = new List<ToolStripItem>();
        headerMenu.Opening += (_, _) =>
        {
            // Columns / Row height (used to be the "View" button) - rebuilt each time so the check marks are current
            foreach (var old in viewItems) headerMenu.Items.Remove(old);
            viewItems.Clear();
            var fresh = new List<ToolStripItem>();
            BuildViewItems(fresh);
            fresh.Add(new ToolStripSeparator());
            for (int i = 0; i < fresh.Count; i++) { headerMenu.Items.Insert(i, fresh[i]); viewItems.Add(fresh[i]); }
            foreach (var upd in resChecks) upd();
            _menuOpen = true;
        };
        headerMenu.Closed += (_, _) => _menuOpen = false;
        _header.ContextMenuStrip = headerMenu;
        menu.Opening += (_, _) => _menuOpen = true;   // keep the list still while a menu is open
        menu.Closed += (_, _) => _menuOpen = false;
        _running.ContextMenuStrip = menu;
        _running.DoubleClick += (_, _) => AddSelected();
        remove.Click += (_, _) => RemoveSelected();
        addManual.Click += (_, _) => AddManual();
        _everything.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            var cur = ConfigStore.Load();   // applies at once, like the switches on the Main tab
            cur.LimitEverything = _everything.Checked;
            ConfigStore.Save(cur);
        };
        exceptions.Click += (_, _) => { using var f = new ExceptionsForm(ConfigStore.Load()); f.ShowDialog(ParentForm); };   // a fresh copy: never a stale list
        save.Click += (_, _) => Save();
        close.Click += (_, _) => { if (ConfirmLeave()) Close(); };
    }

    /// <summary>Puts the columns back in the order the user dragged them to. The app-name column stays first and the filler last.</summary>
    void ApplyColumnOrder()
    {
        var ord = UiPrefs.Current.ColumnOrder;
        int n = _running.Columns.Count;
        if (ord.Count != n || ord[0] != 0 || ord[n - 1] != n - 1 || ord.Distinct().Count() != n || ord.Any(v => v < 0 || v >= n)) return;
        foreach (int col in Enumerable.Range(0, n).OrderBy(i => ord[i]))
            if (_running.Columns[col].DisplayIndex != ord[col]) _running.Columns[col].DisplayIndex = ord[col];
    }

    /// <summary>Applies Settings > Window > Real time update speed to the Apps list timer.</summary>
    public void ApplyUpdateSpeed()
    {
        switch (UiPrefs.Current.UpdateSpeed)
        {
            case "Paused": _timer.Stop(); return;
            case "High": _timer.Interval = 1000; break;
            case "Low": _timer.Interval = 4000; break;
            default: _timer.Interval = 2000; break;
        }
        if (!_timer.Enabled) { _timer.Start(); if (IsHandleCreated) RefreshRunning(); }
    }

    /// <summary>"Minimize on use": after an action that moves you to another app, put this window away.</summary>
    void MinimizeIfWanted()
    {
        if (UiPrefs.Current.MinimizeOnUse && ParentForm is Form f) f.WindowState = FormWindowState.Minimized;
    }

    /// <summary>In Smart mode the intensity level (Custom included) decides the ceilings, so the per-app Limit/Min/Max only work in
    /// Hard mode and for rows ticked "Own limit". Greys them out otherwise, and refreshes the table when the file changed elsewhere.</summary>
    public void RefreshCustomState()
    {
        Config c;
        DateTime stamp = DateTime.MinValue;
        try { stamp = File.GetLastWriteTimeUtc(ConfigStore.FilePath); } catch { }
        if (_lastCfg != null && stamp == _lastStamp) c = _lastCfg;   // unchanged file: no disk read
        else { c = ConfigStore.Load(); _lastCfg = c; _lastStamp = stamp; }
        if (!_dirty && !_grid.IsCurrentCellInEditMode && TargetsSig(c) != _loadedSig) LoadGrid(c);
        bool on = !c.IsSmart();
        _syncing = true;
        _everything.Checked = c.LimitEverything;
        _syncing = false;
        foreach (var col in new[] { "Ceiling", "Min", "Max", "Adaptive" })
        {
            _grid.Columns[col].DefaultCellStyle.ForeColor = on ? Ui.Text : Ui.Muted;
        }
        _customNote.Text = on ? "" : "These numbers are only used in Hard mode, or for rows ticked \"Own limit\". In Smart mode the intensity level sets the ceilings itself.";
    }

    /// <summary>Asks what to do with unsaved edits in the limits table. false = stay where you are.</summary>
    public bool ConfirmLeave()
    {
        if (!_dirty) return true;
        var ask = MessageBox.Show(this, "You changed the limits table. Save the changes?", "GameRamCap", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (ask == DialogResult.Cancel) return false;
        if (ask == DialogResult.Yes) Save();
        else LoadGrid(ConfigStore.Load());   // discard
        return !_dirty;
    }

    /// <summary>Ctrl+S saves the table while this tab is showing.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S) && Visible) { Save(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    static string TargetsSig(Config c) => JsonSerializer.Serialize(c.Targets);

    /// <summary>Fills the limits table from a config. Targets of protected apps are not shown but are kept for Save.</summary>
    void LoadGrid(Config c)
    {
        _loadingGrid = true;
        try
        {
            _hiddenTargets = c.Targets.Where(t => Engine.Protected.Contains(t.Exe)).ToList();
            _grid.Rows.Clear();
            foreach (var t in c.Targets.Where(t => !Engine.Protected.Contains(t.Exe)))
                _grid.Rows.Add(t.Exe, t.CeilingMB, t.MinCeilingMB, t.MaxCeilingMB, t.Adaptive, t.UseOwnLimit, t.BudgetMB, string.Join(", ", t.FocusAlso));
            _loadedSig = TargetsSig(c);
            _dirty = false;
        }
        finally { _loadingGrid = false; }
        _showEmpty?.Invoke();
    }

    HashSet<string> GridNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow r in _grid.Rows)
            if (r.Cells["Exe"].Value is string s) set.Add(s);
        return set;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _relayout.Dispose();
            try { if (_hook.Handle != IntPtr.Zero) _hook.ReleaseHandle(); } catch { }
            if (_sampling) _closePdh = true; else ClosePdh();   // never close the counters under a running scan
            foreach (var b in _icons.Values) b.Dispose();
            _icons.Clear();
        }
        base.Dispose(disposing);
    }

    void AddRow(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || Engine.Protected.Contains(name) || GridNames().Contains(name)) return;
        var d = new Target();
        _grid.Rows.Add(name, d.CeilingMB, d.MinCeilingMB, d.MaxCeilingMB, d.Adaptive, d.UseOwnLimit, 0, "");
    }

    void AddSelected()
    {
        foreach (ListViewItem item in _running.SelectedItems) if (item.Tag is string n) AddRow(n);
        RefreshRunning();
    }

    void AddManual()
    {
        AddRow(_manual.Text);
        _manual.Clear();
        RefreshRunning();
    }

    void RemoveSelected()
    {
        foreach (DataGridViewRow r in _grid.SelectedRows.Cast<DataGridViewRow>().ToList())
            _grid.Rows.Remove(r);
        RefreshRunning();
    }

    void Save()
    {
        _grid.EndEdit();
        var list = new List<Target>();
        var problems = new List<string>();
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.Cells["Exe"].Value is not string exe || exe.Length == 0) continue;
            var v = TargetsLogic.Build(exe, r.Cells["Ceiling"].Value, r.Cells["Min"].Value, r.Cells["Max"].Value,
                r.Cells["Adaptive"].Value is true, r.Cells["Own"].Value is true, r.Cells["Budget"].Value,
                Convert.ToString(r.Cells["Also"].Value), problems);
            list.Add(new Target { Exe = v.Exe, CeilingMB = v.CeilingMB, MinCeilingMB = v.MinMB, MaxCeilingMB = v.MaxMB,
                                  Adaptive = v.Adaptive, UseOwnLimit = v.Own, FocusAlso = v.Also, BudgetMB = v.BudgetMB });
        }
        // (Exceptions / UnprotectedBuiltIn are not touched here: they are edited elsewhere and live in the file.)
        var cur = ConfigStore.Load();
        // targets of protected apps are hidden in the table; keep them unless the user added the same app again
        foreach (var h in _hiddenTargets)
            if (!list.Any(t => string.Equals(t.Exe, h.Exe, StringComparison.OrdinalIgnoreCase))) list.Add(h);
        cur.Targets = list;
        cur.FocusMode = true;
        ConfigStore.Save(cur); // the tray app notices the saved file and reloads within a second
        _lastCfg = null;

        // show the clamped values back in the grid
        LoadGrid(cur);
        string msg = "Saved. The limits update within a second.";
        if (problems.Count > 0) msg += "\n\nSome values were corrected:\n" + string.Join("\n", problems.Take(8)) + (problems.Count > 8 ? $"\n... and {problems.Count - 8} more" : "");
        MessageBox.Show(this, msg, "GameRamCap", MessageBoxButtons.OK, problems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }
}
