using System.Drawing.Drawing2D;

namespace GameRamCap;

/// <summary>A rounded panel that groups related settings (top-to-bottom flow).</summary>
internal sealed class Card : FlowLayoutPanel
{
    public Card()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(22, 18, 22, 18);
        Margin = new Padding(3, 0, 3, 16);
        MinimumSize = new Size(Ui.ContentWidth + 48, 0);
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Bg);
        if (Width < 4 || Height < 4) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Ui.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(this, 14));
        using var fill = new SolidBrush(Ui.Surface);
        using var pen = new Pen(Ui.Border);
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
    }
}

/// <summary>Shared drawing for the check box and the radio button.</summary>
internal static class ModernGlyph
{
    const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                                  TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

    /// <summary>Width of the glyph: the radio dot is round, the check box is drawn as a toggle switch.</summary>
    static int GlyphWidth(ButtonBase b) => b is ModernCheck ? Ui.S(b, 38) : Ui.S(b, 20);

    public static Size Measure(ButtonBase b)
    {
        int box = GlyphWidth(b), gap = Ui.S(b, 10);
        var s = TextRenderer.MeasureText(b.Text, b.Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(box + gap + s.Width + 6, Math.Max(Ui.S(b, 20), s.Height) + 8);
    }

    public static void Paint(ButtonBase b, Graphics g, bool radio, bool on, bool hover)
    {
        g.Clear(b.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool enabled = b.Enabled;
        int box = GlyphWidth(b), gap = Ui.S(b, 10);
        int h = Ui.S(b, 20);
        var r = new Rectangle(1, (b.Height - h) / 2, box - 2, h - 2);
        Color accent = enabled ? Ui.Accent : Color.FromArgb(110, Ui.Muted);
        Color edge = enabled ? (hover || b.Focused ? Ui.Accent : Ui.Muted) : Color.FromArgb(110, Ui.Muted);

        if (!radio)
        {
            // toggle switch: pill track + round knob
            using (var track = Ui.RoundRect(r, r.Height / 2))
            {
                if (on)
                {
                    using var fill = new SolidBrush(hover && enabled ? Ui.AccentHover : accent);
                    g.FillPath(fill, track);
                }
                else
                {
                    using var fill = new SolidBrush(Ui.Input);
                    using var pen = new Pen(edge, 1.4f);
                    g.FillPath(fill, track);
                    g.DrawPath(pen, track);
                }
            }
            int k = r.Height - Ui.S(b, 6);
            int kx = on ? r.Right - k - Ui.S(b, 3) : r.X + Ui.S(b, 3);
            using (var knob = new SolidBrush(on ? Color.White : (enabled ? Ui.Muted : Color.FromArgb(110, Ui.Muted))))
                g.FillEllipse(knob, kx, r.Y + (r.Height - k) / 2, k, k);
            TextRenderer.DrawText(g, b.Text, b.Font, new Rectangle(box + gap, 0, Math.Max(1, b.Width - box - gap), b.Height),
                enabled ? Ui.Text : Ui.Muted, Flags);
            return;
        }

        r = new Rectangle(1, (b.Height - Ui.S(b, 18)) / 2, Ui.S(b, 18) - 2, Ui.S(b, 18) - 2);
        box = Ui.S(b, 20);

        using (var fill = new SolidBrush(Ui.Input)) g.FillEllipse(fill, r);
        using (var pen = new Pen(on ? accent : edge, 1.6f)) g.DrawEllipse(pen, r);
        if (on)
        {
            var dot = Rectangle.Inflate(r, -Ui.S(b, 4), -Ui.S(b, 4));
            using var d = new SolidBrush(accent);
            g.FillEllipse(d, dot);
        }

        TextRenderer.DrawText(g, b.Text, b.Font, new Rectangle(box + gap, 0, Math.Max(1, b.Width - box - gap), b.Height),
            enabled ? Ui.Text : Ui.Muted, Flags);
    }
}

/// <summary>Rounded flat button. "primary" (Tag) = filled with the accent colour.</summary>
internal sealed class ModernButton : Button
{
    bool _hover, _down;

    /// <summary>Optional icon (a code from Windows' icon font) drawn before the text.</summary>
    public string? Glyph { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = base.GetPreferredSize(proposedSize);
        return Glyph != null && Ui.Icons != null ? new Size(s.Width + Ui.S(this, 26), s.Height) : s;
    }

    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Bg);
        if (Width < 4 || Height < 4) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool primary = (Tag as string) == "primary";
        bool ghost = (Tag as string) == "ghost";
        Color fill, edge, fore;
        if (ghost)
        {
            fill = !Enabled ? Parent?.BackColor ?? Ui.Bg : _down ? Ui.Border : _hover ? Ui.Hover : Parent?.BackColor ?? Ui.Bg;
            edge = Focused ? Ui.Accent : fill;
            fore = Enabled ? Ui.Text : Ui.Muted;
        }
        else if (primary)
        {
            fill = !Enabled ? Color.FromArgb(110, Ui.Accent) : _down ? Ui.AccentDown : _hover ? Ui.AccentHover : Ui.Accent;
            edge = fill;
            fore = Color.White;
        }
        else
        {
            fill = !Enabled ? Ui.Input : _down ? Ui.Border : _hover ? Ui.Hover : Ui.Input;
            edge = Focused ? Ui.Accent : Ui.Border;
            fore = Enabled ? Ui.Text : Ui.Muted;
        }

        using (var path = Ui.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(this, 9)))
        using (var br = new SolidBrush(fill))
        using (var pen = new Pen(edge))
        {
            g.FillPath(br, path);
            g.DrawPath(pen, path);
        }

        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                                      TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        if (Glyph != null && Ui.Icons != null)
        {
            int iconW = Ui.S(this, 18), gap = Ui.S(this, 8);
            int textW = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), flags).Width;
            int x = Math.Max(Ui.S(this, 8), (Width - (iconW + gap + textW)) / 2);
            TextRenderer.DrawText(g, Glyph, Ui.Icons, new Rectangle(x, 0, iconW + 4, Height), primary ? fore : (ghost ? Ui.Accent : fore),
                TextFormatFlags.Left | flags);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + iconW + gap, 0, Math.Max(1, Width - x - iconW - gap), Height), fore,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | flags);
            return;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Our own column header for a list: big live total above each column name (like Task Manager),
