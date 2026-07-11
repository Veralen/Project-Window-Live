using ScreenTranslator.Core.Blocks;
using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Ocr;
using ScreenTranslator.Core.Placement;
using Xunit;

namespace ScreenTranslator.Core.Tests;

public class AspectRatioLabelPlacerTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    private static readonly AspectRatioLabelPlacer Placer = new();

    private static TranslatedBlock Block(string translated, PixelRect sourceBounds, double lineHeight, string origText = "orig")
    {
        var line = new OcrLine(origText, sourceBounds, new[] { new OcrWord(origText, sourceBounds) });
        // Give the single synthetic line the requested height, independent of
        // the overall block bounds, so tests can control the median-line-height
        // input to the initial font-size calculation directly.
        var lineWithHeight = line with { Bounds = new PixelRect(sourceBounds.X, sourceBounds.Y, sourceBounds.Width, lineHeight) };
        var source = new TextBlockGroup(origText, sourceBounds, new[] { lineWithHeight });
        return new TranslatedBlock(source, translated);
    }

    private static bool IsWithin(PixelRect rect, PixelRect bounds) =>
        rect.Left >= bounds.Left - 0.001 && rect.Top >= bounds.Top - 0.001 &&
        rect.Right <= bounds.Right + 0.001 && rect.Bottom <= bounds.Bottom + 0.001;

    [Fact]
    public void WideBlock_PlacesLabelBelow()
    {
        var block = Block("Hello there", new PixelRect(100, 100, 300, 50), 50);
        var result = Placer.Place(new[] { block }, Monitor, new FakeTextMeasurer());

        var label = Assert.Single(result);
        Assert.True(label.LabelBounds.Top >= block.Source.Bounds.Bottom);
        Assert.True(IsWithin(label.LabelBounds, Monitor));
    }

    [Fact]
    public void WideBlockNearBottomEdge_FlipsAbove()
    {
        var source = new PixelRect(100, 1000, 300, 50); // bottom = 1050, close to monitor bottom 1080
        var block = Block("Short", source, 50);
        var result = Placer.Place(new[] { block }, Monitor, new FakeTextMeasurer());

        var label = Assert.Single(result);
        Assert.True(label.LabelBounds.Bottom <= source.Top + 0.001);
        Assert.True(IsWithin(label.LabelBounds, Monitor));
    }

    [Fact]
    public void TallBlock_PlacesLabelBeside()
    {
        var block = Block("Menu item", new PixelRect(100, 100, 50, 300), 20);
        var result = Placer.Place(new[] { block }, Monitor, new FakeTextMeasurer());

        var label = Assert.Single(result);
        Assert.True(label.LabelBounds.Left >= block.Source.Bounds.Right);
        Assert.True(IsWithin(label.LabelBounds, Monitor));
    }

    [Fact]
    public void TallBlockNearRightEdge_FlipsLeft()
    {
        var source = new PixelRect(1850, 100, 50, 300); // right = 1900, close to monitor right 1920
        var block = Block("Sidebar entry", source, 20);
        var result = Placer.Place(new[] { block }, Monitor, new FakeTextMeasurer());

        var label = Assert.Single(result);
        Assert.True(label.LabelBounds.Right <= source.Left + 0.001);
        Assert.True(IsWithin(label.LabelBounds, Monitor));
    }

    [Fact]
    public void LabelNeverExitsMonitorBounds_AcrossGridOfPositions()
    {
        var measurer = new FakeTextMeasurer();
        var positions = new (double X, double Y, double W, double H)[]
        {
            (0, 0, 300, 40),          // top-left, wide
            (0, 0, 40, 300),          // top-left, tall
            (1600, 900, 300, 40),     // bottom-right, wide
            (1600, 700, 40, 300),     // bottom-right, tall
            (10, 500, 250, 30),       // left edge, wide
            (1850, 500, 60, 250),     // right edge, tall
            (800, 10, 300, 40),       // top edge, wide
            (800, 1020, 300, 50),     // bottom edge, wide
        };

        foreach (var (x, y, w, h) in positions)
        {
            var source = new PixelRect(x, y, w, h);
            var block = Block("A reasonably long translated caption for sizing purposes", source, Math.Min(w, h));
            var result = Placer.Place(new[] { block }, Monitor, measurer);

            var label = Assert.Single(result);
            Assert.True(IsWithin(label.LabelBounds, Monitor),
                $"Label {label.LabelBounds} exited monitor bounds for source {source}");
        }
    }

    [Fact]
    public void TwoStackedWideBlocks_SecondLabelNudged_NoChipOrSourceIntersections()
    {
        var source1 = new PixelRect(100, 100, 300, 50);
        var source2 = new PixelRect(100, 170, 300, 50); // close enough that block1's label would overlap block2's source
        var block1 = Block("First paragraph translation", source1, 50);
        var block2 = Block("Second paragraph translation", source2, 50);

        var result = Placer.Place(new[] { block1, block2 }, Monitor, new FakeTextMeasurer());

        Assert.Equal(2, result.Count);
        var label1 = result[0].LabelBounds;
        var label2 = result[1].LabelBounds;

        Assert.False(label1.IntersectsWith(label2));
        Assert.False(label1.IntersectsWith(source2));
        Assert.False(label2.IntersectsWith(source1));
        Assert.True(IsWithin(label1, Monitor));
        Assert.True(IsWithin(label2, Monitor));
    }

    [Fact]
    public void EmptyOrWhitespaceTranslation_IsSkipped()
    {
        var block1 = Block("", new PixelRect(100, 100, 300, 50), 50);
        var block2 = Block("   ", new PixelRect(500, 100, 300, 50), 50);
        var block3 = Block("Real text", new PixelRect(900, 100, 300, 50), 50);

        var result = Placer.Place(new[] { block1, block2, block3 }, Monitor, new FakeTextMeasurer());

        var label = Assert.Single(result);
        Assert.Equal("Real text", label.Block.TranslatedText);
    }

    [Fact]
    public void FontShrinks_ForVeryLongTranslationOfSmallBlock()
    {
        var source = new PixelRect(100, 100, 60, 20);
        double initialFont = Math.Clamp(20, LabelStyle.MinFontSize, LabelStyle.MaxFontSize);
        string longText = string.Join(" ", Enumerable.Repeat("word", 60));
        var block = Block(longText, source, 20);

        var result = Placer.Place(new[] { block }, Monitor, new FakeTextMeasurer());

        var label = Assert.Single(result);
        Assert.True(label.FontSize < initialFont);
        Assert.True(label.FontSize >= LabelStyle.MinFontSize);
    }

    [Fact]
    public void Placement_IsDeterministic_AcrossRepeatedRuns()
    {
        var block1 = Block("First translation", new PixelRect(100, 100, 300, 50), 50);
        var block2 = Block("Second translation", new PixelRect(100, 300, 50, 300), 20);

        var result1 = Placer.Place(new[] { block1, block2 }, Monitor, new FakeTextMeasurer());
        var result2 = Placer.Place(new[] { block1, block2 }, Monitor, new FakeTextMeasurer());

        Assert.Equal(result1.Count, result2.Count);
        for (int i = 0; i < result1.Count; i++)
        {
            Assert.Equal(result1[i].LabelBounds, result2[i].LabelBounds);
            Assert.Equal(result1[i].FontSize, result2[i].FontSize);
        }
    }
}
