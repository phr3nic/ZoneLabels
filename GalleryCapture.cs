namespace ZoneLab;

internal sealed class GalleryCapture
{
    public int ScreenLeft { get; set; }
    public int ScreenTop { get; set; }
    public List<GalleryPin> Pins { get; set; } = new();
}

internal sealed class GalleryPin
{
    public int X { get; set; }
    public int Y { get; set; }
    public string? Zone { get; set; }
    public bool Exported { get; set; }
}