/// click to sort, drag the edge of a column to resize. The list's own header is switched off.</summary>
internal sealed class ModernListHeader : Control
{
    readonly ListView _lv;
    static readonly Font Stat = new("Segoe UI Semibold", 12.5f);
    int _hover = -1, _drag = -1, _press = -1, _dragX, _dragW;

    /// <summary>Text shown big above the column name; null = none.</summary>
    public string?[] Summaries { get; set; } = Array.Empty<string?>();
    public event Action<int>? ColumnClicked;
    public event Action<int, int>? ColumnResizing;   // column, wanted width in px
    public event Action? ResizeDone;
    /// <summary>Press a header (not its edge) and drag sideways to move that column; the first column and the filler stay put.</summary>
    public bool AllowReorder { get; set; }
    public event Action? ColumnsReordered;
    int _reorder = -1, _pressX;
    bool _moved;

    public ModernListHeader(ListView lv)
    {
        _lv = lv;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 52;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = Ui.S(this, 52);
    }

    /// <summary>Where each column sits, as the list really paints it: the first row's cells give the position (so the header follows
    /// sideways scrolling and any reordering), with the plain widths in display order as the fallback.</summary>
    Rectangle[] Cells()
    {
        int n = _lv.Columns.Count;
        var r = new Rectangle[n];
        int x = 0;
        foreach (int i in Enumerable.Range(0, n).OrderBy(i => _lv.Columns[i].DisplayIndex))
        {
            int w = _lv.Columns[i].Width;
            r[i] = new Rectangle(x, 0, w, Height);
            x += w;
        }
        if (n > 1 && _lv.IsHandleCreated && _lv.Items.Count > 0 && _lv.Items[0].SubItems.Count >= n)
        {
            try
            {
                var subs = _lv.Items[0].SubItems;
                int minX = int.MaxValue;
                for (int i = 1; i < n; i++)
                {
                    if (!Shown(i)) continue;
                    var bd = subs[i].Bounds;
                    if (bd.Width <= 0) continue;
                    r[i] = new Rectangle(bd.X, 0, bd.Width, Height);
                    minX = Math.Min(minX, bd.X);
                }
                if (minX != int.MaxValue) r[0] = new Rectangle(minX - _lv.Columns[0].Width, 0, _lv.Columns[0].Width, Height);
            }
            catch { }
        }
        return r;
    }

