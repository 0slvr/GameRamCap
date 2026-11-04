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
    static readonly Padding ButtonPadding = new(12, 4, 12, 4);

    public static readonly Font Base    = new("Segoe UI", 9.75f);
    public static readonly Font Bold    = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Title   = new("Segoe UI Semibold", 20f);
    public static readonly Font Hero    = new("Segoe UI Semibold", 13f);
    public static readonly Font TabFont = new("Segoe UI Semibold", 11f);

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
    public static readonly Color Warn = Color.FromArgb(222, 150, 40);

    // One wording for every screen (Main tab, Settings pages, tray).
    public const string SmartText = "Smart - automatic and gentle (recommended)";
    public const string HardText = "Hard - a fixed RAM limit for every app";
    public const string ScopeProcText = "Per process (recommended) - every process of the app gets the limit";
    public const string ScopeAppText = "Per app - the limit is the total, shared by the app's processes";
    public static string LimitLabel(bool perApp) => perApp
        ? "RAM limit per app (total), for apps not in my list (MB):"
        : "RAM limit per process, for apps not in my list (MB):";

    static Ui()
    {
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
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => click();
        SizeButton(b);
        return b;
    }

    /// <summary>A rounded panel that groups related settings.</summary>
    public static Card MakeCard() => new();

    /// <summary>A scrolling top-to-bottom page with the standard padding.</summary>
    public static FlowLayoutPanel PageFlow() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
        AutoScroll = true, Padding = PagePadding,
    };

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
            Bg = Color.FromArgb(28, 28, 28);
            Surface = Color.FromArgb(40, 40, 40);
            Input = Color.FromArgb(54, 54, 54);
            Border = Color.FromArgb(66, 66, 66);
            Hover = Color.FromArgb(64, 64, 64);
            Text = Color.FromArgb(240, 240, 240);
            Muted = Color.FromArgb(160, 160, 160);
            Accent = Color.FromArgb(38, 128, 218);
            AccentHover = Color.FromArgb(58, 144, 228);
            AccentDown = Color.FromArgb(28, 108, 190);
            Good = Color.FromArgb(88, 210, 130);
        }
        else
        {
            Bg = Color.FromArgb(243, 243, 243);
            Surface = Color.FromArgb(255, 255, 255);
            Input = Color.FromArgb(246, 246, 246);
            Border = Color.FromArgb(222, 222, 222);
            Hover = Color.FromArgb(233, 233, 233);
            Text = Color.FromArgb(28, 28, 28);
            Muted = Color.FromArgb(100, 100, 100);
            Accent = Color.FromArgb(0, 103, 192);
            AccentHover = Color.FromArgb(0, 90, 170);
            AccentDown = Color.FromArgb(0, 75, 150);
            Good = Color.FromArgb(0, 130, 65);
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
        b.MinimumSize = new Size(0, 32);
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

    static void ThemeListView(ListView lv)
    {
        if (Drawn.TryGetValue(lv, out _)) return;
        Drawn.Add(lv, new object());
        lv.OwnerDraw = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            using (var b = new SolidBrush(Surface)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Bold, Rectangle.Inflate(e.Bounds, -8, 0), Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;
        lv.Resize += (_, _) => FitLastColumn(lv);
        FitLastColumn(lv);
    }

    static void FitLastColumn(ListView lv)
    {
        if (lv.View != View.Details || lv.Columns.Count == 0 || lv.ClientSize.Width <= 0) return;
        int used = 0;
        for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
        lv.Columns[lv.Columns.Count - 1].Width = Math.Max(60, lv.ClientSize.Width - used);
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
                if (f.TopLevel) { int v = Dark ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref v, 4); }
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
