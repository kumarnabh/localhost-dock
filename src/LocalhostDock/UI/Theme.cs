using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using LocalhostDock.Core;

namespace LocalhostDock.UI;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(24, 25, 29);
    public static readonly Color Surface = Color.FromArgb(34, 36, 41);
    public static readonly Color SurfaceHover = Color.FromArgb(44, 47, 54);
    public static readonly Color Input = Color.FromArgb(17, 18, 21);
    public static readonly Color Border = Color.FromArgb(56, 59, 67);
    public static readonly Color Text = Color.FromArgb(234, 235, 238);
    public static readonly Color Muted = Color.FromArgb(146, 151, 161);
    public static readonly Color Accent = Color.FromArgb(96, 165, 250);
    public static readonly Color AccentStrong = Color.FromArgb(59, 130, 246);
    public static readonly Color Up = Color.FromArgb(52, 199, 89);
    public static readonly Color Down = Color.FromArgb(239, 68, 68);
    public static readonly Color Pending = Color.FromArgb(245, 179, 66);
    public static readonly Color Unknown = Color.FromArgb(110, 114, 122);

    public static readonly Font Body = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9.5f);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font Header = new("Segoe UI Semibold", 7.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 11.5f);
    public static readonly Font Mono = MonoFont();
    public static readonly Font Icons = IconFont();

    // Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10) code points.
    public const string IconOpen = "";
    public const string IconPlay = "";
    public const string IconStop = "";
    public const string IconRestart = "";
    public const string IconMore = "";
    public const string IconAdd = "";
    public const string IconRefresh = "";
    public const string IconSearch = "";
    public const string IconLogs = "";
    public const string IconKill = "";

    /// <summary>Display scale (1.0 at 96 DPI). Layout is written in 96-DPI pixels and passed through S().</summary>
    public static readonly float Scale = GetScale();

    public static int S(int px) => (int)Math.Round(px * Scale);

    private static float GetScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return Math.Max(1f, g.DpiX / 96f);
    }

    private static Font MonoFont()
    {
        using var installed = new InstalledFontCollection();
        return new Font(installed.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9f);
    }

    private static Font IconFont()
    {
        using var installed = new InstalledFontCollection();
        string family = installed.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        return new Font(family, 9.5f);
    }

    public static Color For(AppState s) => s switch
    {
        AppState.Up => Up,
        AppState.Down => Down,
        AppState.Starting or AppState.Stopping => Pending,
        _ => Unknown,
    };

    /// <summary>A flat text button.</summary>
    public static Button Button(string text, EventHandler onClick, bool danger = false, bool primary = false)
    {
        var b = new FlatButton
        {
            Text = text,
            ForeColor = danger ? Down : Text,
            BackColor = primary ? AccentStrong : Surface,
            Font = Small,
            Size = new Size(Math.Max(S(48), TextRenderer.MeasureText(text, Small).Width + S(18)), S(26)),
        };
        b.FlatAppearance.BorderColor = primary ? AccentStrong : Border;
        b.FlatAppearance.MouseOverBackColor = primary ? Accent : SurfaceHover;
        b.Click += onClick;
        return b;
    }

    /// <summary>A square icon-only button with a tooltip.</summary>
    public static Button IconButton(string glyph, string tip, EventHandler onClick, bool danger = false)
    {
        var b = new FlatButton
        {
            Text = glyph,
            Font = Icons,
            ForeColor = danger ? Down : Text,
            BackColor = Color.Transparent,
            Size = new Size(S(28), S(26)),
            AccessibleName = tip,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = SurfaceHover;
        b.FlatAppearance.MouseDownBackColor = Border;
        Tips.SetToolTip(b, tip);
        b.Click += onClick;
        return b;
    }

    public static readonly ToolTip Tips = new() { InitialDelay = 350, ReshowDelay = 100 };

    /// <summary>Applies the dark palette to a dialog and everything in it (and a dark title bar).</summary>
    public static void Dark(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = Body;
        form.HandleCreated += (_, _) =>
        {
            int on = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        };
        DarkChildren(form);
    }

    public static void DarkChildren(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case TextBox t:
                    t.BackColor = Input; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox cb:
                    cb.BackColor = Input; cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Flat;
                    break;
                case ListView lv:
                    lv.BackColor = Input; lv.ForeColor = Text; lv.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case CheckedListBox clb:
                    clb.BackColor = Input; clb.ForeColor = Text; clb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case Button b when b is not FlatButton:
                    b.FlatStyle = FlatStyle.Flat; b.BackColor = Surface; b.ForeColor = Text;
                    b.FlatAppearance.BorderColor = Border; b.FlatAppearance.MouseOverBackColor = SurfaceHover;
                    break;
                case CheckBox ch:
                    ch.ForeColor = Text;
                    break;
            }
            if (c.HasChildren) DarkChildren(c);
        }
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr hwnd, string appName, string? idList);

    /// <summary>Tray icon: a dark rounded square with a dot (green = all up, amber = some up, grey = none).</summary>
    public static Icon TrayIcon(Color dot)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(40, 44, 52));
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 8);
            g.FillPath(bg, path);
            using var pen = new Pen(Color.FromArgb(200, 205, 214), 2.5f);
            // A tiny "dock": three stacked bars.
            g.DrawLine(pen, 7, 10, 19, 10);
            g.DrawLine(pen, 7, 16, 19, 16);
            g.DrawLine(pen, 7, 22, 19, 22);
            using var dotBrush = new SolidBrush(dot);
            g.FillEllipse(dotBrush, 19, 17, 11, 11);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0} MB",
        _ => $"{b / 1024} KB",
    };

    public static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "just now"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{(int)t.TotalDays}d {t.Hours}h";
}

/// <summary>Flat button that doesn't draw the focus rectangle and greys its text when disabled.</summary>
internal sealed class FlatButton : Button
{
    public FlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Margin = new Padding(Theme.S(2), 0, 0, 0);
        Padding = Padding.Empty;
        SetStyle(ControlStyles.Selectable, false);
    }

    protected override bool ShowFocusCues => false;

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Enabled) return;
        // WinForms draws disabled flat-button text in a light grey that's unreadable on dark; repaint it.
        Color back = BackColor;
        for (Control? p = Parent; back.A < 255 && p is not null; p = p.Parent) back = p.BackColor;
        using var bg = new SolidBrush(back.A < 255 ? Theme.Surface : back);
        var r = ClientRectangle;
        r.Inflate(-1, -1);
        e.Graphics.FillRectangle(bg, r);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(78, 82, 90),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>A small filled status dot.</summary>
internal sealed class Dot : Control
{
    private Color _color = Theme.Unknown;
    private bool _ring;

    public Dot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(Theme.S(12), Theme.S(12));
        BackColor = Color.Transparent;
    }

    public Color Color
    {
        get => _color;
        set { if (_color != value) { _color = value; Invalidate(); } }
    }

    /// <summary>Draw as a ring (used for groups that are partly up).</summary>
    public bool Ring
    {
        get => _ring;
        set { if (_ring != value) { _ring = value; Invalidate(); } }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_ring)
        {
            using var pen = new Pen(_color, Theme.S(2));
            e.Graphics.DrawEllipse(pen, 2, 2, Width - 5, Height - 5);
        }
        else
        {
            using var b = new SolidBrush(_color);
            e.Graphics.FillEllipse(b, 1, 1, Width - 3, Height - 3);
        }
    }
}