    bool Shown(int i) => _lv.Columns[i].Width > 0 && (_lv.Columns[i].Tag as string) is not ("hidden" or "filler");

    /// <summary>An invisible last column that only soaks up the leftover width: then every real column may be dragged.</summary>
    bool HasFiller => _lv.Columns.Count > 0 && (_lv.Columns[^1].Tag as string) == "filler";

    int LastShown()
    {
        for (int i = _lv.Columns.Count - 1; i >= 0; i--) if (Shown(i)) return i;
        return -1;
    }

    Rectangle Cell(int i) => Cells()[i];

    int CellAt(Point p)
    {
        var cells = Cells();
        for (int i = 0; i < _lv.Columns.Count; i++) if (Shown(i) && cells[i].Contains(p)) return i;
        return -1;
    }

    int EdgeAt(Point p)
    {
        int last = HasFiller ? -1 : LastShown();
        for (int i = 0; i < _lv.Columns.Count; i++)
            if (Shown(i) && i != last && Math.Abs(p.X - Cell(i).Right) <= Ui.S(this, 4)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Surface);
        int pad = Ui.S(this, 10);
        var cells = Cells();
        for (int i = 0; i < _lv.Columns.Count; i++)
        {
            if (!Shown(i)) continue;
            var c = cells[i];
            if (i == _hover)
                using (var hb = new SolidBrush(Ui.Hover)) g.FillRectangle(hb, c);
            bool right = i > 0 && _lv.Columns[i].TextAlign == HorizontalAlignment.Right;
            var half = c.Height / 2;
            var top = new Rectangle(c.X + pad, c.Y + Ui.S(this, 4), Math.Max(1, c.Width - 2 * pad), half - Ui.S(this, 2));
            var bottom = new Rectangle(c.X + pad, c.Y + half - Ui.S(this, 2), Math.Max(1, c.Width - 2 * pad), c.Height - half - Ui.S(this, 2));
            var align = (right ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            string? sum = i < Summaries.Length ? Summaries[i] : null;
            if (sum != null) TextRenderer.DrawText(g, sum, Stat, top, Ui.Text, align);
            TextRenderer.DrawText(g, _lv.Columns[i].Text, Ui.Base, bottom, Ui.Muted, align);
            using var sep = new Pen(Color.FromArgb(90, Ui.Border));
            g.DrawLine(sep, c.Right - 1, Ui.S(this, 8), c.Right - 1, Height - Ui.S(this, 8));
        }
        using var line = new Pen(Ui.Border);
        g.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag >= 0)
        {
            ColumnResizing?.Invoke(_drag, Math.Max(1, _dragW + e.X - _dragX));
            return;
        }
        if (AllowReorder && _press >= 1 && _reorder < 0 && (e.Button & MouseButtons.Left) != 0 && Math.Abs(e.X - _pressX) > Ui.S(this, 6))
        {
            _reorder = _press; _moved = false; _hover = _reorder; Cursor = Cursors.SizeAll;
        }
        if (_reorder >= 0)
        {
            var cells = Cells();
            for (int j = 1; j < _lv.Columns.Count; j++)
            {
                if (j == _reorder || !Shown(j) || !cells[j].Contains(e.X, cells[j].Y + 1)) continue;
                bool right = cells[j].Left > cells[_reorder].Left;
                int mid = cells[j].Left + cells[j].Width / 2;
                if ((right && e.X >= mid) || (!right && e.X <= mid))
                {
                    _lv.Columns[_reorder].DisplayIndex = _lv.Columns[j].DisplayIndex;
                    _moved = true;
                    _lv.Invalidate();
                    Invalidate();
                }
                break;
            }
            return;
        }
        Cursor = EdgeAt(e.Location) >= 0 ? Cursors.VSplit : Cursors.Default;
        int h = CellAt(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int edge = EdgeAt(e.Location);
            if (edge >= 0) { _drag = edge; _dragX = e.X; _dragW = _lv.Columns[edge].Width; Capture = true; }
            else { _press = CellAt(e.Location); _pressX = e.X; }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag >= 0) { _drag = -1; Capture = false; ResizeDone?.Invoke(); }
        else if (_reorder >= 0)
        {
            _reorder = -1; Cursor = Cursors.Default;
            if (_moved) ColumnsReordered?.Invoke();
            Invalidate();
        }
        else if (_press >= 0 && _press == CellAt(e.Location)) ColumnClicked?.Invoke(_press);
        _press = -1;
        base.OnMouseUp(e);
    }
}

