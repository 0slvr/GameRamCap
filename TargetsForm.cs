using System.Diagnostics;

namespace GameRamCap;

/// <summary>Lets the user pick which running apps get a RAM ceiling and edit each ceiling.</summary>
internal sealed class TargetsForm : Form
{
    readonly Config _cfg = ConfigStore.Load();
    readonly ModernTextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Search running apps..." };
    readonly ListView _running = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, Dock = DockStyle.Fill,
    };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ScrollBars = ScrollBars.Vertical,
    };
    readonly ModernTextBox _manual = new() { Width = 150, PlaceholderText = "app name (no .exe)", Margin = new Padding(3, 7, 3, 3) };
    readonly ModernCheck _everything = new() { Text = "Limit all apps, not just my list", AutoSize = true, Margin = new Padding(3, 7, 20, 3) };
    bool _syncing;

    // ---- live columns (CPU %, I/O) and sorting of the running list ----
    sealed class Grp { public long Ws; public int N; public bool Denied; public long CpuD, IoD; public double Gpu; public List<(int Pid, long Ws)> Procs = new(); }
    sealed record Row(string Name, long Ws, int N, bool Denied, double? Cpu, double? Io, double? Gpu, List<(int Pid, long Ws)> Procs);
    // apps whose processes are shown under them (click the arrow, or Right / Left arrow keys)
    readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, (double cpu, double io)> _procRate = new();
    sealed record ChildTag(string App, int Pid);
    static readonly string[] ColNames = { "Running app", "RAM", "CPU", "GPU", "I/O", "Proc.", "Status" };
    List<Row> _rows = new();
    readonly Dictionary<string, (double cpu, double io)> _rate = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<int, (long cpu, long io)> _prevPid = new();
    DateTime _prevAt = DateTime.MinValue;
    int _sortCol = 1;          // default: RAM, biggest first
    bool _sortAsc = false;
    bool _menuOpen;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    readonly List<ProcessRule> _rules;
    readonly Label _customNote = new() { AutoSize = true, Tag = "muted", Margin = new Padding(3, 3, 3, 6) };

    public TargetsForm()
    {
        Text = "GameRamCap - apps, limits and exceptions";
        ClientSize = new Size(1000, 560);
        MinimumSize = new Size(900, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Base;

        _rules = _cfg.Rules ?? new List<ProcessRule>();
        _running.Columns.Add(ColNames[0], 190);
        _running.Columns.Add(ColNames[1], 70);
        _running.Columns.Add(ColNames[2], 55);
        _running.Columns.Add(ColNames[3], 55);
        _running.Columns.Add(ColNames[4], 80);
        _running.Columns.Add(ColNames[5], 50);
        _running.Columns.Add(ColNames[6], 110);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Exe", HeaderText = "App", ReadOnly = true, FillWeight = 130, MinimumWidth = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ceiling", HeaderText = "Limit MB", FillWeight = 70, MinimumWidth = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Min", HeaderText = "Min MB", FillWeight = 60, MinimumWidth = 50 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Max", HeaderText = "Max MB", FillWeight = 60, MinimumWidth = 50 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Adaptive", HeaderText = "Adaptive", FillWeight = 60, MinimumWidth = 60 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Own", HeaderText = "Own limit", ToolTipText = "Use this app's numbers instead of the intensity level", FillWeight = 60, MinimumWidth = 55 });

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Budget", HeaderText = "Budget MB", ToolTipText = "Smart mode: total RAM of all this app's processes while in the background. 0 = use Max MB", FillWeight = 65, MinimumWidth = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Also", HeaderText = "Focus also", ToolTipText = "Other process names that count as this app being focused (comma separated)", FillWeight = 90, MinimumWidth = 70 });

        // Left: running apps
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(Ui.Heading("Running apps (double-click to add)"), 0, 0);
        // search box with Refresh next to it, so the action buttons below fit on one row
        var refresh = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(6, 3, 3, 3) };
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.Controls.Add(_search, 0, 0);
        searchRow.Controls.Add(refresh, 1, 0);
        left.Controls.Add(searchRow, 0, 1);
        left.Controls.Add(_running, 0, 2);
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var add = new Button { Text = "Add  →", AutoSize = true };
        leftButtons.Controls.Add(add);
        var endTask = new Button { Text = "End task", AutoSize = true };
        var startTask = new Button { Text = "Start task...", AutoSize = true };
        leftButtons.Controls.Add(endTask);
        leftButtons.Controls.Add(startTask);
        var history = new Button { Text = "History...", AutoSize = true };
        var connections = new Button { Text = "Connections...", AutoSize = true };
        leftButtons.Controls.Add(history);
        leftButtons.Controls.Add(connections);
        var treeBtn = new Button { Text = "Process tree...", AutoSize = true };
        leftButtons.Controls.Add(treeBtn);
        left.Controls.Add(leftButtons, 0, 3);

        // Right: chosen apps
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(Ui.Heading("Apps with their own limit (edit the numbers)"), 0, 0);
        right.Controls.Add(_grid, 0, 1);
        var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var remove = new Button { Text = "Remove selected", AutoSize = true };
        var addManual = new Button { Text = "Add by name", AutoSize = true };
        rightButtons.Controls.Add(remove);
        rightButtons.Controls.Add(_manual);
        rightButtons.Controls.Add(addManual);
        right.Controls.Add(rightButtons, 0, 2);

        // Bottom: note on top; below it the app-wide option + Exceptions on the left, Save / Close on the right
        var defaults = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        defaults.Controls.Add(_customNote);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        var exceptions = new Button { Text = "Exceptions...", AutoSize = true };
        options.Controls.Add(_everything);
        options.Controls.Add(exceptions);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Save", AutoSize = true, Tag = "primary" };
        var close = new Button { Text = "Close", AutoSize = true };
        buttons.Controls.Add(close);
        buttons.Controls.Add(save);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(defaults, 0, 0);
        bottom.SetColumnSpan(defaults, 2);
        bottom.Controls.Add(options, 0, 1);
        bottom.Controls.Add(buttons, 1, 1);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = Ui.PagePadding };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        root.Controls.Add(bottom, 0, 1);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);
        Ui.Polish(this);

        foreach (var t in _cfg.Targets.Where(t => !Engine.Protected.Contains(t.Exe)))
            _grid.Rows.Add(t.Exe, t.CeilingMB, t.MinCeilingMB, t.MaxCeilingMB, t.Adaptive, t.UseOwnLimit, t.BudgetMB, string.Join(", ", t.FocusAlso));
        UpdateHeaders();
        RefreshRunning();
        RefreshCustomState();

        refresh.Click += (_, _) => RefreshRunning();
        _search.TextChanged += (_, _) => Render();   // typing only filters; no new process scan
        _running.ColumnClick += (_, e) =>
        {
            if (e.Column == ColNames.Length - 1) return;   // Status is not sortable
            if (_sortCol == e.Column) _sortAsc = !_sortAsc;
            else { _sortCol = e.Column; _sortAsc = e.Column == 0; }
            UpdateHeaders();
            Render();
        };
        _timer.Tick += (_, _) => { if (Visible && IsHandleCreated && !_menuOpen) RefreshRunning(); };
        _timer.Start();
        add.Click += (_, _) => AddSelected();
        endTask.Click += (_, _) => EndSelected();
        startTask.Click += (_, _) => StartTask();
        history.Click += (_, _) => InfoForms.ShowHistory(this);
        connections.Click += (_, _) =>
        {
            var names = SelectedNames();
            if (names.Count == 0) { MessageBox.Show(this, "Select one or more apps in the list first.", "Connections", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            InfoForms.ShowConnections(this, names);
        };
        treeBtn.Click += (_, _) => InfoForms.ShowTree(this, SelectedNames());
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
        var menu = new ContextMenuStrip();
        menu.Items.Add("End task", null, (_, _) => EndSelected());
        menu.Items.Add("Open file location", null, (_, _) => OpenLocation());
        menu.Items.Add("Check on VirusTotal (SHA-256, no upload)", null, (_, _) => CheckVirusTotal());
        menu.Items.Add("Start new task...", null, (_, _) => StartTask());
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
    void RefreshRunning() { Sample(); Render(); }

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
        if (rates) _procRate.Clear();
        var gpu = GpuUsage.Read();   // null until the first full sample exists
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
                        _procRate[p.Id] = (Math.Min(100.0, dc / (secs * 1e7) / Math.Max(1, Environment.ProcessorCount) * 100.0), di / secs);
                    }
                }
            }
            finally { p.Dispose(); }
        }

        if (baseline)
        {
            if (rates)
            {
                _rate.Clear();
                double cores = Math.Max(1, Environment.ProcessorCount);
                foreach (var kv in groups)
                    _rate[kv.Key] = (Math.Min(100.0, kv.Value.CpuD / (secs * 1e7) / cores * 100.0), kv.Value.IoD / secs);
            }
            _prevPid = curPid;
            _prevAt = now;
        }

        _rows = groups.Select(kv => new Row(kv.Key, kv.Value.Ws, kv.Value.N, kv.Value.Denied,
            _rate.TryGetValue(kv.Key, out var r) ? r.cpu : (double?)null,
            _rate.TryGetValue(kv.Key, out var r2) ? r2.io : (double?)null,
            gpu != null ? Math.Min(100.0, kv.Value.Gpu) : (double?)null, kv.Value.Procs)).ToList();
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
            5 => Ord(rows, r => r.N),
            _ => Ord(rows, r => r.Ws),
        };
        return q.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }

    void UpdateHeaders()
    {
        for (int i = 0; i < ColNames.Length; i++)
            _running.Columns[i].Text = i == _sortCol ? ColNames[i] + (_sortAsc ? " ▲" : " ▼") : ColNames[i];
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
        ci.SubItems.Add(ws > 0 ? (ws / (1024 * 1024)) + " MB" : "?");
        ci.SubItems.Add(have ? (pr.cpu < 0.05 ? "0%" : pr.cpu.ToString("0.0") + "%") : "");
        ci.SubItems.Add("");
        ci.SubItems.Add(have ? FmtRate(pr.io) : "");
        ci.SubItems.Add("");
        ci.SubItems.Add("");
        _running.Items.Add(ci);
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

        _running.BeginUpdate();
        _running.Items.Clear();
        foreach (var row in Sorted(_rows))
        {
            if (filter.Length > 0 && !row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            bool canOpen = row.N > 1 || HasForeignKids(map, kids, row.Name);
            bool open = canOpen && _expanded.Contains(row.Name);
            var item = new ListViewItem((canOpen ? (open ? "\u25BE " : "\u25B8 ") : "    ") + row.Name) { Tag = row.Name };
            item.SubItems.Add(row.Denied && row.Ws == 0 ? "?" : (row.Ws / (1024 * 1024)) + " MB");
            item.SubItems.Add(row.Cpu is double c ? (c < 0.05 ? "0%" : c.ToString("0.0") + "%") : "");
            item.SubItems.Add(row.Gpu is double gp ? (gp < 0.05 ? "0%" : gp.ToString("0.0") + "%") : "");
            item.SubItems.Add(FmtRate(row.Io));
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
            if (prot || have.Contains(row.Name)) item.ForeColor = Ui.Muted;
            if (selected.Contains(row.Name)) item.Selected = true;
            _running.Items.Add(item);
            if (open)
            {
                // same hierarchy as the Process tree window: this app's top processes, then their children (any name), nested
                var mine = new HashSet<int>(map.Where(kv => string.Equals(kv.Value.Name, row.Name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key));
                foreach (int pid in mine.Where(p => !mine.Contains(map[p].Parent) || map[p].Parent == p).OrderBy(p => p))
                    AddProcNode(map, kids, row.Name, pid, 1, new HashSet<int>());
            }
        }
        _running.EndUpdate();
        if (top > 0 && top < _running.Items.Count)
        {
            try { _running.TopItem = _running.Items[top]; } catch { }
        }
    }

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
            Text = "CPU cores", ClientSize = new Size(460, 120), StartPosition = FormStartPosition.CenterParent,
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
        var allBtn = new Button { Text = "All cores", AutoSize = true };
        allBtn.Click += (_, _) => boxes.ForEach(b => b.Checked = true);
        var remember = new ModernCheck { Text = "Always use these cores for this app", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        var ok = new Button { Text = "Apply", AutoSize = true, Tag = "primary", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
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
        if (disposing) { _timer.Dispose(); GpuUsage.Close(); }
        base.Dispose(disposing);
    }

    void OpenLocation()
    {
        var name = SelectedNames().FirstOrDefault();
        if (name == null) return;
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)) { Process.Start("explorer.exe", $"/select,\"{path}\""); return; }
            }
            catch { }
            finally { p.Dispose(); }
        }
        MessageBox.Show(this, "Windows does not let us read the location of this process.", "GameRamCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Hashes the exe of the selected app (SHA-256) and opens its VirusTotal page in the browser.
    /// Only the hash goes into the address: the file is not uploaded and no API key is used.</summary>
    async void CheckVirusTotal()
    {
        var name = SelectedNames().FirstOrDefault();
        if (name == null) return;
        string? path = null;
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { path ??= Native.GetImagePath(p.Id); }
            finally { p.Dispose(); }
        }
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "Windows does not let us read the file of this process.", "VirusTotal", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string file = path;
        string hash;
        UseWaitCursor = true;
        try { hash = await Task.Run(() => VirusTotal.Sha256(file)); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not read the file:\n\n" + ex.Message, "VirusTotal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally { UseWaitCursor = false; }
        try { Clipboard.SetText(hash); } catch { }   // the hash is also on the clipboard
        Logger.Write($"VirusTotal: {name} SHA-256 {hash}");
        VirusTotal.Open(hash);
    }

    /// <summary>Start new task: type a program, folder, document or address, or browse for it.
    /// GameRamCap runs as administrator, so a program started from here is elevated too.</summary>
    async void StartTask()
    {
        using var f = new Form
        {
            Text = "Start new task", ClientSize = new Size(500, 150), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, Font = Ui.Base,
        };
        var note = Ui.Note("Type the name of a program, folder, document or web address and Windows will open it.");
        var box = new ModernTextBox { Width = 380 };
        var browse = new Button { Text = "Browse...", AutoSize = true };
        var ok = new Button { Text = "Start", AutoSize = true, Tag = "primary", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
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
        cur.Targets = list;
        cur.FocusMode = true;
        ConfigStore.Save(cur); // the tray app notices the saved file and reloads within a second

        // show the clamped values back in the grid
        _grid.Rows.Clear();
        foreach (var t in list) _grid.Rows.Add(t.Exe, t.CeilingMB, t.MinCeilingMB, t.MaxCeilingMB, t.Adaptive, t.UseOwnLimit, t.BudgetMB, string.Join(", ", t.FocusAlso));
        MessageBox.Show(this, "Saved. The limits update within a second.", "GameRamCap",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
