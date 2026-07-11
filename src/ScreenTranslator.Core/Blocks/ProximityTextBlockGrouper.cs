using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Ocr;

namespace ScreenTranslator.Core.Blocks;

/// <summary>
/// Groups raw OCR lines into paragraph-level blocks using proximity + alignment
/// heuristics. See docs/architecture.md §Grouping rules for the binding contract
/// this implements.
/// </summary>
public sealed class ProximityTextBlockGrouper : ITextBlockGrouper
{
    /// <summary>
    /// Max vertical gap between two lines considered for the same block,
    /// expressed as a multiple of the median line height across the region.
    /// </summary>
    public const double MaxVerticalGapRatio = 0.7;

    /// <summary>
    /// Max left-edge deviation between two lines considered "aligned",
    /// expressed as a multiple of the median estimated char width across the region.
    /// </summary>
    public const double MaxLeftEdgeDeviationCharWidths = 1.5;

    /// <summary>
    /// Minimum horizontal-overlap ratio (relative to the narrower of the two lines)
    /// that counts as alignment even when left edges don't match (e.g. centered text).
    /// </summary>
    public const double MinHorizontalOverlapRatio = 0.5;

    /// <summary>
    /// Max ratio between the taller and shorter line height of two lines being
    /// considered for merge; larger ratios indicate different text roles (e.g.
    /// a heading next to body text) and must not merge.
    /// </summary>
    public const double MaxLineHeightRatio = 1.6;

    public IReadOnlyList<TextBlockGroup> Group(OcrRegionResult ocr)
    {
        ArgumentNullException.ThrowIfNull(ocr);

        if (ocr.Lines.Count == 0)
            return Array.Empty<TextBlockGroup>();

        // Sort lines top-to-bottom, then left-to-right for lines effectively on
        // the same row. This gives a stable overall reading order; the merge
        // logic below is robust to interleaved columns regardless of this order.
        var sorted = ocr.Lines
            .OrderBy(l => l.Bounds.Top)
            .ThenBy(l => l.Bounds.Left)
            .ToList();

        double medianLineHeight = Median(sorted.Select(l => l.Bounds.Height));
        double medianCharWidth = Median(sorted.Select(EstimateCharWidth));

        // Multiple blocks can be "open" (still eligible to receive more lines)
        // at once. This is what lets two side-by-side columns each accumulate
        // their own multi-line block even though their lines interleave in the
        // top-to-bottom/left-to-right sort order.
        var openBlocks = new List<List<OcrLine>>();

        foreach (var line in sorted)
        {
            List<OcrLine>? bestBlock = null;
            double bestGap = double.MaxValue;

            foreach (var block in openBlocks)
            {
                var last = block[^1];
                if (!CanMerge(last, line, medianLineHeight, medianCharWidth))
                    continue;

                double gap = line.Bounds.Top - last.Bounds.Bottom;
                if (bestBlock == null || gap < bestGap)
                {
                    bestBlock = block;
                    bestGap = gap;
                }
            }

            if (bestBlock != null)
                bestBlock.Add(line);
            else
                openBlocks.Add(new List<OcrLine> { line });
        }

        return openBlocks
            .OrderBy(b => b[0].Bounds.Top)
            .ThenBy(b => b[0].Bounds.Left)
            .Select(b => BuildGroup(b, ocr.LanguageTag))
            .ToList();
    }

    private static bool CanMerge(OcrLine prev, OcrLine curr, double medianLineHeight, double medianCharWidth)
    {
        double verticalGap = curr.Bounds.Top - prev.Bounds.Bottom;
        if (verticalGap >= MaxVerticalGapRatio * medianLineHeight)
            return false;

        bool leftAligned = Math.Abs(curr.Bounds.Left - prev.Bounds.Left) <= MaxLeftEdgeDeviationCharWidths * medianCharWidth;
        bool horizontallyOverlapping = HorizontalOverlapRatio(prev.Bounds, curr.Bounds) >= MinHorizontalOverlapRatio;
        if (!leftAligned && !horizontallyOverlapping)
            return false;

        double h1 = prev.Bounds.Height;
        double h2 = curr.Bounds.Height;
        if (h1 <= 0 || h2 <= 0)
            return false;

        double heightRatio = Math.Max(h1, h2) / Math.Min(h1, h2);
        return heightRatio < MaxLineHeightRatio;
    }

    private static double HorizontalOverlapRatio(PixelRect a, PixelRect b)
    {
        double overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (overlap <= 0)
            return 0;

        double narrower = Math.Min(a.Width, b.Width);
        return narrower <= 0 ? 0 : overlap / narrower;
    }

    private static double EstimateCharWidth(OcrLine line)
    {
        int count = Math.Max(1, line.Text.Length);
        return line.Bounds.Width / count;
    }

    private static double Median(IEnumerable<double> values)
    {
        var arr = values.OrderBy(v => v).ToArray();
        if (arr.Length == 0)
            return 0;

        int mid = arr.Length / 2;
        return arr.Length % 2 == 0 ? (arr[mid - 1] + arr[mid]) / 2 : arr[mid];
    }

    private static TextBlockGroup BuildGroup(List<OcrLine> lines, string languageTag)
    {
        var bounds = lines.Select(l => l.Bounds).Aggregate((a, b) => a.Union(b));
        string text = JoinText(lines, languageTag);
        return new TextBlockGroup(text, bounds, lines);
    }

    private static string JoinText(List<OcrLine> lines, string languageTag) =>
        IsCjk(languageTag)
            ? string.Concat(lines.Select(l => l.Text))
            : string.Join(" ", lines.Select(l => l.Text));

    private static bool IsCjk(string languageTag)
    {
        if (string.IsNullOrEmpty(languageTag))
            return false;

        return languageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || languageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            || languageTag.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
    }
}