internal sealed class ModernCheck : CheckBox
{
    bool _hover;

    public ModernCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public override Size GetPreferredSize(Size proposedSize) => ModernGlyph.Measure(this);
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e) => ModernGlyph.Paint(this, e.Graphics, false, Checked, _hover);
}

internal sealed class ModernRadio : RadioButton
{
    bool _hover;

    public ModernRadio()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public override Size GetPreferredSize(Size proposedSize) => ModernGlyph.Measure(this);
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e) => ModernGlyph.Paint(this, e.Graphics, true, Checked, _hover);
}

/// <summary>Tabs with a flat header (accent underline on the selected tab) instead of the old Windows tab strip.
/// Same API the window needs from a TabControl: TabPages, SelectedTab, SelectedIndexChanged.</summary>
internal sealed class ModernTabs : Control
{
    /// <summary>A TabControl whose own tab strip is hidden: TCM_ADJUSTRECT makes the page fill the whole control.</summary>
    sealed class NoHeaderTabControl : TabControl
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x1328 && !DesignMode) { m.Result = (IntPtr)1; return; }
            base.WndProc(ref m);
        }
    }

    sealed class Strip : Control
    {
        readonly ModernTabs _owner;
        readonly List<Rectangle> _rects = new();
        int _hover = -1;

        public Strip(ModernTabs owner)
        {
            _owner = owner;
            Dock = DockStyle.Top;
            Height = owner._compact ? 40 : 64;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Height = Ui.S(this, _owner._compact ? 40 : 64);
        }

        Font TabFont => _owner._compact ? Ui.Base : Ui.TabFont;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            _rects.Clear();
            var pages = _owner._inner.TabPages;
            int sel = _owner._inner.SelectedIndex;
            if (pages.Count == 0) return;

            // a centred segmented control: one rounded container, the selected tab is a filled pill inside it
            int pad = Ui.S(this, 4);
            int barH = Ui.S(this, _owner._compact ? 32 : 40);
            int n = _owner.PinLast ? pages.Count - 1 : pages.Count;
            var widths = new int[Math.Max(0, n)];
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                var sz = TextRenderer.MeasureText(pages[i].Text, TabFont, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                widths[i] = sz.Width + Ui.S(this, 40);
                total += widths[i];
            }
            var bar = new Rectangle((Width - total - 2 * pad) / 2, (Height - barH) / 2, total + 2 * pad, barH);
            using (var path = Ui.RoundRect(bar, barH / 2))
            using (var fill = new SolidBrush(Ui.Surface))
            using (var pen = new Pen(Ui.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            int x = bar.X + pad;
            for (int i = 0; i < n; i++)
            {
                var r = new Rectangle(x, bar.Y + pad, widths[i], barH - 2 * pad);
                _rects.Add(r);
                bool on = i == sel;
                if (on || i == _hover)
                {
                    using var path = Ui.RoundRect(r, r.Height / 2);
                    using var pill = new SolidBrush(on ? Ui.Accent : Ui.Hover);
                    g.FillPath(pill, path);
                }
                TextRenderer.DrawText(g, pages[i].Text, TabFont, r, on ? Color.White : Ui.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                x += widths[i];
            }

            // the last page (Settings) is a separate button at the right end of the top bar
            if (_owner.PinLast)
            {
                int idx = pages.Count - 1;
                string label = "\u2699  " + pages[idx].Text;
                var lsz = TextRenderer.MeasureText(label, TabFont, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int bw = lsz.Width + Ui.S(this, 40);
                var br = new Rectangle(Width - bw - Ui.S(this, 24), bar.Y, bw, barH);
                _rects.Add(br);
                bool pinOn = idx == sel;
                using var bpath = Ui.RoundRect(br, barH / 2);
                using var bfill = new SolidBrush(pinOn ? Ui.Accent : (idx == _hover ? Ui.Hover : Ui.Surface));
                using var bpen = new Pen(pinOn ? Ui.Accent : Ui.Border);
                g.FillPath(bfill, bpath);
                g.DrawPath(bpen, bpath);
                TextRenderer.DrawText(g, label, TabFont, br, pinOn ? Color.White : Ui.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < _rects.Count; i++) if (_rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover != -1) { _hover = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int i = HitTest(e.Location);
                if (i >= 0 && i < _owner._inner.TabPages.Count) _owner._inner.SelectedIndex = i;
            }
            base.OnMouseDown(e);
        }
    }

    readonly NoHeaderTabControl _inner = new() { Dock = DockStyle.Fill };
    readonly Strip _strip;
    readonly bool _compact;

    public ModernTabs(bool compact)
    {
        _compact = compact;
        _strip = new Strip(this);
        Controls.Add(_inner);    // fill first, then the strip on top
        Controls.Add(_strip);
        _inner.SelectedIndexChanged += (_, e) => { _strip.Invalidate(); SelectedIndexChanged?.Invoke(this, e); };
        _inner.ControlAdded += (_, _) => _strip.Invalidate();
        _inner.ControlRemoved += (_, _) => _strip.Invalidate();
    }

    /// <summary>True: the last page is shown as its own button at the right end of the bar, apart from the other tabs.</summary>
    public bool PinLast { get; set; }

    public TabControl.TabPageCollection TabPages => _inner.TabPages;

    public TabPage? SelectedTab
    {
        get => _inner.SelectedTab;
        set => _inner.SelectedTab = value;
    }

    public event EventHandler? SelectedIndexChanged;
}

/// <summary>Text box whose border follows the theme (the native one is bright white in dark mode).</summary>
internal sealed class ModernTextBox : TextBox
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x85 /* WM_NCPAINT */ && BorderStyle == BorderStyle.FixedSingle && Width > 2 && Height > 2)
        {
            IntPtr dc = GetWindowDC(Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                using var g = Graphics.FromHdc(dc);
                using var pen = new Pen(Ui.Border);
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
            finally { ReleaseDC(Handle, dc); }
        }
    }
}

/// <summary>Number box with a theme-coloured border.</summary>
internal sealed class ModernNumeric : NumericUpDown
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 3 || Height < 3) return;
        using var pen = new Pen(Ui.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>A flat slider with a fixed number of labelled stops (click, drag, or use the arrow keys).
/// The value is committed when the mouse is released, so dragging across stops does not fire a change per stop.</summary>
internal sealed class ModernSteps : Control
{
    string[] _labels = Array.Empty<string>();
    int _value;
    int _drag = -1;      // stop under the mouse while dragging, -1 = not dragging
    bool _hover;

    public event EventHandler? ValueChanged;

    public ModernSteps()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public string[] Labels
    {
        get => _labels;
        set { _labels = value ?? Array.Empty<string>(); Invalidate(); }
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, 0, Math.Max(0, _labels.Length - 1));
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    float PadX => Ui.S(this, 56);
    float TrackY => Ui.S(this, 20);
    float StopX(int i) => _labels.Length <= 1 ? Width / 2f : PadX + (Width - 2 * PadX) * i / (_labels.Length - 1);

    int Nearest(int x)
    {
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < _labels.Length; i++)
        {
            float d = Math.Abs(x - StopX(i));
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left && _labels.Length > 0) { _drag = Nearest(e.X); Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag >= 0 && _labels.Length > 0)
        {
            int n = Nearest(e.X);
            if (n != _drag) { _drag = n; Invalidate(); }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag >= 0)
        {
            int v = _drag;
            _drag = -1;
            Invalidate();
            Value = v;
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Down: Value = _value - 1; e.Handled = true; break;
            case Keys.Right: case Keys.Up: Value = _value + 1; e.Handled = true; break;
            case Keys.Home: Value = 0; e.Handled = true; break;
            case Keys.End: Value = _labels.Length - 1; e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Surface);
        int n = _labels.Length;
        if (n == 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int shown = _drag >= 0 ? _drag : _value;
        int last = n - 1;
        float y = TrackY;
        int th = Math.Max(3, Ui.S(this, 4));
        float x0 = StopX(0), x1 = StopX(last), xs = StopX(shown);

        // track, and the filled part up to the thumb (the last stop, Custom, is not "stronger" so it has no fill)
        var track = new Rectangle((int)x0, (int)(y - th / 2f), Math.Max(1, (int)(x1 - x0)), th);
        using (var path = Ui.RoundRect(track, th / 2))
        using (var br = new SolidBrush(Ui.Border)) g.FillPath(br, path);
        if (shown < last && xs > x0)
        {
            var fill = new Rectangle((int)x0, track.Y, Math.Max(1, (int)(xs - x0)), th);
            using var path = Ui.RoundRect(fill, th / 2);
            using var br = new SolidBrush(Ui.Accent);
            g.FillPath(br, path);
        }

        // stops
        float r = Ui.S(this, 4);
        for (int i = 0; i < n; i++)
        {
            bool filled = shown < last && i <= shown;
            using var br = new SolidBrush(filled ? Ui.Accent : Ui.Muted);
            g.FillEllipse(br, StopX(i) - r, y - r, 2 * r, 2 * r);
        }

        // thumb (+ a soft ring on hover / keyboard focus)
        float tr = Ui.S(this, 9);
        if (_hover || Focused || _drag >= 0)
        {
            float gr = Ui.S(this, 14);
            using var glow = new SolidBrush(Color.FromArgb(60, Ui.Accent));
            g.FillEllipse(glow, xs - gr, y - gr, 2 * gr, 2 * gr);
        }
        using (var thumb = new SolidBrush(Ui.Accent)) g.FillEllipse(thumb, xs - tr, y - tr, 2 * tr, 2 * tr);
        using (var inner = new SolidBrush(Color.White)) g.FillEllipse(inner, xs - tr / 3, y - tr / 3, 2 * tr / 3, 2 * tr / 3);

        // labels
        int lw = Ui.S(this, 112), lh = Ui.S(this, 22), ly = (int)y + Ui.S(this, 18);
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        for (int i = 0; i < n; i++)
        {
            bool on = i == shown;
            TextRenderer.DrawText(g, _labels[i], on ? Ui.Bold : Font,
                new Rectangle((int)StopX(i) - lw / 2, ly, lw, lh), on ? Ui.Text : Ui.Muted, flags);
        }
    }
}

/// <summary>A row of round colour swatches; the chosen one has a ring around it.</summary>
internal sealed class ModernSwatches : Control
{
    string _value = "";
    int _hover = -1;
    public event EventHandler? ValueChanged;

    public ModernSwatches()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Size = new Size(Ui.AccentNames.Length * 46, 40);
    }

    public string Value
    {
        get => _value;
        set { if (_value == value) return; _value = value; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
    }

    int Step => Height + 6;
    int At(Point p) { int i = p.X / Step; return i >= 0 && i < Ui.AccentNames.Length ? i : -1; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = At(e.Location);
        if (i != _hover) { _hover = i; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = At(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0) Value = Ui.AccentNames[i];
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < Ui.AccentNames.Length; i++)
        {
            string name = Ui.AccentNames[i];
            int ring = Math.Max(2, Height / 16);
            var cell = new Rectangle(i * Step, 0, Height, Height);
            var dot = Rectangle.Inflate(cell, -Height / 5, -Height / 5);
            if (name == _value)
            {
                using var pen = new Pen(Ui.Text, ring);
                g.DrawEllipse(pen, Rectangle.Inflate(cell, -ring, -ring));
            }
            else if (i == _hover)
            {
                using var pen = new Pen(Ui.Border, ring);
                g.DrawEllipse(pen, Rectangle.Inflate(cell, -ring, -ring));
            }
            using var br = new SolidBrush(Ui.AccentSwatch(name));
            g.FillEllipse(br, dot);
        }
    }
}
