using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Placement;

namespace ScreenTranslator.Core.Tests;

/// <summary>
/// Deterministic stand-in for a real text renderer, used across placement
/// tests. Width = charCount * fontSize * 0.55, clamped to maxWidthPx via a
/// greedy word-wrap (falling back to hard character breaks for unspaced runs,
/// e.g. CJK text, so it never returns a single line wider than maxWidthPx
/// when wrapping is even theoretically possible). Height = lineCount *
/// fontSize * 1.35.
/// </summary>
public sealed class FakeTextMeasurer : ITextMeasurer
{
    public const double CharWidthFactor = 0.55;
    public const double LineHeightFactor = 1.35;

    public PixelSize Measure(string text, double fontSizePx, double maxWidthPx)
    {
        if (string.IsNullOrEmpty(text))
            return new PixelSize(0, 0);

        double fontSize = Math.Max(1, fontSizePx);
        double charWidth = fontSize * CharWidthFactor;
        int maxChars = Math.Max(1, (int)Math.Floor(maxWidthPx / charWidth));

        var lineLengths = new List<int>();
        int current = 0;

        foreach (var rawWord in text.Split(' '))
        {
            var word = rawWord;

            // Hard-break any single "word" (e.g. an unspaced CJK run) that
            // alone exceeds the line budget.
            while (word.Length > maxChars)
            {
                if (current > 0)
                {
                    lineLengths.Add(current);
                    current = 0;
                }

                lineLengths.Add(maxChars);
                word = word[maxChars..];
            }

            int addLength = word.Length + (current > 0 ? 1 : 0);
            if (current > 0 && current + addLength > maxChars)
            {
                lineLengths.Add(current);
                current = 0;
                addLength = word.Length;
            }

            current += addLength;
        }

        if (current > 0 || lineLengths.Count == 0)
            lineLengths.Add(current);

        int maxLineLen = lineLengths.Max();
        int lineCount = lineLengths.Count;

        double width = Math.Min(maxLineLen * charWidth, maxWidthPx);
        double height = lineCount * fontSize * LineHeightFactor;
        return new PixelSize(width, height);
    }
}
