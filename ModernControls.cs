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
        Padding = new Padding(18, 14, 18, 14);
        Margin = new Padding(3, 0, 3, 12);
        MinimumSize = new Size(Ui.ContentWidth + 40, 0);
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Bg);
        if (Width < 4 || Height < 4) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Ui.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(this, 10));
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

    public static Size Measure(ButtonBase b)
    {
        int box = Ui.S(b, 18), gap = Ui.S(b, 8);
        var s = TextRenderer.MeasureText(b.Text, b.Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(box + gap + s.Width + 6, Math.Max(box, s.Height) + 6);
    }

    public static void Paint(ButtonBase b, Graphics g, bool radio, bool on, bool hover)
    {
        g.Clear(b.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool enabled = b.Enabled;
        int box = Ui.S(b, 18), gap = Ui.S(b, 8);
        var r = new Rectangle(1, (b.Height - box) / 2, box - 2, box - 2);
        Color accent = enabled ? Ui.Accent : Color.FromArgb(110, Ui.Muted);
        Color edge = enabled ? (hover || b.Focused ? Ui.Accent : Ui.Muted) : Color.FromArgb(110, Ui.Muted);

        if (radio)
        {
            using (var fill = new SolidBrush(Ui.Input)) g.FillEllipse(fill, r);
            using (var pen = new Pen(on ? accent : edge, 1.6f)) g.DrawEllipse(pen, r);
            if (on)
            {
                var dot = Rectangle.Inflate(r, -Ui.S(b, 4), -Ui.S(b, 4));
                using var d = new SolidBrush(accent);
                g.FillEllipse(d, dot);
            }
        }
        else
        {
            using var path = Ui.RoundRect(r, Ui.S(b, 4));
            if (on)
            {
                using var fill = new SolidBrush(accent);
                g.FillPath(fill, path);
                using var tick = new Pen(Color.White, Math.Max(1.8f, box / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(tick, new[]
                {
                    new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.52f),
                    new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.72f),
                    new PointF(r.X + r.Width * 0.78f, r.Y + r.Height * 0.30f),
                });
            }
            else
            {
                using var fill = new SolidBrush(Ui.Input);
                g.FillPath(fill, path);
                using var pen = new Pen(edge, 1.6f);
                g.DrawPath(pen, path);
            }
        }

        TextRenderer.DrawText(g, b.Text, b.Font, new Rectangle(box + gap, 0, Math.Max(1, b.Width - box - gap), b.Height),
            enabled ? Ui.Text : Ui.Muted, Flags);
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
            Height = owner._compact ? 38 : 50;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Height = Ui.S(this, _owner._compact ? 38 : 50);
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
            int x = Ui.S(this, 12);
            for (int i = 0; i < pages.Count; i++)
            {
                string t = pages[i].Text;
                var sz = TextRenderer.MeasureText(t, TabFont, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int w = sz.Width + Ui.S(this, 28);
                var r = new Rectangle(x, 0, w, Height - 1);
                _rects.Add(r);
                bool on = i == sel;
                if (!on && i == _hover)
                {
                    using var path = Ui.RoundRect(new Rectangle(r.X + 2, Ui.S(this, 6), r.Width - 4, Height - Ui.S(this, 14)), Ui.S(this, 6));
                    using var hb = new SolidBrush(Ui.Hover);
                    g.FillPath(hb, path);
                }
                TextRenderer.DrawText(g, t, TabFont, r, on ? Ui.Text : Ui.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                if (on)
                {
                    int uh = Ui.S(this, 3);
                    var u = new Rectangle(r.X + (r.Width - sz.Width) / 2, Height - uh - 1, sz.Width, uh);
                    using var path = Ui.RoundRect(u, 1);
                    using var ab = new SolidBrush(Ui.Accent);
                    g.FillPath(ab, path);
                }
                x += w;
            }
            using var line = new Pen(Ui.Border);
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
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
