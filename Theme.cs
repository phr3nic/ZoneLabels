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

    public static readonly Color Bg = Color.FromArgb(0x0B, 0x0E, 0x14);
    public static readonly Color BgDeep = Color.FromArgb(0x07, 0x09, 0x0E);
    public static readonly Color Panel = Color.FromArgb(0x12, 0x17, 0x23);
    public static readonly Color PanelHi = Color.FromArgb(0x1C, 0x23, 0x34);
    public static readonly Color PanelTop = Color.FromArgb(0x19, 0x20, 0x2E);
    public static readonly Color Border = Color.FromArgb(0x29, 0x33, 0x4A);
    public static readonly Color BorderSoft = Color.FromArgb(0x1D, 0x26, 0x39);
    public static readonly Color AccentSoft = Color.FromArgb(0x21, 0x22, 0x48);
    public static readonly Color Text = Color.FromArgb(0xE6, 0xE9, 0xF2);
    public static readonly Color Muted = Color.FromArgb(0x8A, 0x93, 0xA8);
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
            TabStop = false,
            UseVisualStyleBackColor = false,
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Warn;
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
