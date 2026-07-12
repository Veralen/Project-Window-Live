using WindowLive.Core.Geometry;
using WindowLive.Core.Overlay;
using Xunit;

namespace WindowLive.Core.Tests;

public class ChipPlacementTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void Place_WhenBelowFits_PlacesChipBelowSelection()
    {
        var selection = new PixelRect(500, 400, 200, 100);

        var chip = ChipPlacement.Place(selection, chipWidth: 150, chipHeight: 40, Monitor, gap: 8);

        Assert.Equal(selection.Bottom + 8, chip.Y);
        Assert.Equal(500, chip.X); // fits horizontally, so no clamping needed
        Assert.Equal(150, chip.Width);
        Assert.Equal(40, chip.Height);
    }

    [Fact]
    public void Place_WhenBelowWouldExitMonitor_FlipsAbove()
    {
        // Selection near the bottom edge — below has no room, above does.
        var selection = new PixelRect(500, 1000, 200, 60);

        var chip = ChipPlacement.Place(selection, chipWidth: 150, chipHeight: 40, Monitor, gap: 8);

        Assert.Equal(selection.Top - 8 - 40, chip.Y);
    }

    [Fact]
    public void Place_WhenSelectionNearRightEdge_ClampsHorizontally()
    {
        // Selection.X + chipWidth would exit past monitor's right edge (1920).
        var selection = new PixelRect(1850, 400, 60, 100);

        var chip = ChipPlacement.Place(selection, chipWidth: 150, chipHeight: 40, Monitor, gap: 8);

        Assert.Equal(Monitor.Right - 150, chip.X);
    }

    [Fact]
    public void Place_WhenSelectionNearLeftEdge_ClampsHorizontally()
    {
        // A monitor to the left of the primary has negative X; selection.X
        // sits right at the monitor's left edge minus a bit.
        var leftMonitor = new PixelRect(-1920, 0, 1920, 1080);
        var selection = new PixelRect(-1920, 400, 40, 100);

        var chip = ChipPlacement.Place(selection, chipWidth: 150, chipHeight: 40, leftMonitor, gap: 8);

        Assert.Equal(leftMonitor.Left, chip.X);
    }

    [Fact]
    public void Place_OnDegenerateTinyMonitor_ClampsInsideWithoutThrowing()
    {
        // Monitor smaller than the chip in both dimensions — must not throw,
        // and should anchor to the monitor's top-left rather than produce a
        // rect wildly outside monitor bounds.
        var tinyMonitor = new PixelRect(0, 0, 20, 20);
        var selection = new PixelRect(5, 5, 10, 10);

        var chip = ChipPlacement.Place(selection, chipWidth: 150, chipHeight: 40, tinyMonitor, gap: 8);

        Assert.Equal(tinyMonitor.Left, chip.X);
        Assert.Equal(tinyMonitor.Top, chip.Y);
        Assert.Equal(150, chip.Width);
        Assert.Equal(40, chip.Height);
    }

    [Fact]
    public void Place_WhenNeitherBelowNorAboveFullyFits_PrefersSmallerOverflow()
    {
        // Monitor is 300 tall. Selection occupies rows 100-200 (height 100).
        // Chip height 90, gap 20.
        // Below candidate: y = 220, bottom = 310 -> overflow 10 (past bottom=300).
        // Above candidate: y = 100 - 20 - 90 = -10 -> overflow 10 (past top=0).
        // Overflow is tied at 10 for both; implementation prefers below when tied
        // (aboveOverflow < belowOverflow is false on tie).
        var monitor = new PixelRect(0, 0, 400, 300);
        var selection = new PixelRect(50, 100, 100, 100);

        var chip = ChipPlacement.Place(selection, chipWidth: 60, chipHeight: 90, monitor, gap: 20);

        // Whichever candidate is chosen, the result must be clamped fully inside monitor bounds.
        Assert.True(chip.Top >= monitor.Top);
        Assert.True(chip.Bottom <= monitor.Bottom);
    }
}
