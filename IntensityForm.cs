using System.Globalization;

namespace GameRamCap;

/// <summary>Lets the user edit the numbers behind the five intensity levels.</summary>
internal sealed class IntensityForm : Form
{
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
    };

    static readonly (string Name, string Header, string Tip)[] Cols =
    {
        ("Scan",        "Check every (s)",     "How often the RAM state is checked."),
        ("HoldMB",      "Ceiling per app (MB)","Hard RAM ceiling for a whole background app, all its processes together."),
        ("Cooldown",    "Cooldown (s)",        "Minimum seconds between two trims of the same app."),
        ("Grace",       "Grace (s)",           "An app you just left stays untouched for this long."),
        ("BudgetScale", "Budget scale",        "Multiplies each app's budget (lower = squeeze more)."),
        ("MinProcMB",   "Min process (MB)",    "Processes smaller than this are ignored."),
        ("CritFactor",  "Critical factor",     "Budget multiplier when RAM is critical (0.1 - 1)."),
    };

    public IntensityForm()
    {
        Text = "GameRamCap - edit intensity levels";
        ClientSize = new Size(900, 300);
        MinimumSize = new Size(760, 260);
        StartPosition = FormStartPosition.CenterScreen;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "Level", ReadOnly = true, FillWeight = 120 });
        foreach (var c in Cols)
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = c.Name, HeaderText = c.Header, ToolTipText = c.Tip, FillWeight = 80 });

        var hint = new Label
        {
            Text = "Edit any number, then Save. Changes apply within a second. Pick the level from the tray: Intensity.",
            AutoSize = true, Margin = new Padding(3, 3, 3, 8),
        };
        var save = new Button { Text = "Save", AutoSize = true };
        var reset = new Button { Text = "Reset all to defaults", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(close);
        buttons.Controls.Add(save);
        buttons.Controls.Add(reset);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(hint, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        Fill(ConfigStore.Load(), useDefaults: false);

        save.Click += (_, _) => Save();
        reset.Click += (_, _) => Fill(new Config(), useDefaults: true);
        close.Click += (_, _) => Close();
    }

    static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    void Fill(Config cfg, bool useDefaults)
    {
        _grid.Rows.Clear();
        for (int i = 0; i < Presets.Names.Length; i++)
        {
            string name = Presets.Names[i];
            Preset p;
            if (useDefaults) p = Presets.Default(name);
            else
            {
                var tmp = new Config { Profile = name, PresetOverrides = cfg.PresetOverrides };
                p = Presets.Resolve(tmp);
            }
            int row = _grid.Rows.Add(Presets.Labels[i], Fmt(p.Scan), p.HoldMB, Fmt(p.Cooldown), Fmt(p.Grace),
                                     Fmt(p.BudgetScale), p.MinProcMB, Fmt(p.CritFactor));
            _grid.Rows[row].Tag = name;
        }
    }

    static double Num(object? v, double fallback)
    {
        var t = (Convert.ToString(v) ?? "").Trim().Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : fallback;
    }

    static bool Differs(double a, double b) => Math.Abs(a - b) > 0.0001;

    void Save()
    {
        _grid.EndEdit();
        var cur = ConfigStore.Load(); // keep everything else (mode, targets...) as it is now
        cur.PresetOverrides = new Dictionary<string, PresetOverride>(StringComparer.OrdinalIgnoreCase);

        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.Tag is not string name) continue;
            var d = Presets.Default(name);
            var o = new PresetOverride();
            double scan = Math.Max(0.5, Num(r.Cells["Scan"].Value, d.Scan));
            int hold = (int)Math.Max(1, Num(r.Cells["HoldMB"].Value, d.HoldMB));
            double cool = Num(r.Cells["Cooldown"].Value, d.Cooldown);
            double grace = Num(r.Cells["Grace"].Value, d.Grace);
            double scale = Math.Clamp(Num(r.Cells["BudgetScale"].Value, d.BudgetScale), 0.1, 10);
            int minp = (int)Math.Max(1, Num(r.Cells["MinProcMB"].Value, d.MinProcMB));
            double crit = Math.Clamp(Num(r.Cells["CritFactor"].Value, d.CritFactor), 0.1, 1);

            if (Differs(scan, d.Scan)) o.Scan = scan;
            if (hold != d.HoldMB) o.HoldMB = hold;
            if (Differs(cool, d.Cooldown)) o.Cooldown = cool;
            if (Differs(grace, d.Grace)) o.Grace = grace;
            if (Differs(scale, d.BudgetScale)) o.BudgetScale = scale;
            if (minp != d.MinProcMB) o.MinProcMB = minp;
            if (Differs(crit, d.CritFactor)) o.CritFactor = crit;

            bool any = o.Scan != null || o.HoldMB != null || o.Cooldown != null || o.Grace != null
                    || o.BudgetScale != null || o.MinProcMB != null || o.CritFactor != null;
            if (any) cur.PresetOverrides[name] = o;
        }
        ConfigStore.Save(cur); // the tray app reloads the file within a second

        // show the cleaned-up values back
        Fill(cur, useDefaults: false);
        MessageBox.Show(this, "Saved. The new levels apply within a second.", "GameRamCap",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
