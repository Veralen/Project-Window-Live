using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Ocr;

namespace ScreenTranslator.Core.Placement;

/// <summary>
/// Decides label placement from the source block's aspect ratio, sizes the
/// label to keep its footprint close to the source's scale, and resolves
/// collisions deterministically. See docs/architecture.md §Placement rules
/// for the binding contract this implements.
/// </summary>
public sealed class AspectRatioLabelPlacer : ILabelPlacer
{
    /// <summary>
    /// Chip footprint area must not exceed this multiple of the source block's
    /// area, or the font is shrunk (down to LabelStyle.MinFontSize).
    /// </summary>
    public const double MaxFootprintAreaRatio = 1.5;

    /// <summary>Minimum wrap width used for below/above placement, physical px.</summary>
    public const double MinBelowAboveWrapWidth = 120;

    /// <summary>Wrap width cap for beside placement, as a multiple of source block width.</summary>
    public const double BesideWrapWidthMultiplier = 1.5;

    /// <summary>Smallest wrap width we'll ever hand to the measurer, physical px (avoids degenerate 0/negative widths near screen edges).</summary>
    public const double MinWrapWidthFloor = 20;

    /// <summary>Max collision-nudge iterations before accepting the current (possibly still-colliding) position.</summary>
    public const int MaxNudgeIterations = 20;

    /// <summary>Extra clearance added past the measured overlap on each nudge step, physical px.</summary>
    public const double NudgeMarginPx = 2;

    private enum Direction { Right, Left, Below, Above }

    public IReadOnlyList<PlacedLabel> Place(
        IReadOnlyList<TranslatedBlock> blocks,
        PixelRect monitorBounds,
        ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(measurer);

        var results = new List<PlacedLabel>();
        var placedChips = new List<PixelRect>();

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (string.IsNullOrWhiteSpace(block.TranslatedText))
                continue;

            var otherSources = new List<PixelRect>(blocks.Count - 1);
            for (int j = 0; j < blocks.Count; j++)
            {
                if (j != i)
                    otherSources.Add(blocks[j].Source.Bounds);
            }

            var placed = PlaceOne(block, monitorBounds, measurer, placedChips, otherSources);
            results.Add(placed);
            placedChips.Add(placed.LabelBounds);
        }

