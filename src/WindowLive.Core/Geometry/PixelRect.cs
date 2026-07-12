namespace WindowLive.Core.Geometry;

/// <summary>
/// A point in physical (device) pixels, in virtual-screen coordinates.
/// The virtual screen spans all monitors; the primary monitor's top-left is (0,0)
/// and coordinates can be negative for monitors left of / above it.
/// </summary>
public readonly record struct PixelPoint(double X, double Y);

/// <summary>A size in physical (device) pixels.</summary>
public readonly record struct PixelSize(double Width, double Height);

/// <summary>
/// A rectangle in physical (device) pixels, virtual-screen coordinates.
/// ALL geometry in Core (OCR bounds, block bounds, label bounds) uses this
/// coordinate space. Conversion to/from WPF device-independent units happens
/// only at the App rendering boundary.
/// </summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public PixelPoint Center => new(X + Width / 2, Y + Height / 2);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool IntersectsWith(PixelRect other) =>
        !IsEmpty && !other.IsEmpty &&
        other.X < Right && X < other.Right &&
        other.Y < Bottom && Y < other.Bottom;

    public bool Contains(PixelPoint p) =>
        p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        double x = Math.Min(X, other.X);
        double y = Math.Min(Y, other.Y);
        return new PixelRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public PixelRect Inflate(double dx, double dy) =>
        new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    public PixelRect Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);
}
