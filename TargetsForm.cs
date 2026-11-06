using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameRamCap;

/// <summary>Lets the user pick which running apps get a RAM ceiling and edit each ceiling.</summary>
internal sealed class TargetsForm : Form
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
        var relayout = new System.Windows.Forms.Timer { Interval = 40 };
        relayout.Tick += (_, _) => { relayout.Stop(); LayoutColumns(); };
        _running.ClientSizeChanged += (_, _) => { LayoutColumns(); relayout.Stop(); relayout.Start(); };
        _running.HandleCreated += (_, _) => { AttachScrollHook(); ApplyColumnOrder(); LayoutColumns(); };
        AttachScrollHook();
        Shown += (_, _) => LayoutColumns();
        LayoutColumns();

        foreach (var t in _cfg.Targets.Where(t => !Engine.Protected.Contains(t.Exe)))
            _grid.Rows.Add(t.Exe, t.CeilingMB, t.MinCeilingMB, t.MaxCeilingMB, t.Adaptive, t.UseOwnLimit, t.BudgetMB, string.Join(", ", t.FocusAlso));
        _showEmpty?.Invoke();
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
        _timer.Tick += (_, _) => { if (Visible && IsHandleCreated && !_menuOpen) RefreshRunning(); };
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
        menu.Items.Add("Open file location", null, (_, _) => { OpenLocation(); MinimizeIfWanted(); });
        menu.Items.Add("Check on VirusTotal (SHA-256, no upload)", null, (_, _) => CheckVirusTotal());
        menu.Items.Add("Start new task...", null, (_, _) => { StartTask(); MinimizeIfWanted(); });
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
        headerMenu.Items.Add("Start new task...", null, (_, _) => { StartTask(); MinimizeIfWanted(); });
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
        exceptions.Click += (_, _) => { using var f = new ExceptionsForm(_cfg); f.ShowDialog(ParentForm); };
        save.Click += (_, _) => Save();
        close.Click += (_, _) => Close();
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

    /// <summary>In Smart mode the five levels decide the ceilings, so the per-app Limit/Min/Max and the
    /// "apps not in the list" limit only work with the Custom level (and in Hard mode). Greys them out otherwise.</summary>
    public void RefreshCustomState()
    {
        var c = ConfigStore.Load();
        bool on = !c.IsSmart() || Presets.IsCustom(c);
        _syncing = true;
        _everything.Checked = c.LimitEverything;
        _syncing = false;
        foreach (var col in new[] { "Ceiling", "Min", "Max", "Adaptive" })
        {
            _grid.Columns[col].ReadOnly = false;   // editable: they apply to Custom, Hard, and to rows ticked "Own limit"
            _grid.Columns[col].DefaultCellStyle.ForeColor = on ? Ui.Text : Ui.Muted;
        }
        _customNote.Text = on ? "" : "These numbers are only used in Hard mode, or for rows ticked \"Own limit\". In Smart mode the intensity level sets the ceilings itself.";
    }

    HashSet<string> GridNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow r in _grid.Rows)
            if (r.Cells["Exe"].Value is string s) set.Add(s);
        return set;
    }

    /// <summary>Scans the processes (RAM, CPU %, I/O per second) and redraws the list.</summary>
    bool _sampling;

    /// <summary>The heavy part (reading every process, GPU and network counters, app icons) runs on a background thread, so the window
    /// opens at once and never freezes; the list is then filled on the UI thread.</summary>
    async void RefreshRunning()
    {
        if (_sampling) return;
        _sampling = true;
        try { await Task.Run(Sample); }
        catch { }
        finally { _sampling = false; }
        if (IsDisposed || Disposing) return;
        try { Render(); } catch { }
    }

    /// <summary>Reads every process once. CPU % and I/O per second need two samples, so they appear after the second scan.
    /// Scans closer than a second apart keep the last rates (a quick manual Refresh must not give noisy numbers).</summary>
    void Sample()
    {
        var now = DateTime.UtcNow;
        double secs = (now - _prevAt).TotalSeconds;
        bool first = _prevAt == DateTime.MinValue;
        bool baseline = first || secs >= 1.0;
        bool rates = !first && secs >= 1.0;
        var curPid = new Dictionary<int, (long cpu, long io)>();
        var procRate = rates ? new Dictionary<int, (double cpu, double io)>() : _procRate;
        var gpu = GpuUsage.Read();   // null until the first full sample exists
        var net = NetUsage.Read();   // same for network traffic
        _netRate = net;
        var groups = new Dictionary<string, Grp>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string name;
                try { name = p.ProcessName; } catch { continue; }   // exited while we looked
                if (!groups.TryGetValue(name, out var g)) groups[name] = g = new Grp();
                g.N++;
                if (gpu != null && gpu.TryGetValue(p.Id, out var gv)) g.Gpu += gv;
                if (net != null && net.TryGetValue(p.Id, out var nv)) g.Net += nv;
                long pws = 0;
                try { pws = p.WorkingSet64; g.Ws += pws; } catch { g.Denied = true; }   // access denied: still listed, RAM unknown
                g.Procs.Add((p.Id, pws));
                if (baseline && Native.GetCpuIo(p.Id, out var cpu, out var io))
                {
                    curPid[p.Id] = (cpu, io);
                    if (rates && _prevPid.TryGetValue(p.Id, out var pv))
                    {
                        long dc = Math.Max(0, cpu - pv.cpu), di = Math.Max(0, io - pv.io);
                        g.CpuD += dc;   // only processes seen in both scans count
                        g.IoD += di;
                        procRate[p.Id] = (Math.Min(100.0, dc / (secs * 1e7) / Math.Max(1, Environment.ProcessorCount) * 100.0), di / secs);
                    }
                }
            }
            finally { p.Dispose(); }
        }

        if (baseline)
        {
            if (rates)
            {
                var newRate = new Dictionary<string, (double cpu, double io)>(StringComparer.OrdinalIgnoreCase);
                double cores = Math.Max(1, Environment.ProcessorCount);
                foreach (var kv in groups)
                    newRate[kv.Key] = (Math.Min(100.0, kv.Value.CpuD / (secs * 1e7) / cores * 100.0), kv.Value.IoD / secs);
                _rate = newRate;
                _procRate = procRate;
            }
            _prevPid = curPid;
            _prevAt = now;
        }

        // icons are read here, off the UI thread (this was the slowest part of opening the window)
        foreach (var kv in groups)
            if (!_icons.ContainsKey(kv.Key)) IconFor(kv.Key, kv.Value.Procs.Count > 0 ? kv.Value.Procs[0].Pid : 0);

        _rows = groups.Select(kv => new Row(kv.Key, kv.Value.Ws, kv.Value.N, kv.Value.Denied,
            _rate.TryGetValue(kv.Key, out var r) ? r.cpu : (double?)null,
            _rate.TryGetValue(kv.Key, out var r2) ? r2.io : (double?)null,
            gpu != null ? Math.Min(100.0, kv.Value.Gpu) : (double?)null,
            net != null ? kv.Value.Net : (double?)null, kv.Value.Procs)).ToList();
    }

    IOrderedEnumerable<Row> Ord(IEnumerable<Row> rows, Func<Row, double> key) =>
        _sortAsc ? rows.OrderBy(key) : rows.OrderByDescending(key);

    IEnumerable<Row> Sorted(IEnumerable<Row> rows)
    {
        var q = _sortCol switch
        {
            0 => _sortAsc ? rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                          : rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase),
            2 => Ord(rows, r => r.Cpu ?? -1),
            3 => Ord(rows, r => r.Gpu ?? -1),
            4 => Ord(rows, r => r.Io ?? -1),
            5 => Ord(rows, r => r.Net ?? -1),
            6 => Ord(rows, r => r.N),
            _ => Ord(rows, r => r.Ws),
        };
        return q.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }

    // ---- the user's view of the running list: which columns show, and how tall the rows are ----
    // Column shares (in %, of the visible columns) and the narrowest each column may get. The list always fills its width:
    // resize the window and every column follows; drag a header edge and the others give or take the room.
    static readonly int[] ColMin = { 120, 64, 56, 56, 72, 72, 56, 90 };
    static readonly (string Label, int Px)[] RowHeights = { ("Compact", 28), ("Comfortable", 36), ("Spacious", 44) };
    bool _layingOut;

    // Like Task Manager: every column keeps its own width and dragging a header edge changes only that column.
    // The last shown column (Status) takes whatever room is left, so the app-name column never moves when you change a column on its right.
    static readonly double[] ColPx = { 260, 80, 64, 64, 84, 84, 60, 120 };
    double[]? _px;

    double[] Widths()
    {
        if (_px != null) return _px;
        var saved = UiPrefs.Current.ColumnPx;
        _px = saved.Count == ColNames.Length && saved.Take(ColNames.Length - 1).All(v => v > 0) ? saved.ToArray() : (double[])ColPx.Clone();
        return _px;
    }

    bool IsHidden(int i) => i > 0 && (ColNames[i] == "Proc." || ColNames[i] == "Status" || UiPrefs.Current.HiddenColumns.Contains(ColNames[i]));   // "Proc." and "Status" are removed for good

    /// <summary>The column that takes the leftover width (Status; the one before it if Status is hidden).</summary>
    int LastShown()
    {
        for (int i = ColNames.Length - 1; i >= 1; i--) if (!IsHidden(i)) return i;
        return 0;
    }

    int Desired(int i) => Math.Max(Ui.S(this, ColMin[i]), Ui.S(this, (int)Widths()[i]));

    void LayoutColumns()
    {
        if (_layingOut || _running.ClientSize.Width <= 0) return;
        _layingOut = true;
        _running.BeginUpdate();
        try
        {
            int avail = _running.ClientSize.Width;
            int n = ColNames.Length;
            var w = new int[n];
            int sum = 0;
            for (int i = 0; i < n; i++)
            {
                if (IsHidden(i)) continue;
                w[i] = Desired(i);
                sum += w[i];
            }
            // Every column keeps the width it was given; when they add up to more than the list, the list scrolls sideways
            // (the header follows). Empty the filler first and shrink columns before growing others, so no stray scroll appears
            // while the columns are changed one by one.
            var filler = _running.Columns[n];
            if (filler.Width != 0) filler.Width = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    var col = _running.Columns[i];
                    if (IsHidden(i))
                    {
                        if (pass == 0) { col.Tag = "hidden"; if (col.Width != 0) col.Width = 0; }
                        continue;
                    }
                    col.Tag = null;
                    if (pass == 0 ? w[i] < col.Width : w[i] > col.Width) col.Width = w[i];
                }
            // the leftover room goes to an invisible filler column, so no real column changes when the window or another column does
            int fw = Math.Max(0, avail - sum);
            if (filler.Width != fw) filler.Width = fw;
        }
        finally { _running.EndUpdate(); _layingOut = false; _header?.Invalidate(); }
    }

    /// <summary>The user drags a column edge: only that column changes. If the columns end up wider than the list, it scrolls sideways.</summary>
    void OnHeaderResizing(int col, int wanted)
    {
        double scale = Math.Max(0.1, Ui.S(this, 1000) / 1000.0);
        int w = Math.Clamp(wanted, Ui.S(this, ColMin[col]), Ui.S(this, 1500));
        Widths()[col] = w / scale;
        LayoutColumns();
    }

    /// <summary>Tells us when the list is scrolled sideways, so the header can move with it.</summary>
    sealed class ScrollHook : NativeWindow
    {
        public Action? Scrolled;
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x114 /* WM_HSCROLL */ || m.Msg == 0x20E /* WM_MOUSEHWHEEL */ || m.Msg == 0x115 /* WM_VSCROLL */) Scrolled?.Invoke();
        }
    }
    readonly ScrollHook _hook = new();

    void AttachScrollHook()
    {
        if (!_running.IsHandleCreated) return;
        if (_hook.Handle != IntPtr.Zero) _hook.ReleaseHandle();
        _hook.AssignHandle(_running.Handle);
        _hook.Scrolled = () => _header?.Invalidate();
    }

    void ApplyColumns() => LayoutColumns();

    void SetRowHeight(int px)
    {
        UiPrefs.Current.RowHeight = px;
        _running.SmallImageList = new ImageList { ImageSize = new Size(1, Ui.S(this, px)) };
    }

    void BuildViewItems(List<ToolStripItem> items)
    {
        var hidden = UiPrefs.Current.HiddenColumns;
        items.Add(new ToolStripMenuItem("Columns") { Enabled = false });
        for (int i = 1; i < ColNames.Length; i++)
        {
            string name = ColNames[i];
            if (name == "Proc." || name == "Status") continue;   // columns removed
            var it = new ToolStripMenuItem(name) { Checked = !hidden.Contains(name) };
            it.Click += (_, _) =>
            {
                if (!hidden.Remove(name)) hidden.Add(name);
                UiPrefs.Save();
                ApplyColumns();
            };
            items.Add(it);
        }
    }

    // ---- icons and heat map ----
    Bitmap IconFor(string app, int pid)
    {
        if (_icons.TryGetValue(app, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            string? path = Native.GetImagePath(pid);
            if (path != null && File.Exists(path))
            {
                using var ic = Icon.ExtractAssociatedIcon(path);
                if (ic != null)
                {
                    bmp = new Bitmap(16, 16);
                    using var g = Graphics.FromImage(bmp);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(ic.ToBitmap(), 0, 0, 16, 16);
                }
            }
        }
        catch { bmp = null; }
        if (bmp == null)
        {
            // no icon available (system process, no access): a small rounded tile with the first letter
            bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Ui.RoundRect(new Rectangle(0, 0, 15, 15), 4))
            using (var br = new SolidBrush(Color.FromArgb(150, Ui.Accent))) g.FillPath(br, path);
            using var f = new Font("Segoe UI Semibold", 7.5f);
            TextRenderer.DrawText(g, app.Length > 0 ? app.Substring(0, 1).ToUpperInvariant() : "?", f, new Rectangle(0, 0, 16, 16), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        _icons[app] = bmp;
        return bmp;
    }

    static double LogHeat(double? bytesPerSec) => bytesPerSec is double b && b >= 1 ? Math.Min(1.0, Math.Log10(b) / 7.0) : 0;

    static void SetHeat(ListViewItem it, double ram, double cpu, double gpu, double io, double net)
    {
        it.SubItems[1].Tag = Math.Clamp(ram, 0, 1);
        it.SubItems[2].Tag = Math.Clamp(cpu, 0, 1);
        it.SubItems[3].Tag = Math.Clamp(gpu, 0, 1);
        it.SubItems[4].Tag = Math.Clamp(io, 0, 1);
        it.SubItems[5].Tag = Math.Clamp(net, 0, 1);
    }

    static string? Pct(double? v) => v is double p ? (p < 1 ? "0%" : p.ToString("0") + "%") : null;

    /// <summary>The big totals above the column names.</summary>
    void UpdateSummaries()
    {
        double free = Native.FreeRamPercent();
        double cpu = Math.Min(100, _rows.Sum(r => r.Cpu ?? 0));
        double gpu = Math.Min(100, _rows.Sum(r => r.Gpu ?? 0));
        _header.Summaries = new string?[]
        {
            null,
            free >= 0 ? (100 - free).ToString("0") + "%" : null,
            cpu.ToString("0") + "%",
            gpu.ToString("0") + "%",
            Pct(SystemLoad.DiskPercent()),
            Pct(SystemLoad.NetPercent(_rows.Sum(r => r.Net ?? 0))),
            null, null,
        };
        _header.Invalidate();
    }

    void UpdateHeaders()
    {
        for (int i = 0; i < ColNames.Length; i++)
            _running.Columns[i].Text = i == _sortCol ? ColNames[i] + (_sortAsc ? " ▲" : " ▼") : ColNames[i];
        _header?.Invalidate();
    }

    // Resource values: Memory = share of installed RAM; Disk = the system's disk busy time split by each app's share of the I/O;
    // Network = share of the fastest connected adapter's speed.
    double _ioSum, _diskBusy;

    static string FmtPct(double p) => p < 0.05 ? "0%" : p.ToString("0.0") + "%";

    static string MemText(long ws)
    {
        long total = Native.TotalRam();
        return UiPrefs.Current.MemoryAsPercent && total > 0 ? FmtPct(100.0 * ws / total) : (ws / (1024 * 1024)) + " MB";
    }

    string DiskText(double? io)
    {
        if (!UiPrefs.Current.DiskAsPercent || io is not double b) return FmtRate(io);
        return _ioSum > 0 ? FmtPct(Math.Min(100, _diskBusy * b / _ioSum)) : "0%";
    }

    static string NetText(double? bytesPerSec)
    {
        if (UiPrefs.Current.NetAsPercent && bytesPerSec is double b && SystemLoad.NetPercent(b) is double p) return FmtPct(p);
        return FmtRate(bytesPerSec);
    }

    static string FmtRate(double? bytesPerSec)
    {
        if (bytesPerSec is not double b) return "";
        if (b >= 1024 * 1024) return (b / (1024 * 1024)).ToString("0.0") + " MB/s";
        if (b >= 1024) return (b / 1024).ToString("0") + " KB/s";
        return b >= 1 ? ((int)b) + " B/s" : "0";
    }

    string RuleText(ProcessRule r)
    {
        var parts = new List<string>();
        if (r.Priority.Length > 0) parts.Add(ProcessRules.Label(r.Priority));
        if (r.AffinityMask != 0) parts.Add(CountBits(r.AffinityMask & ProcessRules.AllCoresMask()) + " cores");
        return string.Join(", ", parts);
    }

    static int CountBits(long v) { int n = 0; for (ulong u = (ulong)v; u != 0; u >>= 1) n += (int)(u & 1); return n; }

    ProcessRule? FindRule(string exe) =>
        _rules.FirstOrDefault(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase));

    bool HasForeignKids(Dictionary<int, (int Parent, string Name)> map, Dictionary<int, List<int>> kids, string app)
    {
        foreach (var kv in map)
            if (string.Equals(kv.Value.Name, app, StringComparison.OrdinalIgnoreCase) && kids.TryGetValue(kv.Key, out var l)
                && l.Any(c => !string.Equals(map[c].Name, app, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    /// <summary>One process under an expanded app, then its children, indented by depth.</summary>
    void AddProcNode(Dictionary<int, (int Parent, string Name)> map, Dictionary<int, List<int>> kids, string app, int pid, int depth, HashSet<int> seen)
    {
        if (depth > 32 || !seen.Add(pid) || !map.TryGetValue(pid, out var info)) return;
        Native.GetMemory(pid, out long ws, out _);
        var ci = new ListViewItem(new string(' ', 4 + depth * 4) + (depth > 1 ? "\u2514 " : "") + $"{info.Name} [{pid}]") { Tag = new ChildTag(app, pid), ForeColor = Ui.Muted };
        bool have = _procRate.TryGetValue(pid, out var pr);
        ci.SubItems.Add(ws > 0 ? MemText(ws) : "?");
        ci.SubItems.Add(have ? (pr.cpu < 0.05 ? "0%" : pr.cpu.ToString("0.0") + "%") : "");
        ci.SubItems.Add("");
        ci.SubItems.Add(have ? DiskText(pr.io) : "");
        ci.SubItems.Add(_netRate != null ? NetText(_netRate.GetValueOrDefault(pid)) : "");
        ci.SubItems.Add("");
        ci.SubItems.Add("");
        ci.SubItems.Add("");
        SetHeat(ci, Math.Sqrt(ws / 1048576.0 / 1500.0), have ? Math.Sqrt(pr.cpu / 25.0) : 0, 0, have ? LogHeat(pr.io) : 0, _netRate != null ? LogHeat(_netRate.GetValueOrDefault(pid)) : 0);
        Ui.ItemIcons.AddOrUpdate(ci, IconFor(app, pid));
        _fresh.Add(ci);
        if (kids.TryGetValue(pid, out var l))
            foreach (int c in l.OrderBy(x => map[x].Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x))
                AddProcNode(map, kids, app, c, depth + 1, seen);
    }

    /// <summary>Redraws the list from the last scan: filter, sort, keep the selection and the scroll position.</summary>
    void Render()
    {
        var filter = _search.Text.Trim();
        var have = GridNames();
        var selected = SelectedNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ioSum = _rows.Sum(r => r.Io ?? 0);
        _diskBusy = SystemLoad.DiskPercent() ?? 0;
        var selChildren = _running.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<ChildTag>().ToHashSet();
        object? focusTag = _running.FocusedItem?.Tag;
        int top = 0;
        try { top = _running.TopItem?.Index ?? 0; } catch { }

        var map = Native.ProcessMap();
        map.Remove(0);
        var kids = new Dictionary<int, List<int>>();
        foreach (var kv in map)
        {
            int par = kv.Value.Parent;
            if (par == kv.Key || !map.ContainsKey(par)) continue;
            if (!kids.TryGetValue(par, out var kl)) kids[par] = kl = new List<int>();
            kl.Add(kv.Key);
        }

        _fresh = new List<ListViewItem>();
        foreach (var row in Sorted(_rows))
        {
            if (filter.Length > 0 && !row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            bool canOpen = row.N > 1 || HasForeignKids(map, kids, row.Name);
            bool open = canOpen && _expanded.Contains(row.Name);
            var item = new ListViewItem((canOpen ? (open ? "\u25BE " : "\u25B8 ") : "    ") + row.Name + (row.N > 1 ? $" ({row.N})" : "")) { Tag = row.Name, ForeColor = Ui.Text };
            item.SubItems.Add(row.Denied && row.Ws == 0 ? "?" : MemText(row.Ws));
            item.SubItems.Add(row.Cpu is double c ? (c < 0.05 ? "0%" : c.ToString("0.0") + "%") : "");
            item.SubItems.Add(row.Gpu is double gp ? (gp < 0.05 ? "0%" : gp.ToString("0.0") + "%") : "");
            item.SubItems.Add(DiskText(row.Io));
            item.SubItems.Add(NetText(row.Net));
            item.SubItems.Add(row.N.ToString());
            bool prot = Engine.Protected.Contains(row.Name);
            var rule = FindRule(row.Name);
            var status = new List<string>();
            if (prot) status.Add("Protected");
            else
            {
                if (rule != null && RuleText(rule).Length > 0) status.Add("Rule: " + RuleText(rule));
                if (Suspender.IsFrozen(row.Name)) status.Add("Frozen");
                if (have.Contains(row.Name)) status.Add("In my list");
                if (status.Count == 0 && row.Denied) status.Add("No access");
            }
            item.SubItems.Add(string.Join(", ", status));
            item.SubItems.Add("");   // filler column
            if (prot || have.Contains(row.Name)) item.ForeColor = Ui.Muted;
            if (selected.Contains(row.Name)) item.Selected = true;
            SetHeat(item, Math.Sqrt(row.Ws / 1048576.0 / 1500.0), Math.Sqrt((row.Cpu ?? 0) / 25.0), Math.Sqrt((row.Gpu ?? 0) / 40.0), LogHeat(row.Io), LogHeat(row.Net));
            Ui.ItemIcons.AddOrUpdate(item, IconFor(row.Name, row.Procs.Count > 0 ? row.Procs[0].Pid : 0));
            _fresh.Add(item);
            if (open)
            {
                // same hierarchy as the Process tree window: this app's top processes, then their children (any name), nested
                var mine = new HashSet<int>(map.Where(kv => string.Equals(kv.Value.Name, row.Name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key));
                foreach (int pid in mine.Where(p => !mine.Contains(map[p].Parent) || map[p].Parent == p).OrderBy(p => p))
                    AddProcNode(map, kids, row.Name, pid, 1, new HashSet<int>());
            }
        }
        // Same rows in the same order (the usual case on a timer tick): update the numbers in place. No clearing and no scroll jump,
        // so nothing flickers or repaints wrongly. Only a changed row set rebuilds the list.
        var cur = _running.Items;
        bool same = cur.Count == _fresh.Count;
        for (int i = 0; same && i < cur.Count; i++)
            same = cur[i].Text == _fresh[i].Text && Equals(cur[i].Tag, _fresh[i].Tag);
        _running.BeginUpdate();
        try
        {
            if (same)
            {
                for (int i = 0; i < cur.Count; i++)
                {
                    var a = cur[i]; var b = _fresh[i];
                    for (int k = 0; k < a.SubItems.Count && k < b.SubItems.Count; k++)
                    {
                        if (a.SubItems[k].Text != b.SubItems[k].Text) a.SubItems[k].Text = b.SubItems[k].Text;
                        a.SubItems[k].Tag = b.SubItems[k].Tag;
                    }
                    if (a.ForeColor != b.ForeColor) a.ForeColor = b.ForeColor;
                    if (Ui.ItemIcons.TryGetValue(b, out var ic)) Ui.ItemIcons.AddOrUpdate(a, ic);
                }
            }
            else
            {
                _running.Items.Clear();
                _running.Items.AddRange(_fresh.ToArray());
                // a rebuilt list starts with nothing selected: put the selection (apps and their process rows) and the focus back by name
                foreach (ListViewItem it in _running.Items)
                {
                    bool want = it.Tag is string n ? selected.Contains(n) : it.Tag is ChildTag ct && selChildren.Contains(ct);
                    if (want != it.Selected) it.Selected = want;
                    if (focusTag != null && Equals(it.Tag, focusTag)) it.Focused = true;
                }
            }
        }
        finally { _running.EndUpdate(); }
        _running.Invalidate();
        UpdateSummaries();
        if (!same && top > 0 && top < _running.Items.Count)
        {
            try { _running.TopItem = _running.Items[top]; } catch { }
        }
    }

    List<ListViewItem> _fresh = new();

    // ---------------- Task Manager style actions ----------------

    List<string> SelectedNames() =>
        _running.SelectedItems.Cast<ListViewItem>().Where(i => i.Tag is string).Select(i => (string)i.Tag!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>End task: closes every process of the selected apps (and their child processes), like Task Manager.
    /// System, security and recording processes are refused: ending them can crash Windows or the recording.</summary>
    async void EndSelected()
    {
        var names = SelectedNames();
        if (names.Count == 0) return;
        var blocked = names.Where(n => Engine.Protected.Contains(n)).ToList();
        names = names.Where(n => !Engine.Protected.Contains(n)).ToList();
        if (blocked.Count > 0)
            MessageBox.Show(this, "These are protected (system, security or recording) and cannot be ended from here:\n\n" + string.Join(", ", blocked),
                "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (names.Count == 0) return;

        var ask = MessageBox.Show(this, "End " + string.Join(", ", names) + "?\n\nUnsaved data in these apps will be lost.",
            "End task", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ask != DialogResult.Yes) return;

        int failed = 0;
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(true); }
                catch { failed++; }
                finally { p.Dispose(); }
            }
        }
        await Task.Delay(700);
        RefreshRunning();
        if (failed > 0)
            MessageBox.Show(this, $"{failed} process(es) could not be ended (access denied, or already closing).",
                "End task", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------------- Protect / unprotect ----------------

    /// <summary>Protected apps are never limited, frozen or ended from here. Protect adds the selected apps to the user's own list
    /// (Config.Exceptions); Unprotect removes them from it, and turns off the built-in protection of the ones that come with
    /// GameRamCap.</summary>
    void SetProtection(bool protect)
    {
        var names = SelectedNames();
        if (names.Count == 0) return;
        foreach (var n in names)
        {
            if (protect)
            {
                _cfg.UnprotectedBuiltIn.RemoveAll(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
                if (!_cfg.Exceptions.Contains(n, StringComparer.OrdinalIgnoreCase) && !Engine.BaseProtected.Contains(n)) _cfg.Exceptions.Add(n);
            }
            else
            {
                _cfg.Exceptions.RemoveAll(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
                if (Engine.BaseProtected.Contains(n) && !_cfg.UnprotectedBuiltIn.Contains(n, StringComparer.OrdinalIgnoreCase)) _cfg.UnprotectedBuiltIn.Add(n);
            }
        }
        try
        {
            var cur = ConfigStore.Load();   // start from the file as it is now, change only these two lists
            cur.Exceptions = _cfg.Exceptions;
            cur.UnprotectedBuiltIn = _cfg.UnprotectedBuiltIn;
            ConfigStore.Save(cur);
            Engine.RebuildProtected(cur);   // effective at once; the tray app reloads the file within a second
        }
        catch (Exception ex) { Logger.Write("Could not save protection: " + ex.Message); }
        Render();
    }

    // ---------------- CPU priority and cores (this run, or remembered as a rule) ----------------

    /// <summary>Selected apps minus the protected ones (system, security, recording). Tells the user about the refused ones.</summary>
    List<string> AllowedSelected()
    {
        var names = SelectedNames();
        var blocked = names.Where(n => Engine.Protected.Contains(n)).ToList();
        if (blocked.Count > 0)
            MessageBox.Show(this, "These are protected (system, security or recording) and are left alone:\n\n" + string.Join(", ", blocked),
                "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return names.Where(n => !Engine.Protected.Contains(n)).ToList();
    }

    void SaveRules()
    {
        // Start from the file as it is NOW, so everything changed meanwhile (tray, other tabs) is kept.
        var cur = ConfigStore.Load();
        cur.Rules = _rules;
        ConfigStore.Save(cur);
    }

    ProcessRule GetOrAddRule(string exe)
    {
        var r = FindRule(exe);
        if (r == null) { r = new ProcessRule { Exe = exe }; _rules.Add(r); }
        return r;
    }

    void ReportApplied(int ok, int failed)
    {
        if (failed > 0)
            MessageBox.Show(this, $"{failed} process(es) could not be changed (access denied, or already closing)." +
                (ok > 0 ? $"\n{ok} worked." : ""), "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void SetPriority(string priority, bool remember)
    {
        var names = AllowedSelected();
        if (names.Count == 0) return;
        int ok = 0, failed = 0;
        foreach (var n in names)
        {
            var (o, f) = ProcessRules.SetNow(n, priority, 0);
            ok += o; failed += f;
            if (remember) GetOrAddRule(n).Priority = priority;
        }
        if (remember) SaveRules();
        ReportApplied(ok, failed);
        RefreshRunning();
    }

    /// <summary>Removes the saved priority and/or cores of the selected apps (the running processes keep what they have now).</summary>
    void ForgetRule(bool priority, bool cores)
    {
        bool changed = false;
        foreach (var n in SelectedNames())
        {
            var r = FindRule(n);
            if (r == null) continue;
            if (priority && r.Priority.Length > 0) { r.Priority = ""; changed = true; }
            if (cores && r.AffinityMask != 0) { r.AffinityMask = 0; changed = true; }
            if (r.Priority.Length == 0 && r.AffinityMask == 0) _rules.Remove(r);
        }
        if (changed) SaveRules();
        Render();
    }

    // ---------------- Freeze (suspend), manual only ----------------

    void FreezeSelected()
    {
        var names = AllowedSelected();
        if (names.Count == 0) return;
        int ok = 0, failed = 0;
        foreach (var n in names) { var (o, f) = Suspender.Suspend(n); ok += o; failed += f; }
        ReportApplied(ok, failed);
        RefreshRunning();
    }

    void ResumeSelected()
    {
        foreach (var n in SelectedNames()) Suspender.Resume(n);
        RefreshRunning();
    }

    /// <summary>Choose which logical cores the selected apps may use.</summary>
    void PickCores()
    {
        var names = AllowedSelected();
        if (names.Count == 0) return;
        int count = Math.Clamp(Environment.ProcessorCount, 1, 63);
        long all = ProcessRules.AllCoresMask();

        // start from the cores the first process uses now
        long start = all;
        foreach (var p in Process.GetProcessesByName(names[0]))
        {
            try { start = (long)p.ProcessorAffinity & all; if (start == 0) start = all; break; }
            catch { }
            finally { p.Dispose(); }
        }

        using var f = new Form
        {
            Icon = AppIcon.Window, Text = "CPU cores", ClientSize = new Size(460, 120), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, Font = Ui.Base,
        };
        var note = Ui.Note("Tick the cores " + string.Join(", ", names) + " may run on. At least one.");
        var boxes = new List<ModernCheck>();
        var grid = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(430, 0), WrapContents = true };
        for (int i = 0; i < count; i++)
        {
            var cb = new ModernCheck { Text = i.ToString(), AutoSize = true, Checked = (start & (1L << i)) != 0, Margin = new Padding(3, 3, 12, 3) };
            boxes.Add(cb);
            grid.Controls.Add(cb);
        }
        var allBtn = new ModernButton { Text = "All cores", AutoSize = true };
        allBtn.Click += (_, _) => boxes.ForEach(b => b.Checked = true);
        var remember = new ModernCheck { Text = "Always use these cores for this app", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        var ok = new ModernButton { Text = "Apply", AutoSize = true, Tag = "primary", DialogResult = DialogResult.OK };
        var cancel = new ModernButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        btns.Controls.Add(ok);
        btns.Controls.Add(cancel);
        btns.Controls.Add(allBtn);
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16), AutoSize = true };
        root.Controls.Add(note);
        root.Controls.Add(grid);
        root.Controls.Add(remember);
        root.Controls.Add(btns);
        f.Controls.Add(root);
        f.AutoSize = true;
        f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Ui.Polish(f);

        ok.Click += (_, e) =>
        {
            if (!boxes.Any(b => b.Checked))
            {
                MessageBox.Show(f, "Tick at least one core.", "CPU cores", MessageBoxButtons.OK, MessageBoxIcon.Information);
                f.DialogResult = DialogResult.None;   // stay open
            }
        };
        if (f.ShowDialog(this) != DialogResult.OK) return;

        long mask = 0;
        for (int i = 0; i < boxes.Count; i++) if (boxes[i].Checked) mask |= 1L << i;
        if (mask == 0) return;

        int okCount = 0, failed = 0;
        foreach (var n in names)
        {
            var (o, fl) = ProcessRules.SetNow(n, "", mask);
            okCount += o; failed += fl;
            if (remember.Checked)
            {
                var r = GetOrAddRule(n);
                r.AffinityMask = mask == all ? 0 : mask;   // all cores = no restriction = no rule
                if (r.Priority.Length == 0 && r.AffinityMask == 0) _rules.Remove(r);
            }
        }
        if (remember.Checked) SaveRules();
        ReportApplied(okCount, failed);
        RefreshRunning();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); GpuUsage.Close(); NetUsage.Close(); }
        base.Dispose(disposing);
    }

    void OpenLocation()
    {
        var names = SelectedNames();
        if (names.Count == 0) return;
        var failed = new List<string>();
        foreach (var name in names)
        {
            bool done = false;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (!done && !string.IsNullOrEmpty(path)) { Process.Start("explorer.exe", $"/select,\"{path}\""); done = true; }
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (!done) failed.Add(name);
        }
        if (failed.Count > 0)
            MessageBox.Show(this, "Windows does not let us read the location of: " + string.Join(", ", failed), "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Hashes the exe of the selected app (SHA-256) and opens its VirusTotal page in the browser.
    /// Only the hash goes into the address: the file is not uploaded and no API key is used.</summary>
    async void CheckVirusTotal()
    {
        var names = SelectedNames();
        if (names.Count == 0) return;
        if (names.Count > 3 && MessageBox.Show(this, $"This opens {names.Count} VirusTotal pages in your browser. Continue?", "VirusTotal",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var failed = new List<string>();
        UseWaitCursor = true;
        try
        {
            foreach (var name in names)
            {
                string? path = null;
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { path ??= Native.GetImagePath(p.Id); }
                    finally { p.Dispose(); }
                }
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { failed.Add(name); continue; }
                string file = path, hash;
                try { hash = await Task.Run(() => VirusTotal.Sha256(file)); }
                catch (Exception ex) { failed.Add(name + " (" + ex.Message + ")"); continue; }
                try { Clipboard.SetText(hash); } catch { }   // the last hash is also on the clipboard
                Logger.Write($"VirusTotal: {name} SHA-256 {hash}");
                VirusTotal.Open(hash);
            }
        }
        finally { UseWaitCursor = false; }
        if (failed.Count > 0)
            MessageBox.Show(this, "Could not read the file of: " + string.Join(", ", failed), "VirusTotal", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Start new task: type a program, folder, document or address, or browse for it.
    /// GameRamCap runs as administrator, so a program started from here is elevated too.</summary>
    async void StartTask()
    {
        using var f = new Form
        {
            Icon = AppIcon.Window, Text = "Start new task", ClientSize = new Size(500, 150), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, Font = Ui.Base,
        };
        var note = Ui.Note("Type the name of a program, folder, document or web address and Windows will open it.");
        var box = new ModernTextBox { Width = 380 };
        var browse = new ModernButton { Text = "Browse...", AutoSize = true };
        var ok = new ModernButton { Text = "Start", AutoSize = true, Tag = "primary", DialogResult = DialogResult.OK };
        var cancel = new ModernButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*" };
            if (d.ShowDialog(f) == DialogResult.OK) box.Text = d.FileName;
        };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(box);
        row.Controls.Add(browse);
        var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        btns.Controls.Add(ok);
        btns.Controls.Add(cancel);
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16) };
        root.Controls.Add(note);
        root.Controls.Add(row);
        root.Controls.Add(btns);
        f.Controls.Add(root);
        Ui.Polish(f);
        if (f.ShowDialog(this) != DialogResult.OK) return;

        string target = box.Text.Trim().Trim('"');
        if (target.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not start \"" + target + "\":\n\n" + ex.Message, "Start task", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await Task.Delay(1000);
        RefreshRunning();
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

    static int Num(object? v, int fallback) =>
        int.TryParse(Convert.ToString(v), out var n) && n > 0 ? n : fallback;

    void Save()
    {
        _grid.EndEdit();
        var list = new List<Target>();
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.Cells["Exe"].Value is not string exe || exe.Length == 0) continue;
            int min = Num(r.Cells["Min"].Value, 64);
            int max = Math.Max(min, Num(r.Cells["Max"].Value, 600));
            int ceil = Math.Clamp(Num(r.Cells["Ceiling"].Value, 200), min, max);
            bool adaptive = r.Cells["Adaptive"].Value is true;
            int budget = int.TryParse(Convert.ToString(r.Cells["Budget"].Value), out var bm) && bm > 0 ? bm : 0;
            var also = (Convert.ToString(r.Cells["Also"].Value) ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x)
                .Where(x => x.Length > 0).ToList();
            list.Add(new Target { Exe = exe, CeilingMB = ceil, MinCeilingMB = min, MaxCeilingMB = max, Adaptive = adaptive, UseOwnLimit = r.Cells["Own"].Value is true,
                                  FocusAlso = also, BudgetMB = budget });
        }
        // Start from the file as it is NOW, so Mode/Profile/options changed from the tray meanwhile are kept.
        var cur = ConfigStore.Load();
        cur.Exceptions = _cfg.Exceptions;
        cur.UnprotectedBuiltIn = _cfg.UnprotectedBuiltIn;
        cur.Targets = list;
        cur.FocusMode = true;
        ConfigStore.Save(cur); // the tray app notices the saved file and reloads within a second

        // show the clamped values back in the grid
        _grid.Rows.Clear();
        foreach (var t in list) _grid.Rows.Add(t.Exe, t.CeilingMB, t.MinCeilingMB, t.MaxCeilingMB, t.Adaptive, t.UseOwnLimit, t.BudgetMB, string.Join(", ", t.FocusAlso));
        _showEmpty?.Invoke();
        MessageBox.Show(this, "Saved. The limits update within a second.", "GameRamCap",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
