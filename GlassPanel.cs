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

    public GlassPanel()
    {
        SetStyle(ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg);
        if (Width < 2 || Height < 2) return;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var fill = new SolidBrush(SurfaceColor);
        using var edge = new Pen(EdgeColor);
        e.Graphics.FillRectangle(fill, bounds);
        e.Graphics.DrawRectangle(edge, bounds);
    }
}
