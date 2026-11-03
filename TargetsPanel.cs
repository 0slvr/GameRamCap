using System.Diagnostics;

namespace GameRamCap;

/// <summary>Pick which running apps take part in the experiments.</summary>
internal sealed class TargetsPanel : UserControl
{
    readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Search running apps..." };
    readonly ListView _running = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, Dock = DockStyle.Fill,
    };
    readonly ListBox _chosen = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended };
    readonly CheckBox _everything = new() { Text = "Test ALL apps (audio / mic apps are protected automatically)", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    readonly Label _saved = new() { AutoSize = true, Margin = new Padding(12, 9, 3, 3), ForeColor = Color.SeaGreen };

    public TargetsPanel()
    {
        Dock = DockStyle.Fill;
        var cfg = ConfigStore.Load();

        _running.Columns.Add("Running app", 170);
        _running.Columns.Add("RAM", 70);
        _running.Columns.Add("Processes", 70);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(new Label { Text = "Running apps (double-click to add)", AutoSize = true }, 0, 0);
        left.Controls.Add(_search, 0, 1);
        left.Controls.Add(_running, 0, 2);
        var add = new Button { Text = "Add  →", AutoSize = true };
        left.Controls.Add(add, 0, 3);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(new Label { Text = "Apps in the test", AutoSize = true }, 0, 0);
        right.Controls.Add(_chosen, 0, 1);
        var remove = new Button { Text = "Remove selected", AutoSize = true };
        right.Controls.Add(remove, 0, 2);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var save = new Button { Text = "Save", AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
        bottom.Controls.Add(_everything);
        bottom.Controls.Add(save);
        bottom.Controls.Add(_saved);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        root.Controls.Add(bottom, 0, 1);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);

        _everything.Checked = cfg.LabEverything;
        foreach (var t in cfg.Targets.Where(t => !Engine.Protected.Contains(t.Exe))) _chosen.Items.Add(t.Exe);
        RefreshRunning();

        _search.TextChanged += (_, _) => RefreshRunning();
        add.Click += (_, _) => AddSelected();
        _running.DoubleClick += (_, _) => AddSelected();
        remove.Click += (_, _) => RemoveSelected();
        save.Click += (_, _) => Save();
    }

    HashSet<string> ChosenNames() =>
        new(_chosen.Items.Cast<string>(), StringComparer.OrdinalIgnoreCase);

    public void RefreshRunning()
    {
        var filter = _search.Text.Trim();
        var have = ChosenNames();
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

    void AddSelected()
    {
        var have = ChosenNames();
        foreach (ListViewItem item in _running.SelectedItems)
            if (!have.Contains(item.Text)) _chosen.Items.Add(item.Text);
        RefreshRunning();
    }

    void RemoveSelected()
    {
        foreach (var x in _chosen.SelectedItems.Cast<string>().ToList()) _chosen.Items.Remove(x);
        RefreshRunning();
    }

    void Save()
    {
        // Start from the file as it is NOW, so settings changed in the window meanwhile are kept.
        var cur = ConfigStore.Load();
        var old = cur.Targets;
        cur.Targets = _chosen.Items.Cast<string>()
            .Select(n => old.FirstOrDefault(x => string.Equals(x.Exe, n, StringComparison.OrdinalIgnoreCase)) ?? new Target { Exe = n })
            .ToList();
        cur.LabEverything = _everything.Checked;
        ConfigStore.Save(cur); // the app notices the saved file and reloads within a second
        _saved.Text = "Saved ✓";
    }
}
