using ScreenTranslator.Core.Blocks;
using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Ocr;
using Xunit;

namespace ScreenTranslator.Core.Tests;

public class ProximityTextBlockGrouperTests
{
    private static OcrLine Line(string text, double x, double y, double width, double height) =>
        new(text, new PixelRect(x, y, width, height), new[] { new OcrWord(text, new PixelRect(x, y, width, height)) });

    private static readonly ProximityTextBlockGrouper Grouper = new();

    [Fact]
    public void EmptyInput_ProducesEmptyOutput()
    {
        var result = Grouper.Group(new OcrRegionResult(Array.Empty<OcrLine>(), "en-US"));
        Assert.Empty(result);
    }

    [Fact]
    public void SingleLine_ProducesSingleBlock()
    {
        var line = Line("Solo", 10, 10, 100, 20);
        var result = Grouper.Group(new OcrRegionResult(new[] { line }, "en-US"));

        var block = Assert.Single(result);
        Assert.Equal("Solo", block.Text);
        Assert.Equal(line.Bounds, block.Bounds);
        Assert.Single(block.Lines);
    }

    [Fact]
    public void ThreeAlignedLines_MergeIntoOneParagraphBlock()
    {
        var lines = new[]
        {
            Line("Alpha", 100, 0, 300, 20),
            Line("Bravo", 100, 25, 300, 20),   // gap 5 < 0.7*20=14
            Line("Charlie", 100, 50, 300, 20), // gap 5
        };

        var result = Grouper.Group(new OcrRegionResult(lines, "en-US"));

        var block = Assert.Single(result);
        Assert.Equal(3, block.Lines.Count);
        Assert.Equal("Alpha Bravo Charlie", block.Text);
        Assert.Equal(new PixelRect(100, 0, 300, 70), block.Bounds);
    }

    [Fact]
    public void LargeVerticalGap_SplitsIntoSeparateBlocks()
    {
        var lines = new[]
        {
            Line("Top", 100, 0, 300, 20),
            Line("Bottom", 100, 60, 300, 20), // gap 40 >= 0.7*20=14
        };

        var result = Grouper.Group(new OcrRegionResult(lines, "en-US"));

        Assert.Equal(2, result.Count);
        Assert.Equal("Top", result[0].Text);
        Assert.Equal("Bottom", result[1].Text);
    }

    [Fact]
    public void TwoSideBySideColumns_StaySeparateEvenWhenRowsInterleave()
    {
        // Column A: x 0-100. Column B: x 300-400. Each has 3 rows at matching
        // Y positions, so top-to-bottom/left-to-right sort interleaves them
        // (A1,B1,A2,B2,A3,B3) — the grouper must still keep each column intact.
        var lines = new[]
        {
            Line("A1", 0, 0, 100, 20),
            Line("B1", 300, 0, 100, 20),
            Line("A2", 0, 25, 100, 20),
            Line("B2", 300, 25, 100, 20),
            Line("A3", 0, 50, 100, 20),
            Line("B3", 300, 50, 100, 20),
        };

        var result = Grouper.Group(new OcrRegionResult(lines, "en-US"));

        Assert.Equal(2, result.Count);
        var columnA = result.Single(b => b.Text.StartsWith("A"));
        var columnB = result.Single(b => b.Text.StartsWith("B"));
        Assert.Equal("A1 A2 A3", columnA.Text);
        Assert.Equal("B1 B2 B3", columnB.Text);
    }

    [Fact]
    public void TwoVerticallyOverlappingButHorizontallyFarLines_DoNotMerge()
    {
        // Same row (0 vertical gap / full vertical overlap), but far apart
        // horizontally with no overlap and misaligned left edges.
        var lines = new[]
        {
            Line("Left", 0, 0, 100, 20),
            Line("Right", 500, 0, 100, 20),
        };

        var result = Grouper.Group(new OcrRegionResult(lines, "en-US"));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CjkLanguageTag_JoinsLinesWithNoSeparator()
    {
        var lines = new[]
        {
            Line("你好", 100, 0, 100, 20),
            Line("世界", 100, 25, 100, 20),
        };

        var result = Grouper.Group(new OcrRegionResult(lines, "zh-Hans-CN"));

        var block = Assert.Single(result);
        Assert.Equal("你好世界", block.Text);
    }

    [Theory]
    [InlineData("zh-Hans-CN")]
    [InlineData("ZH")]
    [InlineData("ja-JP")]
    [InlineData("ko-KR")]
    public void CjkPrefixMatch_IsCaseInsensitiveAndAnySuffix(string tag)
    {
        var lines = new[]
        {
            Line("A", 100, 0, 100, 20),
            Line("B", 100, 25, 100, 20),
        };

        var result = Grouper.Group(new OcrRegionResult(lines, tag));

        var block = Assert.Single(result);
        Assert.Equal("AB", block.Text);
    }

    [Fact]
    public void LatinLanguageTag_JoinsLinesWithSingleSpace()
    {
        var lines = new[]
        {
            Line("Hello", 100, 0, 200, 20),
            Line("World", 100, 25, 200, 20),
        };

        var result = Grouper.Group(new OcrRegionResult(lines, "en-US"));

        var block = Assert.Single(result);
        Assert.Equal("Hello World", block.Text);
    }

    [Fact]
    public void DifferingLineHeights_SplitEvenWhenAlignedAndClose()
    {
        var lines = new[]
        {
            Line("Small", 100, 0, 300, 20),
            Line("Big", 100, 25, 300, 45), // height ratio 45/20 = 2.25 >= 1.6
        };

        var result = Grouper.Group(new OcrRegionResult(lines, "en-US"));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void HorizontalOverlapWithoutLeftAlignment_StillMergesWhenOverlapAtLeastHalf()
    {
        // Line 2 is indented (not left-aligned) but overlaps line 1 horizontally
        // by more than 50% of the narrower line's width.
        var lines = new[]
        {
            Line("Wide first line", 0, 0, 200, 20),
            Line("indented", 50, 25, 150, 20), // overlap [50,200] = 150; narrower width = 150 -> 100% overlap
        };

        var result = Grouper.Group(new OcrRegionResult(lines, "en-US"));

        var block = Assert.Single(result);
        Assert.Equal(2, block.Lines.Count);
    }
}
