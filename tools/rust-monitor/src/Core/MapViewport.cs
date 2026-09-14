using System.Windows;

namespace RustMonitor.Core;

public sealed class MapViewport
{
    public double Zoom { get; private set; } = 1;
    public Point Center { get; private set; } = new(.5, .5);
    public Rect ImageRect { get; private set; }
    private Size viewport, image;

    // Rust's published map includes a 500-pixel ocean border on every edge.
    // Positive world Z is north, whereas image Y increases toward the south.
    public static Point? Project(PlayerRecord player, string wipe, int worldSize, int width, int height)
    {
        if (player.WipeId != wipe || !DateTimeOffset.TryParse(player.PositionAt, out _) ||
            player.X is not double x || player.Z is not double z || !double.IsFinite(x) || !double.IsFinite(z) ||
            worldSize <= 0 || width <= 1000 || height <= 1000) return null;
        var point = new Point((500 + (x / worldSize + .5) * (width - 1000)) / width,
            (500 + (.5 - z / worldSize) * (height - 1000)) / height);
        return point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1 ? point : null;
    }

    public void Resize(Size available, Size pixels)
    {
        viewport = available; image = pixels; Layout();
    }
    public void Reset() { Zoom = 1; Center = new(.5, .5); Layout(); }
    public Point ToScreen(Point normalized) => new(ImageRect.X + normalized.X * ImageRect.Width, ImageRect.Y + normalized.Y * ImageRect.Height);
    public void ZoomAt(double factor, Point anchor)
    {
        if (ImageRect.IsEmpty || ImageRect.Width <= 0 || !double.IsFinite(factor) || factor <= 0) return;
        var normalized = new Point((anchor.X - ImageRect.X) / ImageRect.Width, (anchor.Y - ImageRect.Y) / ImageRect.Height);
        Zoom = Math.Clamp(Zoom * factor, 1, 16); Layout();
        Center = new(normalized.X - (anchor.X - viewport.Width / 2) / ImageRect.Width,
            normalized.Y - (anchor.Y - viewport.Height / 2) / ImageRect.Height);
        Layout();
    }
    public void Pan(Vector delta)
    {
        if (ImageRect.IsEmpty || ImageRect.Width <= 0) return;
        Center = new(Center.X - delta.X / ImageRect.Width, Center.Y - delta.Y / ImageRect.Height); Layout();
    }
    public void Focus(Point point) { Center = point; Zoom = Math.Max(3, Zoom); Layout(); }
    private void Layout()
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || image.Width <= 0 || image.Height <= 0) { ImageRect = Rect.Empty; return; }
        var scale = Math.Min(viewport.Width / image.Width, viewport.Height / image.Height) * Zoom;
        var width = image.Width * scale; var height = image.Height * scale;
        var halfX = viewport.Width / width / 2; var halfY = viewport.Height / height / 2;
        Center = new(halfX >= .5 ? .5 : Math.Clamp(Center.X, halfX, 1 - halfX),
            halfY >= .5 ? .5 : Math.Clamp(Center.Y, halfY, 1 - halfY));
        ImageRect = new(viewport.Width / 2 - Center.X * width, viewport.Height / 2 - Center.Y * height, width, height);
    }
}
