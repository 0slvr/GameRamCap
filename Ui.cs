using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameRamCap;

/// <summary>One modern look for every window: follows the Windows light/dark setting, flat buttons, rounded cards,
/// same fonts, spacing and headings everywhere. Call <see cref="Polish"/> once after building a window.</summary>
internal static class Ui
{
    public const int ContentWidth = 640;
    public static readonly Padding PagePadding = new(24, 16, 24, 16);
    static readonly Padding ButtonPadding = new(16, 4, 16, 4);

    public static readonly Font Base    = new("Segoe UI", 10f);
    public static readonly Font Bold    = new("Segoe UI Semibold", 10f);
    public static readonly Font Title   = new("Segoe UI Semibold", 24f);
    public static readonly Font Hero    = new("Segoe UI Semibold", 15f);
    public static readonly Font TabFont = new("Segoe UI Semibold", 10.5f);

    // ---- palette (set by Detect) ----
    public static bool Dark { get; private set; }
    public static Color Bg { get; private set; }
    public static Color Surface { get; private set; }
    public static Color Input { get; private set; }
    public static Color Border { get; private set; }
    public static Color Hover { get; private set; }
    public static Color Text { get; private set; }
    public static Color Muted { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentHover { get; private set; }
    public static Color AccentDown { get; private set; }
    public static Color Good { get; private set; }
    public static readonly Color Warn = Color.FromArgb(245, 158, 11);

    // One wording for every screen (Main tab, Settings pages, tray).
    public const string SmartText = "Smart - automatic and gentle (recommended)";
    public const string HardText = "Hard - a fixed RAM limit for every app";
    public const string ScopeProcText = "Per process (recommended) - every process of the app gets the limit";
    public const string ScopeAppText = "Per app - the limit is the total, shared by the app's processes";
    public static string LimitLabel(bool perApp) => perApp
        ? "RAM limit per app (total), for apps not in my list (MB):"
        : "RAM limit per process, for apps not in my list (MB):";

    // ---- accent colours the user can pick (dark theme colour, light theme colour) ----
    public static readonly string[] AccentNames = { "Blue", "Violet", "Teal", "Orange", "Pink" };
    static readonly Dictionary<string, (Color Dark, Color Light)> Accents = new()
    {
        ["Blue"]   = (Color.FromArgb(79, 125, 245),  Color.FromArgb(37, 99, 235)),
        ["Violet"] = (Color.FromArgb(139, 108, 246), Color.FromArgb(109, 40, 217)),
        ["Teal"]   = (Color.FromArgb(20, 184, 166),  Color.FromArgb(13, 148, 136)),
        ["Orange"] = (Color.FromArgb(249, 128, 60),  Color.FromArgb(217, 90, 20)),
        ["Pink"]   = (Color.FromArgb(236, 92, 160),  Color.FromArgb(219, 39, 119)),
    };
    public static string AccentName { get; private set; } = "Blue";

    /// <summary>The swatch colour of an accent for the current light / dark theme.</summary>
    public static Color AccentSwatch(string name) =>
        Accents.TryGetValue(name, out var a) ? (Dark ? a.Dark : a.Light) : Accent;

    /// <summary>Changes the accent colour everywhere at once and remembers it.</summary>
    public static void SetAccent(string name)
    {
        if (!Accents.ContainsKey(name) || name == AccentName) return;
        AccentName = name;
        UiPrefs.Current.Accent = name;
        UiPrefs.Save();
        Detect();
        foreach (var f in Application.OpenForms.Cast<Form>().ToArray())
            if (f.IsHandleCreated) f.Invalidate(true);
    }

    static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    static Ui()
    {
        var saved = UiPrefs.Current.Accent;
        if (Accents.ContainsKey(saved)) AccentName = saved;
        Detect();
        ToolStripManager.Renderer = new ThemedRenderer();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    // ================= building blocks =================

    public static Label Heading(string text, bool first = false) => new()
    {
        Text = text, AutoSize = true, Font = Bold,
        Margin = first ? new Padding(3, 0, 3, 8) : new Padding(3, 16, 3, 6),
    };

    public static Label Note(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(ContentWidth, 0),
        Tag = "muted", Margin = new Padding(3, 0, 3, 6),
    };

    public static Button Btn(string text, Action click)
    {
        var b = new ModernButton { Text = text, AutoSize = true };
        b.Click += (_, _) => click();
        SizeButton(b);
        return b;
    }

    /// <summary>A rounded panel that groups related settings.</summary>
    public static Card MakeCard() => new();

    /// <summary>A scrolling top-to-bottom page with the standard padding.</summary>
    public static FlowLayoutPanel PageFlow() => new CenteredPage();

    /// <summary>A scrolling page whose column of cards stays centred when the window is wide.</summary>
    sealed class CenteredPage : FlowLayoutPanel
    {
        public CenteredPage()
        {
            Dock = DockStyle.Fill; FlowDirection = FlowDirection.TopDown; WrapContents = false;
            AutoScroll = true; Padding = PagePadding;
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            int column = S(this, ContentWidth + 48 + 6);
            // centred in 8 px steps: re-laying out every card on every pixel of a resize drag is what makes it lag
            int left = Math.Max(S(this, PagePadding.Left), (ClientSize.Width - column) / 2 / 8 * 8);
            var p = new Padding(left, PagePadding.Top + 8, PagePadding.Right, PagePadding.Bottom + 8);
            if (Padding != p) Padding = p;
        }
    }

    /// <summary>Tab pages use the same plain background as the forms inside them.</summary>
    public static void Flat(TabPage page)
    {
        page.UseVisualStyleBackColor = false;
        page.BackColor = Bg;
    }

    /// <summary>Scales a pixel size designed for 96 dpi to the control's real dpi.</summary>
    public static int S(Control c, int px) => (int)Math.Round(px * Math.Max(c.DeviceDpi, 96) / 96.0);

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ================= theme =================

    static void Detect()
    {
        bool dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { }
        Dark = dark;
        if (dark)
        {
            Bg = Color.FromArgb(17, 18, 23);
            Surface = Color.FromArgb(27, 29, 36);
            Input = Color.FromArgb(37, 40, 49);
            Border = Color.FromArgb(45, 48, 59);
            Hover = Color.FromArgb(44, 47, 58);
            Text = Color.FromArgb(238, 240, 246);
            Muted = Color.FromArgb(142, 148, 164);
            Accent = Color.FromArgb(79, 125, 245);
            AccentHover = Color.FromArgb(103, 145, 255);
            AccentDown = Color.FromArgb(62, 105, 220);
            Good = Color.FromArgb(52, 211, 153);
        }
        else
        {
            Bg = Color.FromArgb(244, 245, 249);
            Surface = Color.FromArgb(255, 255, 255);
            Input = Color.FromArgb(241, 243, 248);
            Border = Color.FromArgb(227, 230, 238);
            Hover = Color.FromArgb(234, 237, 245);
            Text = Color.FromArgb(23, 25, 35);
            Muted = Color.FromArgb(104, 110, 128);
            Accent = Color.FromArgb(37, 99, 235);
            AccentHover = Color.FromArgb(29, 84, 205);
            AccentDown = Color.FromArgb(24, 70, 175);
            Good = Color.FromArgb(5, 150, 105);
        }
        if (Accents.TryGetValue(AccentName, out var acc))
        {
            Accent = dark ? acc.Dark : acc.Light;
            AccentHover = dark ? Blend(Accent, Color.White, 0.16) : Blend(Accent, Color.Black, 0.14);
            AccentDown = Blend(Accent, Color.Black, dark ? 0.16 : 0.26);
        }
    }

    static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        bool was = Dark;
        Detect();
        if (was == Dark) return;
        foreach (var f in Application.OpenForms.Cast<Form>().ToArray())
        {
            if (!f.IsHandleCreated) continue;
            var form = f;
            form.BeginInvoke(new Action(() => { Polish(form); form.Invalidate(true); }));
        }
    }

    /// <summary>Applies the current theme to a whole control tree (the control itself and everything inside it).</summary>
    public static void Polish(Control root)
    {
        Style(root);
        Hook(root);
        foreach (Control c in root.Controls) Polish(c);
    }

    static void Style(Control c)
    {
        Color under = c.Parent?.BackColor ?? Bg;
        switch (c)
        {
            case Form:
                c.BackColor = Bg; c.ForeColor = Text; break;
            case Card:
                c.BackColor = Surface; c.ForeColor = Text; break;
            case TabPage tp:
                tp.UseVisualStyleBackColor = false; tp.BackColor = Bg; tp.ForeColor = Text; break;
            case Panel or UserControl:
                c.BackColor = under; c.ForeColor = Text;
                // nested rows inside a flow: no extra outer margin, so every row lines up with its siblings
                if (c is Panel && c.Parent is FlowLayoutPanel && c.Margin == new Padding(3)) c.Margin = Padding.Empty;
                break;
            case Label l:
                l.BackColor = under; l.ForeColor = (l.Tag as string) == "muted" ? Muted : (l.Tag as string) == "warn" ? Warn : Text; break;
            case CheckBox or RadioButton:
                c.BackColor = under; c.ForeColor = Text; break;
            case Button b:
                StyleButton(b); break;
            case TextBox t:
                t.BackColor = Input; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle; break;
            case ComboBox cb:
                cb.BackColor = Input; cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Flat; break;
            case NumericUpDown n:
                n.BackColor = Input; n.ForeColor = Text; n.BorderStyle = BorderStyle.FixedSingle; break;
            case ListBox lb:
                lb.BackColor = Input; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.None; break;
            case ListView lv:
                lv.BackColor = Input; lv.ForeColor = Text; lv.BorderStyle = BorderStyle.None; ThemeListView(lv); break;
            case DataGridView g:
                StyleGrid(g); break;
        }
    }

    static void SizeButton(Button b)
    {
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = ButtonPadding;
        b.MinimumSize = new Size(0, 36);
    }

    static void StyleButton(Button b)
    {
        SizeButton(b);
        bool primary = (b.Tag as string) == "primary";
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Border;
        b.BackColor = primary ? Accent : (Dark ? Input : Surface);
        b.ForeColor = primary ? Color.White : Text;
        b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Hover;
        b.FlatAppearance.MouseDownBackColor = primary ? AccentDown : Border;
    }

    static void StyleGrid(DataGridView g)
    {
        g.BorderStyle = BorderStyle.None;
        g.BackgroundColor = g.Parent?.BackColor ?? Bg;
        g.GridColor = Border;
        g.EnableHeadersVisualStyles = false;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 34;
        g.RowTemplate.Height = 30;

        var head = g.ColumnHeadersDefaultCellStyle;
        head.BackColor = Surface; head.ForeColor = Muted; head.Font = Bold;
        head.SelectionBackColor = Surface; head.SelectionForeColor = Muted;
        head.Padding = new Padding(6, 0, 0, 0);

        var cell = g.DefaultCellStyle;
        cell.BackColor = Input; cell.ForeColor = Text;
        cell.SelectionBackColor = Accent; cell.SelectionForeColor = Color.White;
        cell.Padding = new Padding(4, 0, 0, 0);
    }

    // ---- list views: flat custom header, last column fills the width ----
    static readonly ConditionalWeakTable<Control, object> Drawn = new();

    /// <summary>App icon of a list row (set by the owner). Rows that have one are drawn as a tree: chevron, icon, name.</summary>
    public static readonly ConditionalWeakTable<ListViewItem, Image> ItemIcons = new();

    static Font? _icons;
    /// <summary>Windows' own icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on 10); null when neither exists.</summary>
    public static Font? Icons
    {
        get
        {
            if (_icons != null) return _icons;
            foreach (var n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                if (FontFamily.Families.Any(f => f.Name == n)) return _icons = new Font(n, 11f);
            return null;
        }
    }

    static void ThemeListView(ListView lv)
    {
        if (Drawn.TryGetValue(lv, out _)) return;
        Drawn.Add(lv, new object());
        lv.OwnerDraw = true;
        lv.GridLines = false;
        try
        {
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(lv, true);
        }
        catch { }

        int hover = -1;
        lv.DrawColumnHeader += (_, e) =>
        {
            using (var b = new SolidBrush(Surface)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Bold, Rectangle.Inflate(e.Bounds, -8, 0), Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        lv.DrawItem += (_, _) => { };   // each row is painted cell by cell below
        lv.DrawSubItem += (sender, e) =>
        {
            if (e.Item == null || e.SubItem == null) return;
            var g = e.Graphics;
            bool tree = ItemIcons.TryGetValue(e.Item, out var icon);
            bool child = tree && e.Item.Tag is not string;
            // like Task Manager: a selected app is a soft neutral highlight with a thin outline around the app and its processes
            ListViewItem? parent = null;
            if (child)
                for (int k = e.Item.Index - 1; k >= 0; k--) if (lv.Items[k].Tag is string) { parent = lv.Items[k]; break; }
            bool selVis = e.Item.Selected || (parent?.Selected ?? false);
            Color baseBack = e.Item.Index == hover ? Hover : (child ? Blend(Input, Bg, 0.35) : Input);
            Color back = selVis ? Blend(Input, Text, Dark ? 0.10 : 0.08) : baseBack;
            // heat map: the busier a cell, the stronger the accent tint behind it
            if (e.SubItem.Tag is double heat && heat > 0)
                back = Blend(back, Accent, (Dark ? 0.07 : 0.05) + (Dark ? 0.48 : 0.32) * Math.Min(1.0, heat));
            void Outline()
            {
                if (!selVis) return;
                int first = e.Item.Index, last = first;
                var owner = parent != null && parent.Selected ? parent : e.Item;
                if (owner.Tag is string && ItemIcons.TryGetValue(owner, out _))
                {
                    first = owner.Index; last = first;
                    while (last + 1 < lv.Items.Count && lv.Items[last + 1].Tag is not string && ItemIcons.TryGetValue(lv.Items[last + 1], out _)) last++;
                }
                using var pen = new Pen(Color.FromArgb(215, Text));
                var b = e.Bounds;
                if (e.ColumnIndex == 0) g.DrawLine(pen, b.Left, b.Top, b.Left, b.Bottom - 1);
                if (e.ColumnIndex == lv.Columns.Count - 1) g.DrawLine(pen, b.Right - 1, b.Top, b.Right - 1, b.Bottom - 1);
                if (e.Item.Index == first) g.DrawLine(pen, b.Left, b.Top, b.Right - 1, b.Top);
                if (e.Item.Index == last) g.DrawLine(pen, b.Left, b.Bottom - 1, b.Right - 1, b.Bottom - 1);
            }
            using (var br = new SolidBrush(back)) g.FillRectangle(br, e.Bounds);
            using (var line = new Pen(Color.FromArgb(70, Border)))
                g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            Color fore = e.Item.UseItemStyleForSubItems ? e.Item.ForeColor : e.SubItem.ForeColor;
            if (selVis) fore = Text;
            var font = e.SubItem.Font ?? lv.Font;
            const TextFormatFlags common = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                           TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

            if (tree && e.ColumnIndex == 0 && icon != null)
            {
                string t = e.SubItem.Text;
                int lead = t.Length - t.TrimStart(' ').Length;
                t = t.TrimStart(' ');
                char chev = ' ';
                if (t.Length > 0 && (t[0] == '\u25B8' || t[0] == '\u25BE')) { chev = t[0]; t = t.Substring(1).TrimStart(' '); }
                if (t.StartsWith("\u2514")) t = t.Substring(1).TrimStart(' ');
                int x = e.Bounds.X + 8;
                if (child) x += 18 + Math.Max(0, (lead - 8) / 4) * 14;
                int cy = e.Bounds.Y + e.Bounds.Height / 2;
                if (chev != ' ')
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    int cx = x + 6;
                    var pts = chev == '\u25BE'
                        ? new[] { new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3) }
                        : new[] { new Point(cx - 2, cy - 4), new Point(cx + 3, cy), new Point(cx - 2, cy + 4) };
                    using var cb = new SolidBrush(Muted);
                    g.FillPolygon(cb, pts);
                }
                x += 20;
                g.DrawImage(icon, new Rectangle(x, cy - 8, 16, 16));
                x += 24;
                TextRenderer.DrawText(g, t, font, new Rectangle(x, e.Bounds.Y, Math.Max(1, e.Bounds.Right - x - 6), e.Bounds.Height),
                    fore, TextFormatFlags.Left | common);
                Outline();
                return;
            }

            bool right = e.ColumnIndex > 0 && e.ColumnIndex < lv.Columns.Count && lv.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right;
            var rect = right
                ? new Rectangle(e.Bounds.X + 4, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 16), e.Bounds.Height)
                : new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 12), e.Bounds.Height);
            TextRenderer.DrawText(g, e.SubItem.Text, font, rect, fore, (right ? TextFormatFlags.Right : TextFormatFlags.Left) | common);
            Outline();
        };
        lv.MouseMove += (_, e) =>
        {
            int i = lv.GetItemAt(e.X, e.Y)?.Index ?? -1;
            if (i == hover) return;
            int old = hover;
            hover = i;
            if (old >= 0 && old < lv.Items.Count) lv.RedrawItems(old, old, false);
            if (i >= 0 && i < lv.Items.Count) lv.RedrawItems(i, i, false);
        };
        lv.MouseLeave += (_, _) =>
        {
            int old = hover;
            hover = -1;
            if (old >= 0 && old < lv.Items.Count) lv.RedrawItems(old, old, false);
        };
        lv.SelectedIndexChanged += (_, _) => lv.Invalidate();   // the outline spans an app and its process rows
        lv.Resize += (_, _) => FitLastColumn(lv);
        lv.ClientSizeChanged += (_, _) => FitLastColumn(lv);
        lv.HandleCreated += (_, _) => FitLastColumn(lv);
        lv.VisibleChanged += (_, _) => FitLastColumn(lv);
        FitLastColumn(lv);
    }

    /// <summary>The last shown column takes the remaining width, so the header has no empty strip.
    /// A column the user hid has Tag "hidden" and width 0.</summary>
    public static void FitLastColumn(ListView lv)
    {
        if (lv.View != View.Details || lv.Columns.Count == 0 || lv.ClientSize.Width <= 0) return;
        if ((lv.Tag as string) == "fluid") return;   // the owner sizes its columns itself, in proportion
        ColumnHeader? last = null;
        int used = 0;
        foreach (ColumnHeader c in lv.Columns)
        {
            if ((c.Tag as string) == "hidden") continue;
            if (last != null) used += last.Width;
            last = c;
        }
        if (last == null) return;
        int w = Math.Max(60, lv.ClientSize.Width - used);
        if (last.Width != w) last.Width = w;
    }

    // ---- native bits: dark title bar, dark scrollbars / edit boxes ----
    static readonly ConditionalWeakTable<Control, object> Hooked = new();

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? subApp, string? subIdList);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static void Hook(Control c)
    {
        if (c is not (ScrollableControl or TextBoxBase or ComboBox or NumericUpDown or ListBox or ListView or DataGridView)) return;
        if (!Hooked.TryGetValue(c, out _))
        {
            Hooked.Add(c, new object());
            c.HandleCreated += (s, _) => Native((Control)s!);
        }
        if (c.IsHandleCreated) Native(c);
    }

    static void Native(Control c)
    {
        try
        {
            if (c is Form f)
            {
                if (f.TopLevel)
                {
                    int v = Dark ? 1 : 0;
                    DwmSetWindowAttribute(f.Handle, 20, ref v, 4);
                    // Windows 11: title bar takes the page colour, so the window looks like one seamless surface
                    int cap = Bg.R | (Bg.G << 8) | (Bg.B << 16);
                    int txt = Text.R | (Text.G << 8) | (Text.B << 16);
                    DwmSetWindowAttribute(f.Handle, 35, ref cap, 4);
                    DwmSetWindowAttribute(f.Handle, 36, ref txt, 4);
                }
                return;
            }
            bool multi = c is TextBox tb && tb.Multiline;
            string? theme = !Dark ? null : (c is ComboBox or NumericUpDown || (c is TextBoxBase && !multi)) ? "DarkMode_CFD" : "DarkMode_Explorer";
            SetWindowTheme(c.Handle, theme, null);
        }
        catch { }
    }
}

