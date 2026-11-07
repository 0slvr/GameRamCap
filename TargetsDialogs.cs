namespace GameRamCap;

/// <summary>"CPU cores": tick the logical cores an app may run on.</summary>
internal sealed class CoresDialog : Form
{
    readonly List<ModernCheck> _boxes = new();
    readonly ModernCheck _remember;

    public long Mask { get; private set; }
    public bool Remember => _remember.Checked;

    public CoresDialog(List<string> names, long start)
    {
        int count = Math.Clamp(Environment.ProcessorCount, 1, 63);
        Icon = AppIcon.Window; Text = "CPU cores"; ClientSize = new Size(460, 120); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; Font = Ui.Base;

        var note = Ui.Note("Tick the cores " + string.Join(", ", names) + " may run on. At least one.");
        var grid = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(430, 0), WrapContents = true };
        for (int i = 0; i < count; i++)
        {
            var cb = new ModernCheck { Text = i.ToString(), AutoSize = true, Checked = (start & (1L << i)) != 0, Margin = new Padding(3, 3, 12, 3) };
            _boxes.Add(cb);
            grid.Controls.Add(cb);
        }
        var allBtn = new ModernButton { Text = "All cores", AutoSize = true };
        allBtn.Click += (_, _) => _boxes.ForEach(b => b.Checked = true);
        _remember = new ModernCheck { Text = "Always use these cores for this app", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        var ok = new ModernButton { Text = "Apply", AutoSize = true, Tag = "primary", DialogResult = DialogResult.OK };
        var cancel = new ModernButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;
        var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        btns.Controls.Add(ok);
        btns.Controls.Add(cancel);
        btns.Controls.Add(allBtn);
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16), AutoSize = true };
        root.Controls.Add(note);
        root.Controls.Add(grid);
        root.Controls.Add(_remember);
        root.Controls.Add(btns);
        Controls.Add(root);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Ui.Polish(this);

        ok.Click += (_, _) =>
        {
            if (!_boxes.Any(b => b.Checked))
            {
                MessageBox.Show(this, "Tick at least one core.", "CPU cores", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;   // stay open
                return;
            }
            long m = 0;
            for (int i = 0; i < _boxes.Count; i++) if (_boxes[i].Checked) m |= 1L << i;
            Mask = m;
        };
    }
}

/// <summary>"Start new task": type a program, folder, document or address, or browse for it.</summary>
internal sealed class StartTaskDialog : Form
{
    readonly ModernTextBox _box = new() { Width = 380 };

    public string Command => _box.Text.Trim();

    public StartTaskDialog()
    {
        Icon = AppIcon.Window; Text = "Start new task"; ClientSize = new Size(500, 150); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; Font = Ui.Base;

        var note = Ui.Note("Type the name of a program, folder, document or web address and Windows will open it (without administrator rights).");
        var browse = new ModernButton { Text = "Browse...", AutoSize = true };
        var ok = new ModernButton { Text = "Start", AutoSize = true, Tag = "primary", DialogResult = DialogResult.OK };
        var cancel = new ModernButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK) _box.Text = d.FileName;
        };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(_box);
        row.Controls.Add(browse);
        var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        btns.Controls.Add(ok);
        btns.Controls.Add(cancel);
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16) };
        root.Controls.Add(note);
        root.Controls.Add(row);
        root.Controls.Add(btns);
        Controls.Add(root);
        Ui.Polish(this);
    }
}
