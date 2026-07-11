using ScreenTranslator.Core.Geometry;
using Xunit;

namespace ScreenTranslator.Core.Tests;

/// <summary>
/// Coverage for the PixelRect geometry helpers relied on by grouping and
/// placement. No bugs found here; these tests just pin down the edge-case
/// behavior (touching-but-not-overlapping rects, empty-rect union, negative
/// virtual-screen coordinates) that the rest of Work Package 3 depends on.
/// </summary>
public class PixelRectTests
{
    [Fact]
    public void IntersectsWith_TouchingEdges_DoNotIntersect()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(100, 0, 100, 100); // shares the x=100 edge, no area overlap

        Assert.False(a.IntersectsWith(b));
        Assert.False(b.IntersectsWith(a));
    }

    [Fact]
    public void IntersectsWith_OverlappingByOnePixel_Intersects()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(99, 0, 100, 100);

        Assert.True(a.IntersectsWith(b));
    }

    [Fact]
    public void IntersectsWith_EmptyRect_NeverIntersects()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var empty = new PixelRect(10, 10, 0, 0);

        Assert.False(a.IntersectsWith(empty));
        Assert.False(empty.IntersectsWith(a));
    }

    [Fact]
    public void Union_WithEmptyRect_ReturnsTheOtherRect()
    {
        var a = new PixelRect(10, 10, 50, 50);
        var empty = new PixelRect(0, 0, 0, 0);

        Assert.Equal(a, a.Union(empty));
        Assert.Equal(a, empty.Union(a));
    }

    [Fact]
    public void Union_AcrossNegativeVirtualScreenCoordinates_ComputesCorrectBounds()
    {
        // A monitor to the left of the primary monitor has negative X.
        var onSecondaryMonitor = new PixelRect(-500, 100, 200, 50);
        var onPrimaryMonitor = new PixelRect(50, 300, 100, 50);

        var union = onSecondaryMonitor.Union(onPrimaryMonitor);

        Assert.Equal(-500, union.X);
        Assert.Equal(100, union.Y);
        Assert.Equal(650, union.Width);  // max(Right) - min(X) = 150 - (-500)
        Assert.Equal(250, union.Height); // max(Bottom) - min(Y) = 350 - 100
    }

    [Fact]
    public void Union_OfThreeLineBounds_MatchesManualBoundingBox()
    {
        var line1 = new PixelRect(100, 0, 300, 20);
        var line2 = new PixelRect(100, 25, 280, 20);
        var line3 = new PixelRect(90, 50, 310, 22);

        var union = line1.Union(line2).Union(line3);

        Assert.Equal(90, union.X);
        Assert.Equal(0, union.Y);
        Assert.Equal(400, union.Right);  // line1 and line3 both reach x=400
        Assert.Equal(310, union.Width);  // 400 - 90
        Assert.Equal(72, union.Bottom);  // line3 bottom = 50+22 = 72, the lowest edge
    }
}
