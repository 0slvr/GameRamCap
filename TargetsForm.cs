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
    readonly TextBox _manual = new() { Width = 130, PlaceholderText = "app name (no .exe)" };
    readonly CheckBox _always = new()
    {
        Text = "Limit these apps all the time (not only while a game runs)", AutoSize = true, Margin = new Padding(3, 8, 20, 3),
    };

    public TargetsForm()
    {
        Text = "GameRamCap - choose apps to limit";
        ClientSize = new Size(920, 540);
        MinimumSize = new Size(780, 420);
        StartPosition = FormStartPosition.CenterScreen;

        _running.Columns.Add("Running app", 170);
        _running.Columns.Add("RAM", 70);
        _running.Columns.Add("Processes", 70);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Exe", HeaderText = "App", ReadOnly = true, FillWeight = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ceiling", HeaderText = "Ceiling MB", FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Min", HeaderText = "Min MB", FillWeight = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Max", HeaderText = "Max MB", FillWeight = 60 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Adaptive", HeaderText = "Adaptive", FillWeight = 60 });

        // Left: running apps
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(new Label { Text = "Running apps (double-click or press Add)", AutoSize = true }, 0, 0);
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
        right.Controls.Add(new Label
        {
            Text = "Limited apps (edit the numbers; Adaptive lets the ceiling grow when the app is busy)", AutoSize = true,
        }, 0, 0);
        right.Controls.Add(_grid, 0, 1);
        var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var remove = new Button { Text = "Remove selected", AutoSize = true };
        var addManual = new Button { Text = "Add by name", AutoSize = true };
        rightButtons.Controls.Add(remove);
        rightButtons.Controls.Add(_manual);
        rightButtons.Controls.Add(addManual);
        right.Controls.Add(rightButtons, 0, 2);

        // Bottom
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var save = new Button { Text = "Save", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        bottom.Controls.Add(_always);
        bottom.Controls.Add(save);
        bottom.Controls.Add(close);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        root.Controls.Add(bottom, 0, 1);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);

        _always.Checked = _cfg.AlwaysLimit;
        foreach (var t in _cfg.Targets)
            _grid.Rows.Add(t.Exe, t.CeilingMB, t.MinCeilingMB, t.MaxCeilingMB, t.Adaptive);
        RefreshRunning();

        refresh.Click += (_, _) => RefreshRunning();
        _search.TextChanged += (_, _) => RefreshRunning();
        add.Click += (_, _) => AddSelected();
        _running.DoubleClick += (_, _) => AddSelected();
        remove.Click += (_, _) => RemoveSelected();
        addManual.Click += (_, _) => AddManual();
        save.Click += (_, _) => Save();
        close.Click += (_, _) => Close();
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
        _grid.Rows.Add(name, d.CeilingMB, d.MinCeilingMB, d.MaxCeilingMB, d.Adaptive);
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
            list.Add(new Target { Exe = exe, CeilingMB = ceil, MinCeilingMB = min, MaxCeilingMB = max, Adaptive = adaptive });
        }
        _cfg.Targets = list;
        _cfg.AlwaysLimit = _always.Checked;
        ConfigStore.Save(_cfg); // the tray app notices the saved file and reloads within a second

        // show the clamped values back in the grid
        _grid.Rows.Clear();
        foreach (var t in list) _grid.Rows.Add(t.Exe, t.CeilingMB, t.MinCeilingMB, t.MaxCeilingMB, t.Adaptive);
        MessageBox.Show(this, "Saved. The limits update within a second.", "GameRamCap",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
