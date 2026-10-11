using System.Drawing.Drawing2D;
using LocalhostDock.Core;

namespace LocalhostDock.UI;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(28, 29, 33);
    public static readonly Color Surface = Color.FromArgb(38, 40, 45);
    public static readonly Color SurfaceHover = Color.FromArgb(50, 53, 59);
    public static readonly Color Border = Color.FromArgb(58, 61, 68);
    public static readonly Color Text = Color.FromArgb(232, 233, 236);
    public static readonly Color Muted = Color.FromArgb(150, 154, 163);
    public static readonly Color Accent = Color.FromArgb(96, 165, 250);
    public static readonly Color Up = Color.FromArgb(52, 199, 89);
    public static readonly Color Down = Color.FromArgb(239, 68, 68);
    public static readonly Color Pending = Color.FromArgb(245, 179, 66);
    public static readonly Color Unknown = Color.FromArgb(110, 114, 122);

    public static readonly Font Body = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9.5f);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font Header = new("Segoe UI Semibold", 8f);

    /// <summary>Display scale (1.0 at 96 DPI). Layout is written in 96-DPI pixels and passed through S().</summary>
    public static readonly float Scale = GetScale();

    public static int S(int px) => (int)Math.Round(px * Scale);

    private static float GetScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return Math.Max(1f, g.DpiX / 96f);
    }

    public static Color For(AppState s) => s switch
    {
        AppState.Up => Up,
        AppState.Down => Down,
        AppState.Starting or AppState.Stopping => Pending,
        _ => Unknown,
    };

    public static Button Button(string text, EventHandler onClick, bool danger = false)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            ForeColor = danger ? Down : Text,
            BackColor = Surface,
            Font = Small,
            Size = new Size(Math.Max(S(48), TextRenderer.MeasureText(text, Small).Width + S(16)), S(24)),
            Padding = Padding.Empty,
            Margin = new Padding(S(3), 0, 0, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = SurfaceHover;
        b.Click += onClick;
        return b;
    }

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
}

/// <summary>A small filled status dot.</summary>
internal sealed class Dot : Control
{
    private Color _color = Theme.Unknown;

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

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(_color);
        e.Graphics.FillEllipse(b, 1, 1, Width - 3, Height - 3);
    }
}