/// <summary>Menu colours for the tray menu (follows the same palette).</summary>
internal sealed class ThemedColors : ProfessionalColorTable
{
    public ThemedColors() { UseSystemColors = false; }
    public override Color ToolStripDropDownBackground => Ui.Surface;
    public override Color ImageMarginGradientBegin => Ui.Surface;
    public override Color ImageMarginGradientMiddle => Ui.Surface;
    public override Color ImageMarginGradientEnd => Ui.Surface;
    public override Color MenuBorder => Ui.Border;
    public override Color MenuItemBorder => Ui.Hover;
    public override Color MenuItemSelected => Ui.Hover;
    public override Color MenuItemSelectedGradientBegin => Ui.Hover;
    public override Color MenuItemSelectedGradientEnd => Ui.Hover;
    public override Color MenuItemPressedGradientBegin => Ui.Hover;
    public override Color MenuItemPressedGradientMiddle => Ui.Hover;
    public override Color MenuItemPressedGradientEnd => Ui.Hover;
    public override Color SeparatorDark => Ui.Border;
    public override Color SeparatorLight => Ui.Surface;
    public override Color CheckBackground => Ui.Accent;
    public override Color CheckSelectedBackground => Ui.Accent;
    public override Color CheckPressedBackground => Ui.Accent;
}

internal sealed class ThemedRenderer : ToolStripProfessionalRenderer
{
    public ThemedRenderer() : base(new ThemedColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Ui.Text : Ui.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Ui.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        using (var path = Ui.RoundRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), 4))
        using (var fill = new SolidBrush(Ui.Accent))
            g.FillPath(fill, path);
        using var pen = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(r.X + r.Width * 0.25f, r.Y + r.Height * 0.52f),
            new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.70f),
            new PointF(r.X + r.Width * 0.76f, r.Y + r.Height * 0.30f),
        });
    }
}
