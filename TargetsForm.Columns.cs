using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameRamCap;

/// <summary>Apps tab, part 3: column widths, the sideways-scroll hook, row height and the header menu items.</summary>
internal sealed partial class TargetsForm
{
    // ---- the user's view of the running list: which columns show, and how tall the rows are ----
    // Column shares (in %, of the visible columns) and the narrowest each column may get. The list always fills its width:
    // resize the window and every column follows; drag a header edge and the others give or take the room.
    static readonly int[] ColMin = { 120, 64, 56, 56, 72, 72, 56, 90 };
    static readonly (string Label, int Px)[] RowHeights = { ("Compact", 28), ("Comfortable", 36), ("Spacious", 44) };
    bool _layingOut;

    // Like Task Manager: every column keeps its own width and dragging a header edge changes only that column.
    // The last shown column (Status) takes whatever room is left, so the app-name column never moves when you change a column on its right.
    static readonly double[] ColPx = { 260, 80, 64, 64, 84, 84, 60, 120 };
    double[]? _px;

    double[] Widths()
    {
        if (_px != null) return _px;
        var saved = UiPrefs.Current.ColumnPx;
        _px = saved.Count == ColNames.Length && saved.Take(ColNames.Length - 1).All(v => v > 0) ? saved.ToArray() : (double[])ColPx.Clone();
        return _px;
    }

    bool IsHidden(int i) => i > 0 && (ColNames[i] == "Proc." || ColNames[i] == "Status" || UiPrefs.Current.HiddenColumns.Contains(ColNames[i]));   // "Proc." and "Status" are removed for good

    /// <summary>The column that takes the leftover width (Status; the one before it if Status is hidden).</summary>
    int LastShown()
    {
        for (int i = ColNames.Length - 1; i >= 1; i--) if (!IsHidden(i)) return i;
        return 0;
    }

    int Desired(int i) => Math.Max(Ui.S(this, ColMin[i]), Ui.S(this, (int)Widths()[i]));

    void LayoutColumns()
    {
        if (_layingOut || _running.ClientSize.Width <= 0) return;
        _layingOut = true;
        _running.BeginUpdate();
        try
        {
            int avail = _running.ClientSize.Width;
            int n = ColNames.Length;
            var w = new int[n];
            int sum = 0;
            for (int i = 0; i < n; i++)
            {
                if (IsHidden(i)) continue;
                w[i] = Desired(i);
                sum += w[i];
            }
            // Every column keeps the width it was given; when they add up to more than the list, the list scrolls sideways
            // (the header follows). Empty the filler first and shrink columns before growing others, so no stray scroll appears
            // while the columns are changed one by one.
            var filler = _running.Columns[n];
            if (filler.Width != 0) filler.Width = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    var col = _running.Columns[i];
                    if (IsHidden(i))
                    {
                        if (pass == 0) { col.Tag = "hidden"; if (col.Width != 0) col.Width = 0; }
                        continue;
                    }
                    col.Tag = null;
                    if (pass == 0 ? w[i] < col.Width : w[i] > col.Width) col.Width = w[i];
                }
            // the leftover room goes to an invisible filler column, so no real column changes when the window or another column does
            int fw = Math.Max(0, avail - sum);
            if (filler.Width != fw) filler.Width = fw;
        }
        finally { _running.EndUpdate(); _layingOut = false; _header?.Invalidate(); }
    }

    /// <summary>The user drags a column edge: only that column changes. If the columns end up wider than the list, it scrolls sideways.</summary>
    void OnHeaderResizing(int col, int wanted)
    {
        double scale = Math.Max(0.1, Ui.S(this, 1000) / 1000.0);
        int w = Math.Clamp(wanted, Ui.S(this, ColMin[col]), Ui.S(this, 1500));
        Widths()[col] = w / scale;
        LayoutColumns();
    }

    /// <summary>Tells us when the list is scrolled sideways, so the header can move with it.</summary>
    sealed class ScrollHook : NativeWindow
    {
        public Action? Scrolled;
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x114 /* WM_HSCROLL */ || m.Msg == 0x20E /* WM_MOUSEHWHEEL */ || m.Msg == 0x115 /* WM_VSCROLL */) Scrolled?.Invoke();
        }
    }
    readonly ScrollHook _hook = new();

    void AttachScrollHook()
    {
        if (!_running.IsHandleCreated) return;
        if (_hook.Handle != IntPtr.Zero) _hook.ReleaseHandle();
        _hook.AssignHandle(_running.Handle);
        _hook.Scrolled = () => _header?.Invalidate();
    }

    void ApplyColumns() => LayoutColumns();

    void SetRowHeight(int px)
    {
        UiPrefs.Current.RowHeight = px;
        var old = _running.SmallImageList;
        _running.SmallImageList = new ImageList { ImageSize = new Size(1, Ui.S(this, px)) };
        old?.Dispose();
    }

    void BuildViewItems(List<ToolStripItem> items)
    {
        var hidden = UiPrefs.Current.HiddenColumns;
        items.Add(new ToolStripMenuItem("Columns") { Enabled = false });
        for (int i = 1; i < ColNames.Length; i++)
        {
            string name = ColNames[i];
            if (name == "Proc." || name == "Status") continue;   // columns removed
            var it = new ToolStripMenuItem(name) { Checked = !hidden.Contains(name) };
            it.Click += (_, _) =>
            {
                if (!hidden.Remove(name)) hidden.Add(name);
                UiPrefs.Save();
                ApplyColumns();
            };
            items.Add(it);
        }
    }
}
