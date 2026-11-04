using System.Diagnostics;

namespace GameRamCap;

/// <summary>Lets the user pick which running apps get a RAM ceiling and edit each ceiling.</summary>
internal sealed class TargetsForm : Form
{
    readonly Config _cfg = ConfigStore.Load();
    readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Search running apps..." };
    readonly ListView _running = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, Dock = DockStyle.Fill,
    };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
    };
    readonly TextBox _manual = new() { Width = 150, PlaceholderText = "app name (no .exe)", Margin = new Padding(3, 6, 3, 3) };
    readonly Label _customNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 3, 3, 6) };

    public TargetsForm()
    {
        Text = "GameRamCap - apps, limits and exceptions";
        ClientSize = new Size(920, 540);
        MinimumSize = new Size(780, 440);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Base;

        _running.Columns.Add("Running app", 170);
        _running.Columns.Add("RAM", 70);
        _running.Columns.Add("Processes", 70);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Exe", HeaderText = "App", ReadOnly = true, FillWeight = 130, MinimumWidth = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ceiling", HeaderText = "Limit MB", FillWeight = 70, MinimumWidth = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Min", HeaderText = "Min MB", FillWeight = 60, MinimumWidth = 65 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Max", HeaderText = "Max MB", FillWeight = 60, MinimumWidth = 65 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Adaptive", HeaderText = "Adaptive", FillWeight = 60, MinimumWidth = 75 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Own", HeaderText = "Own limit", ToolTipText = "Use this app's numbers instead of the intensity level", FillWeight = 60, MinimumWidth = 75 });

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Budget", HeaderText = "Budget MB", ToolTipText = "Smart mode: total RAM of all this app's processes while in the background. 0 = use Max MB", FillWeight = 65, MinimumWidth = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Also", HeaderText = "Focus also", ToolTipText = "Other process names that count as this app being focused (comma separated)", FillWeight = 90, MinimumWidth = 90 });

        // Left: running apps
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(Ui.Heading("Running apps (double-click to add)"), 0, 0);
        left.Controls.Add(_search, 0, 1);
        left.Controls.Add(_running, 0, 2);
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var add = new Button { Text = "Add  →", AutoSize = true };
        leftButtons.Controls.Add(refresh);
        leftButtons.Controls.Add(add);
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

        // Bottom: three tidy rows
        var defaults = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        defaults.Controls.Add(_customNote);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var exceptions = new Button { Text = "Exceptions...", AutoSize = true };
        options.Controls.Add(exceptions);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Save", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        buttons.Controls.Add(close);
        buttons.Controls.Add(save);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3 };
        bottom.Controls.Add(defaults, 0, 0);
        bottom.Controls.Add(options, 0, 1);
        bottom.Controls.Add(buttons, 0, 2);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = Ui.PagePadding };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
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
        RefreshRunning();
        RefreshCustomState();

        refresh.Click += (_, _) => RefreshRunning();
        _search.TextChanged += (_, _) => RefreshRunning();
        add.Click += (_, _) => AddSelected();
        _running.DoubleClick += (_, _) => AddSelected();
        remove.Click += (_, _) => RemoveSelected();
        addManual.Click += (_, _) => AddManual();
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
        foreach (var col in new[] { "Ceiling", "Min", "Max", "Adaptive" })
        {
            _grid.Columns[col].ReadOnly = false;   // editable: they apply to Custom, Hard, and to rows ticked "Own limit"
            _grid.Columns[col].DefaultCellStyle.ForeColor = on ? SystemColors.ControlText : SystemColors.GrayText;
        }
        _customNote.Text = on ? "" : "These numbers are only used with the Custom intensity or Hard mode (Advanced > Mode). The current level sets the ceilings itself.";
    }

    HashSet<string> GridNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow r in _grid.Rows)
            if (r.Cells["Exe"].Value is string s) set.Add(s);
        return set;
    }

    void RefreshRunning()
    {
        var filter = _search.Text.Trim();
        var have = GridNames();
        var groups = new Dictionary<string, (long ws, int n)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (Engine.Protected.Contains(name) || have.Contains(name)) continue;
                long ws = p.WorkingSet64;
                groups[name] = groups.TryGetValue(name, out var g) ? (g.ws + ws, g.n + 1) : (ws, 1);
            }
            catch { /* process exited or access denied */ }
            finally { p.Dispose(); }
        }

        _running.BeginUpdate();
        _running.Items.Clear();
        foreach (var kv in groups.OrderByDescending(k => k.Value.ws))
        {
            if (filter.Length > 0 && !kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListViewItem(kv.Key);
            item.SubItems.Add((kv.Value.ws / (1024 * 1024)) + " MB");
            item.SubItems.Add(kv.Value.n.ToString());
            _running.Items.Add(item);
        }
        _running.EndUpdate();
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
        foreach (ListViewItem item in _running.SelectedItems) AddRow(item.Text);
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
