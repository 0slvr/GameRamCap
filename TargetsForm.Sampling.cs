using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameRamCap;

/// <summary>Apps tab, part 2: scanning the processes, the app icons and drawing the running list.</summary>
internal sealed partial class TargetsForm
{
    /// <summary>Scans the processes (RAM, CPU %, I/O per second) and redraws the list.</summary>
    bool _sampling;

    /// <summary>The heavy part (reading every process, GPU and network counters, app icons) runs on a background thread, so the window
    /// opens at once and never freezes; the list is then filled on the UI thread.</summary>
    async void RefreshRunning()
    {
        if (_sampling) return;
        _sampling = true;
        try { await Task.Run(Sample); }
        catch (Exception ex) { LogScanError("scan", ex); }
        finally { _sampling = false; }
        if (_closePdh) ClosePdh();   // the window was closed while the scan ran: release the counters now
        if (IsDisposed || Disposing) return;
        try { Render(); } catch (Exception ex) { LogScanError("list", ex); }
    }

    void LogScanError(string what, Exception ex)
    {
        if (_lastScanErr == ex.Message) return;   // log each different error once, not every 2 seconds
        _lastScanErr = ex.Message;
        Logger.Write($"Apps {what}: {ex.GetType().Name}: {ex.Message}");
    }

    void ClosePdh()
    {
        if (_pdhClosed) return;
        _pdhClosed = true;
        try { GpuUsage.Close(); } catch { }
        try { NetUsage.Close(); } catch { }
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

        // the process tree and the disk load are read here too, so Render (UI thread) only draws
        var map = Native.ProcessMap();
        map.Remove(0);
        _map = map;
        _diskPct = SystemLoad.DiskPercent();

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


    // ---- icons and heat map ----
    Bitmap IconFor(string app, int pid)
    {
        if (_icons.TryGetValue(app, out var cached)) return cached;
        var made = MakeIcon(app, pid);
        var won = _icons.GetOrAdd(app, made);   // the scan thread and the UI thread may race: keep one, dispose the other
        if (!ReferenceEquals(won, made)) made.Dispose();
        return won;
    }

    Bitmap MakeIcon(string app, int pid)
    {
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
                    using var src = ic.ToBitmap();
                    g.DrawImage(src, 0, 0, 16, 16);
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
            Pct(_diskPct),
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
        _diskBusy = _diskPct ?? 0;
        var selChildren = _running.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<ChildTag>().ToHashSet();
        object? focusTag = _running.FocusedItem?.Tag;
        int top = 0;
        try { top = _running.TopItem?.Index ?? 0; } catch { }

        var map = _map;   // from the last scan
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
}
