namespace GameRamCap;

/// <summary>One look for every tab: same font, same spacing, same headings, same buttons, same background.</summary>
internal static class Ui
{
    public const int ContentWidth = 640;
    public static readonly Padding PagePadding = new(24, 16, 24, 16);
    static readonly Padding ButtonPadding = new(10, 4, 10, 4);

    public static readonly Font Base  = new("Segoe UI", 9f);
    public static readonly Font Bold  = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font Title = new("Segoe UI", 14f, FontStyle.Bold);
    public static readonly Font Hero  = new("Segoe UI", 12f, FontStyle.Bold);

    // One wording for every screen (Main tab, Settings pages, tray).
    public const string SmartText = "Smart - automatic and gentle (recommended)";
    public const string HardText = "Hard - a fixed RAM limit for every app";
    public const string ScopeProcText = "Per process (recommended) - every process of the app gets the limit";
    public const string ScopeAppText = "Per app - the limit is the total, shared by the app's processes";
    public static string LimitLabel(bool perApp) => perApp
        ? "RAM limit per app (total), for apps not in my list (MB):"
        : "RAM limit per process, for apps not in my list (MB):";

    public static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = Bold, Margin = new Padding(3, 16, 3, 4),
    };

    public static Label Note(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(ContentWidth, 0),
        ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 6),
    };

    public static Button Btn(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => click();
        Style(b);
        return b;
    }

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
        page.BackColor = SystemColors.Control;
    }

    static void Style(Button b)
    {
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = ButtonPadding;
        b.MinimumSize = new Size(0, 30);
        b.FlatStyle = FlatStyle.System;
    }

    /// <summary>Applies the common look to a whole control tree (buttons, tab pages, grids, lists).</summary>
    public static void Polish(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Button b: Style(b); break;
                case TabPage tp: Flat(tp); break;
                case DataGridView g:
                    g.BorderStyle = BorderStyle.FixedSingle;
                    g.EnableHeadersVisualStyles = true;
                    g.RowTemplate.Height = 24;
                    g.ColumnHeadersHeight = 28;
                    g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                    break;
                case ListView lv: lv.BorderStyle = BorderStyle.FixedSingle; break;
            }
            Polish(c);
        }
    }
}
