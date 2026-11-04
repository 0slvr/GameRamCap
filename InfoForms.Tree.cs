using System.Diagnostics;

namespace GameRamCap;

// Read-only windows opened from the Apps tab: Process tree.
internal static partial class InfoForms
{
    /// <summary>Parent / child view of every running process (Chrome and its sub-processes, a launcher and its game...).
    /// Selected apps (or the filter box) narrow it to the trees that contain them.</summary>
    public static void ShowTree(IWin32Window owner, List<string> names)
    {
        var tree = new TreeView { HideSelection = false, ShowLines = true, ShowRootLines = true, FullRowSelect = true };
        var search = new ModernTextBox { Width = 200, PlaceholderText = "Filter by name...", Margin = new Padding(12, 3, 3, 3) };
        var noteRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        noteRow.Controls.Add(Ui.Note("Parent and child processes with their RAM. Grey = protected (never touched by GameRamCap). Read-only."));
        noteRow.Controls.Add(search);
        using var f = Shell("Process tree", new Size(780, 560), tree, out var buttons, noteRow);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var all = new Button { Text = "Show all", AutoSize = true };
        buttons.Controls.Add(close);
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(all);
        f.CancelButton = close;
        Ui.Polish(f);

        bool useNames = names.Count > 0;
        void Fill()
        {
            var map = Native.ProcessMap();
            map.Remove(0);
            var kids = new Dictionary<int, List<int>>();
            foreach (var kv in map)
            {
                int par = kv.Value.Parent;
                if (par == kv.Key || !map.ContainsKey(par)) continue;
                if (!kids.TryGetValue(par, out var l)) kids[par] = l = new List<int>();
                l.Add(kv.Key);
            }
            var roots = map.Keys.Where(pid => { int par = map[pid].Parent; return par == pid || !map.ContainsKey(par); })
                .OrderBy(pid => map[pid].Name, StringComparer.OrdinalIgnoreCase).ThenBy(pid => pid).ToList();

            string text = search.Text.Trim();
            bool Match(int pid)
            {
                string n = map[pid].Name;
                if (text.Length > 0) return n.Contains(text, StringComparison.OrdinalIgnoreCase);
                return !useNames || names.Contains(n, StringComparer.OrdinalIgnoreCase);
            }
            bool Has(int pid, int depth)
            {
                if (Match(pid)) return true;
                if (depth > 64 || !kids.TryGetValue(pid, out var l)) return false;
                return l.Any(c => Has(c, depth + 1));
            }
            TreeNode Build(int pid, int depth, out long total)
            {
                string name = map[pid].Name;
                long own = Native.GetMemory(pid, out long ws, out _) ? ws : 0;
                long sum = own;
                var node = new TreeNode();
                if (depth < 64 && kids.TryGetValue(pid, out var l))
                {
                    foreach (var c in l.OrderBy(x => map[x].Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x))
                    {
                        node.Nodes.Add(Build(c, depth + 1, out long t));
                        sum += t;
                    }
                }
                node.Text = $"{name}  [{pid}]  {Mb(own)}" + (node.Nodes.Count > 0 ? $"   (with children: {Mb(sum)})" : "");
                if (Engine.Protected.Contains(name)) node.ForeColor = Ui.Muted;
                total = sum;
                return node;
            }

            tree.BeginUpdate();
            tree.Nodes.Clear();
            foreach (int r in roots)
                if (Has(r, 0)) tree.Nodes.Add(Build(r, 0, out _));
            tree.ExpandAll();
            tree.EndUpdate();
            if (tree.Nodes.Count > 0) tree.Nodes[0].EnsureVisible();
        }
        refresh.Click += (_, _) => Fill();
        all.Click += (_, _) => { useNames = false; search.Clear(); Fill(); };
        search.TextChanged += (_, _) => Fill();
        Fill();
        f.ShowDialog(owner);
    }
}
