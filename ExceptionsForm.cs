using System.Diagnostics;

namespace GameRamCap;

/// <summary>Apps listed here are NEVER limited (OBS, VMs, anything that must keep all its RAM).</summary>
internal sealed class ExceptionsForm : Form
{
    readonly Config _cfg;
    readonly ListBox _list = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended };
    readonly ListView _running = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, Dock = DockStyle.Fill,
    };
    readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Search running apps..." };
    readonly TextBox _manual = new() { Width = 150, PlaceholderText = "app name (no .exe)" };

    public ExceptionsForm(Config cfg)
    {
        _cfg = cfg;
        Text = "GameRamCap - exceptions (never limited)";
        StartPosition = FormStartPosition.CenterParent;
        Width = 760; Height = 500;
        Font = Ui.Base;

        _running.Columns.Add("Running app", 190);
        _running.Columns.Add("RAM", 80);

        // Left: running apps
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(Ui.Heading("Running apps (double-click to except)"), 0, 0);
        left.Controls.Add(_search, 0, 1);
        left.Controls.Add(_running, 0, 2);
        var lb = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var add = new Button { Text = "Add  →", AutoSize = true };
        lb.Controls.Add(refresh);
        lb.Controls.Add(add);
        left.Controls.Add(lb, 0, 3);

        // Right: exceptions
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(Ui.Heading("Never limited"), 0, 0);
        right.Controls.Add(_list, 0, 1);
        var rb = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var remove = new Button { Text = "Remove selected", AutoSize = true };
        var addManual = new Button { Text = "Add by name", AutoSize = true };
        rb.Controls.Add(remove);
        rb.Controls.Add(_manual);
        rb.Controls.Add(addManual);
        right.Controls.Add(rb, 0, 2);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = Ui.PagePadding };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        var close = new Button { Text = "Close", AutoSize = true };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        bottom.Controls.Add(close);
        root.Controls.Add(bottom, 0, 1);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);
        Ui.Polish(this);

        FillList();
        RefreshRunning();

        refresh.Click += (_, _) => RefreshRunning();
        _search.TextChanged += (_, _) => RefreshRunning();
        add.Click += (_, _) => AddNames(_running.SelectedItems.Cast<ListViewItem>().Select(i => i.Text));
        _running.DoubleClick += (_, _) => AddNames(_running.SelectedItems.Cast<ListViewItem>().Select(i => i.Text));
        remove.Click += (_, _) => RemoveSelected();
        addManual.Click += (_, _) => { AddNames(new[] { _manual.Text }); _manual.Clear(); };
        close.Click += (_, _) => Close();
    }

    void FillList()
    {
        _list.Items.Clear();
        foreach (var n in _cfg.Exceptions) _list.Items.Add(n);
    }

    void AddNames(IEnumerable<string> names)
    {
        bool changed = false;
        foreach (var raw in names.ToList())
        {
            var n = (raw ?? "").Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
            if (n.Length == 0 || _cfg.Exceptions.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
            _cfg.Exceptions.Add(n);
            changed = true;
        }
        if (!changed) return;
        _cfg.Exceptions.Sort(StringComparer.OrdinalIgnoreCase);
        Persist();
    }

    void RemoveSelected()
    {
        var sel = _list.SelectedItems.Cast<string>().ToList();
        if (sel.Count == 0) return;
        _cfg.Exceptions.RemoveAll(x => sel.Contains(x, StringComparer.OrdinalIgnoreCase));
        Persist();
    }

    void Persist()
    {
        // Merge into the file as it is now so Mode/options changed from the tray meanwhile are kept.
        var cur = ConfigStore.Load();
        cur.Exceptions = _cfg.Exceptions;
        ConfigStore.Save(cur); // the tray app reloads the file within a second
        FillList();
        RefreshRunning();
    }

    void RefreshRunning()
    {
        var filter = _search.Text.Trim();
        var groups = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (Engine.Protected.Contains(name) || _cfg.Exceptions.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                groups[name] = (groups.TryGetValue(name, out var ws) ? ws : 0) + p.WorkingSet64;
            }
            catch { }
            finally { p.Dispose(); }
        }
        _running.BeginUpdate();
        _running.Items.Clear();
        foreach (var kv in groups.OrderByDescending(k => k.Value))
        {
            if (filter.Length > 0 && !kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListViewItem(kv.Key);
            item.SubItems.Add((kv.Value / (1024 * 1024)) + " MB");
            _running.Items.Add(item);
        }
        _running.EndUpdate();
    }
}
