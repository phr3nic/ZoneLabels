using System.Drawing.Drawing2D;

namespace ZoneLab;

internal sealed class GlassPanel : Panel
{
    private Color _surfaceColor = Theme.Panel;

    public Color SurfaceColor
    {
        get => _surfaceColor;
        set
        {
            _surfaceColor = value;
            BackColor = value;
        }
    }

    public Color EdgeColor { get; set; } = Theme.BorderSoft;
    public int CornerRadius { get; set; } = 12;

    public GlassPanel()
    {
        SetStyle(ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width < 2 || Height < 2) return;
        using var path = CreatePath(new Rectangle(0, 0, Width, Height));
        Region? previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg);
        if (Width < 2 || Height < 2) return;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = CreatePath(bounds);
        using var fill = new LinearGradientBrush(bounds, Theme.PanelTop, SurfaceColor, 90f);
        using var edge = new Pen(EdgeColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(edge, path);
    }

    private GraphicsPath CreatePath(Rectangle bounds)
    {
        int radius = Math.Min(CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2);
        int diameter = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Region?.Dispose();
        base.Dispose(disposing);
    }
}
