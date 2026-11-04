using System.Diagnostics;

namespace GameRamCap;

/// <summary>Small read-only windows opened from the Apps tab: History (RAM / CPU of the last 60 minutes) and Connections.</summary>
internal static partial class InfoForms
{
    static Form Shell(string title, Size size, Control list, out FlowLayoutPanel buttons, Control? note)
    {
        var f = new Form
        {
            Text = title, ClientSize = size, MinimumSize = new Size(560, 320), StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false, Font = Ui.Base,
        };
        list.Dock = DockStyle.Fill;
        buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = Ui.PagePadding };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(note ?? new Label { AutoSize = true }, 0, 0);
        root.Controls.Add(list, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        f.Controls.Add(root);
        return f;
    }

    static string Mb(long bytes) => (bytes / (1024 * 1024)) + " MB";

    public static void ShowHistory(IWin32Window owner)
    {
        var list = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false };
        list.Columns.Add("App", 150);
        list.Columns.Add("RAM now", 80);
        list.Columns.Add("RAM peak", 85);
        list.Columns.Add("Change (10 min)", 110);
        list.Columns.Add("CPU avg", 70);
        list.Columns.Add("CPU peak", 75);
        list.Columns.Add("Watched", 70);
        var note = Ui.Note("RAM and CPU of every app, last 60 minutes (kept in memory; it starts empty each time GameRamCap starts). " +
                           "Amber = grew by 300 MB or more in 10 minutes: look for a memory leak or a runaway app.");
        using var f = Shell("History", new Size(760, 480), list, out var buttons, note);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        buttons.Controls.Add(close);
        buttons.Controls.Add(refresh);
        f.CancelButton = close;
        Ui.Polish(f);

        void Fill()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var s in UsageMonitor.Snapshot().OrderByDescending(x => x.Peak).Take(300))
            {
                var item = new ListViewItem(s.Name);
                item.SubItems.Add(Mb(s.Ram));
                item.SubItems.Add(Mb(s.Peak));
                long dMb = s.Delta10 / (1024 * 1024);
                item.SubItems.Add((dMb > 0 ? "+" : "") + dMb + " MB");
                item.SubItems.Add(s.CpuAvg.ToString("0.0") + "%");
                item.SubItems.Add(s.CpuPeak.ToString("0.0") + "%");
                item.SubItems.Add(s.Minutes + " min");
                if (dMb >= 300) item.ForeColor = Ui.Warn;
                list.Items.Add(item);
            }
            list.EndUpdate();
        }
        refresh.Click += (_, _) => Fill();
        Fill();
        f.ShowDialog(owner);
    }

    public static void ShowConnections(IWin32Window owner, List<string> names)
    {
        var list = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false };
        list.Columns.Add("App", 120);
        list.Columns.Add("PID", 55);
        list.Columns.Add("Proto", 50);
        list.Columns.Add("Local", 140);
        list.Columns.Add("Remote", 150);
        list.Columns.Add("State", 90);
        var note = Ui.Note("Open IPv4 connections of " + string.Join(", ", names) + ". This shows who is connected to where; " +
                           "Windows has no per-app traffic speed without extra tracing.");
        using var f = Shell("Connections", new Size(760, 460), list, out var buttons, note);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        buttons.Controls.Add(close);
        buttons.Controls.Add(refresh);
        f.CancelButton = close;
        Ui.Polish(f);

        void Fill()
        {
            var byPid = new Dictionary<int, string>();
            foreach (var n in names)
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try { byPid[p.Id] = n; }
                    finally { p.Dispose(); }
                }
            var conns = NetTable.For(new HashSet<int>(byPid.Keys));
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var c in conns.OrderBy(x => byPid[x.Pid], StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Proto).ThenBy(x => x.State))
            {
                var item = new ListViewItem(byPid[c.Pid]);
                item.SubItems.Add(c.Pid.ToString());
                item.SubItems.Add(c.Proto);
                item.SubItems.Add(c.Local);
                item.SubItems.Add(c.Remote);
                item.SubItems.Add(c.State);
                list.Items.Add(item);
            }
            list.EndUpdate();
            if (conns.Count == 0) list.Items.Add(new ListViewItem("(no open connections)"));
        }
        refresh.Click += (_, _) => Fill();
        Fill();
        f.ShowDialog(owner);
    }
}
