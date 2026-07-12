using WindowLive.Core.Geometry;

namespace WindowLive.Core.Overlay;

/// <summary>
/// Places the single translation chip relative to a snip selection (see
/// docs/window-live-design.md, "Overlay chip placement"). Below the selection
/// by default; flips above if below would exit the monitor bounds; clamped
/// horizontally so the chip never leaves the monitor. No multi-label
/// collision avoidance — there is only ever one chip.
/// </summary>
public static class ChipPlacement
{
    /// <summary>
    /// Computes the chip rect for a given selection.
    /// </summary>
    /// <param name="selection">The dragged snip region, physical pixels, virtual-screen coordinates.</param>
    /// <param name="chipWidth">Chip width in physical pixels.</param>
    /// <param name="chipHeight">Chip height in physical pixels.</param>
    /// <param name="monitorBounds">Bounds of the monitor the selection lives on.</param>
    /// <param name="gap">Vertical gap between the selection and the chip.</param>
    public static PixelRect Place(PixelRect selection, int chipWidth, int chipHeight, PixelRect monitorBounds, int gap)
    {
        double belowY = selection.Bottom + gap;
        double aboveY = selection.Top - gap - chipHeight;

        bool belowFits = belowY >= monitorBounds.Top && belowY + chipHeight <= monitorBounds.Bottom;
        bool aboveFits = aboveY >= monitorBounds.Top && aboveY + chipHeight <= monitorBounds.Bottom;

        double y;
        if (belowFits)
        {
            y = belowY;
        }
        else if (aboveFits)
        {
            y = aboveY;
        }
        else
        {
            // Neither position fits fully inside the monitor (e.g. a monitor
            // too short for the chip, or a selection spanning nearly the full
            // height). Prefer whichever candidate needs the least vertical
            // clamping, then clamp it inside the monitor bounds.
            double belowOverflow = VerticalOverflow(belowY, chipHeight, monitorBounds);
            double aboveOverflow = VerticalOverflow(aboveY, chipHeight, monitorBounds);
            double candidateY = aboveOverflow < belowOverflow ? aboveY : belowY;
            y = ClampVertical(candidateY, chipHeight, monitorBounds);
        }

        double x = ClampHorizontal(selection.X, chipWidth, monitorBounds);

        return new PixelRect(x, y, chipWidth, chipHeight);
    }

    private static double VerticalOverflow(double y, double height, PixelRect monitorBounds)
    {
        double aboveTop = Math.Max(0, monitorBounds.Top - y);
        double belowBottom = Math.Max(0, (y + height) - monitorBounds.Bottom);
        return aboveTop + belowBottom;
    }

    private static double ClampVertical(double y, double height, PixelRect monitorBounds)
    {
        double minY = monitorBounds.Top;
        double maxY = monitorBounds.Bottom - height;
        // Chip taller than the monitor: anchor to the top rather than throw.
        return maxY < minY ? minY : Math.Clamp(y, minY, maxY);
    }

    private static double ClampHorizontal(double x, double width, PixelRect monitorBounds)
    {
        double minX = monitorBounds.Left;
        double maxX = monitorBounds.Right - width;
        // Chip wider than the monitor: anchor to the left rather than throw.
        return maxX < minX ? minX : Math.Clamp(x, minX, maxX);
    }
}
