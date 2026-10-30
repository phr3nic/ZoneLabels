using System.Drawing;
using System.Runtime.InteropServices;

namespace ZoneLab;

internal static class Theme
{
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    public static readonly Color Bg = Color.FromArgb(0x0B, 0x0D, 0x12);
    public static readonly Color BgDeep = Color.FromArgb(0x0B, 0x0D, 0x12);
    public static readonly Color Panel = Color.FromArgb(0x12, 0x15, 0x1C);
    public static readonly Color PanelHi = Color.FromArgb(0x1A, 0x1F, 0x2A);
    public static readonly Color PanelTop = Color.FromArgb(0x12, 0x15, 0x1C);
    public static readonly Color Border = Color.FromArgb(0x24, 0x2A, 0x3A);
    public static readonly Color BorderSoft = Color.FromArgb(0x24, 0x2A, 0x3A);
    public static readonly Color Text = Color.FromArgb(0xE6, 0xE9, 0xF0);
    public static readonly Color Muted = Color.FromArgb(0x8A, 0x93, 0xA6);
    public static readonly Color Accent = Color.FromArgb(0x6C, 0x6C, 0xF0);
    public static readonly Color Success = Color.FromArgb(0x46, 0xD6, 0x9A);
    public static readonly Color Warn = Color.FromArgb(0xF0, 0x8C, 0x6C);

    public static Control CreateDialogTitleBar(Form form, string title)
    {
        var titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Bg,
            Padding = new Padding(12, 0, 5, 0),
        };
        var caption = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = Text,
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var close = new Button
        {
            Text = "×",
            Dock = DockStyle.Right,
            Width = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Bg,
            ForeColor = Text,
            Font = new Font("Segoe UI", 12f),
            Cursor = Cursors.Hand,
            TabStop = true,
            UseVisualStyleBackColor = false,
            AccessibleName = "Close dialog",
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = PanelHi;
        close.FlatAppearance.MouseDownBackColor = Border;
        close.Click += (_, _) => form.Close();
        titleBar.Controls.Add(caption);
        titleBar.Controls.Add(close);
        titleBar.MouseDown += DragDialog;
        caption.MouseDown += DragDialog;
        return titleBar;
    }

    private static void DragDialog(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var form = (sender as Control)?.FindForm();
        if (form is null) return;
        ReleaseCapture();
        SendMessage(form.Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }
}