        return results;
    }

    private static PlacedLabel PlaceOne(
        TranslatedBlock block,
        PixelRect monitorBounds,
        ITextMeasurer measurer,
        List<PixelRect> placedChips,
        List<PixelRect> otherSources)
    {
        var source = block.Source.Bounds;
        string text = block.TranslatedText;
        double initialFont = ClampFontSize(MedianLineHeight(block.Source.Lines));

        // 1. Direction from aspect ratio.
        var direction = source.Height > source.Width ? Direction.Right : Direction.Below;

        // 2/3. Size the label for that direction, then flip once if it would
        // exit the monitor bounds on that side.
        var (chip, font) = ComputeChip(text, source, monitorBounds, direction, initialFont, measurer);
        if (ExitsBounds(chip, direction, monitorBounds))
        {
            direction = Opposite(direction);
            (chip, font) = ComputeChip(text, source, monitorBounds, direction, initialFont, measurer);
        }

        // 4. Clamp fully inside monitor bounds.
        chip = ClampToMonitor(chip, monitorBounds);

        // 5. Collision avoidance: nudge along the placement axis; if still
        // colliding after the bounded nudge, try the flipped direction once;
        // otherwise accept the (clamped) overlap as a last resort.
        chip = NudgeUntilClear(chip, direction, monitorBounds, placedChips, otherSources, out bool resolved);
        if (!resolved)
        {
            var flipDirection = Opposite(direction);
            var (flipChip, flipFont) = ComputeChip(text, source, monitorBounds, flipDirection, initialFont, measurer);
            flipChip = ClampToMonitor(flipChip, monitorBounds);
            flipChip = NudgeUntilClear(flipChip, flipDirection, monitorBounds, placedChips, otherSources, out _);
            chip = flipChip;
            font = flipFont;
        }

        return new PlacedLabel(block, chip, font);
    }

    private static (PixelRect chip, double font) ComputeChip(
        string text,
        PixelRect source,
        PixelRect monitorBounds,
        Direction direction,
        double initialFont,
        ITextMeasurer measurer)
    {
        double font = initialFont;
        double sourceArea = Math.Max(source.Width * source.Height, 1);
        double wrapWidth = ComputeWrapWidth(source, monitorBounds, direction);

        while (true)
        {
            var textSize = measurer.Measure(text, font, wrapWidth);
            double chipWidth = textSize.Width + 2 * LabelStyle.PaddingX;
            double chipHeight = textSize.Height + 2 * LabelStyle.PaddingY;
            var chip = PositionChip(source, direction, chipWidth, chipHeight);

            double chipArea = chipWidth * chipHeight;
            if (chipArea > MaxFootprintAreaRatio * sourceArea && font > LabelStyle.MinFontSize)
            {
                font = Math.Max(LabelStyle.MinFontSize, font - 1);
                continue;
            }

            return (chip, font);
        }
    }

    private static double ComputeWrapWidth(PixelRect source, PixelRect monitorBounds, Direction direction)
    {
        switch (direction)
        {
            case Direction.Below:
            case Direction.Above:
                return Math.Max(MinBelowAboveWrapWidth, source.Width);

            case Direction.Right:
            {
                double available = monitorBounds.Right - (source.Right + LabelStyle.Gap);
                return Math.Max(MinWrapWidthFloor, Math.Min(available, source.Width * BesideWrapWidthMultiplier));
            }

            case Direction.Left:
            {
                double available = (source.Left - LabelStyle.Gap) - monitorBounds.Left;
                return Math.Max(MinWrapWidthFloor, Math.Min(available, source.Width * BesideWrapWidthMultiplier));
            }

            default:
                return MinBelowAboveWrapWidth;
        }
    }

    private static PixelRect PositionChip(PixelRect source, Direction direction, double chipWidth, double chipHeight)
    {
        return direction switch
        {
            Direction.Below => new PixelRect(source.Center.X - chipWidth / 2, source.Bottom + LabelStyle.Gap, chipWidth, chipHeight),
            Direction.Above => new PixelRect(source.Center.X - chipWidth / 2, source.Top - LabelStyle.Gap - chipHeight, chipWidth, chipHeight),
            Direction.Right => new PixelRect(source.Right + LabelStyle.Gap, source.Center.Y - chipHeight / 2, chipWidth, chipHeight),
            Direction.Left => new PixelRect(source.Left - LabelStyle.Gap - chipWidth, source.Center.Y - chipHeight / 2, chipWidth, chipHeight),
            _ => new PixelRect(source.Left, source.Bottom + LabelStyle.Gap, chipWidth, chipHeight),
        };
    }

    private static bool ExitsBounds(PixelRect chip, Direction direction, PixelRect monitorBounds) => direction switch
    {
        Direction.Right => chip.Right > monitorBounds.Right,
        Direction.Left => chip.Left < monitorBounds.Left,
        Direction.Below => chip.Bottom > monitorBounds.Bottom,
        Direction.Above => chip.Top < monitorBounds.Top,
        _ => false,
    };

    private static Direction Opposite(Direction d) => d switch
    {
        Direction.Right => Direction.Left,
        Direction.Left => Direction.Right,
        Direction.Below => Direction.Above,
        Direction.Above => Direction.Below,
        _ => d,
    };

    private static PixelRect ClampToMonitor(PixelRect chip, PixelRect monitorBounds)
    {
        double width = Math.Min(chip.Width, monitorBounds.Width);
        double height = Math.Min(chip.Height, monitorBounds.Height);

        double maxX = Math.Max(monitorBounds.Left, monitorBounds.Right - width);
        double maxY = Math.Max(monitorBounds.Top, monitorBounds.Bottom - height);

        double x = Math.Clamp(chip.X, monitorBounds.Left, maxX);
        double y = Math.Clamp(chip.Y, monitorBounds.Top, maxY);

        return new PixelRect(x, y, width, height);
    }

    private static PixelRect NudgeUntilClear(
        PixelRect chip,
        Direction direction,
        PixelRect monitorBounds,
        List<PixelRect> placedChips,
        List<PixelRect> otherSources,
        out bool resolved)
    {
        for (int i = 0; i < MaxNudgeIterations; i++)
        {
            var collider = FindCollision(chip, placedChips, otherSources);
            if (collider is null)
            {
                resolved = true;
                return chip;
            }

            chip = NudgeAlongAxis(chip, direction, collider.Value);
            chip = ClampToMonitor(chip, monitorBounds);
        }

        resolved = FindCollision(chip, placedChips, otherSources) is null;
        return chip;
    }

    private static PixelRect? FindCollision(PixelRect chip, List<PixelRect> placedChips, List<PixelRect> otherSources)
    {
        foreach (var p in placedChips)
        {
            if (chip.IntersectsWith(p))
                return p;
        }

        foreach (var s in otherSources)
        {
            if (chip.IntersectsWith(s))
                return s;
        }

        return null;
    }

    private static PixelRect NudgeAlongAxis(PixelRect chip, Direction direction, PixelRect collider)
    {
        switch (direction)
        {
            case Direction.Below:
            {
                double overlap = Math.Max(0, Math.Min(chip.Bottom, collider.Bottom) - Math.Max(chip.Top, collider.Top));
                return chip.Offset(0, overlap + NudgeMarginPx);
            }
            case Direction.Above:
            {
                double overlap = Math.Max(0, Math.Min(chip.Bottom, collider.Bottom) - Math.Max(chip.Top, collider.Top));
                return chip.Offset(0, -(overlap + NudgeMarginPx));
            }
            case Direction.Right:
            {
                double overlap = Math.Max(0, Math.Min(chip.Right, collider.Right) - Math.Max(chip.Left, collider.Left));
                return chip.Offset(overlap + NudgeMarginPx, 0);
            }
            case Direction.Left:
            {
                double overlap = Math.Max(0, Math.Min(chip.Right, collider.Right) - Math.Max(chip.Left, collider.Left));
                return chip.Offset(-(overlap + NudgeMarginPx), 0);
            }
            default:
                return chip;
        }
    }

    private static double MedianLineHeight(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0)
            return LabelStyle.MinFontSize;

        var arr = lines.Select(l => l.Bounds.Height).OrderBy(h => h).ToArray();
        int mid = arr.Length / 2;
        return arr.Length % 2 == 0 ? (arr[mid - 1] + arr[mid]) / 2 : arr[mid];
    }

    private static double ClampFontSize(double v) => Math.Clamp(v, LabelStyle.MinFontSize, LabelStyle.MaxFontSize);
}
