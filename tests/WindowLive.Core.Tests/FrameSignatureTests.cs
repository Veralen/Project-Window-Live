using WindowLive.Core.Polling;
using Xunit;

namespace WindowLive.Core.Tests;

/// <summary>
/// Coverage for <see cref="FrameSignature"/>: the downsampled per-cell
/// mean-luminance grid that replaced exact-byte hashing for game-mode change
/// detection (see docs/window-live-design.md, "Live loop"). The grid needs to
/// (a) cancel small per-pixel noise via cell averaging, (b) flag a genuinely
/// new chat line as a change, and (c) partition any width/height — including
/// non-divisible ones — with exact, non-overlapping pixel coverage.
/// </summary>
public class FrameSignatureTests
{
    // BGRA32, no stride padding: 4 bytes per pixel, B G R A, top-down rows.
    private static byte[] MakeSolidBuffer(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var buf = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            buf[i * 4] = b;
            buf[i * 4 + 1] = g;
            buf[i * 4 + 2] = r;
            buf[i * 4 + 3] = a;
        }
        return buf;
    }

    private static void SetPixel(byte[] buf, int width, int x, int y, byte b, byte g, byte r, byte a = 255)
    {
        int i = (y * width + x) * 4;
        buf[i] = b;
        buf[i + 1] = g;
        buf[i + 2] = r;
        buf[i + 3] = a;
    }

    private static void FillRect(byte[] buf, int width, int x0, int y0, int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
                SetPixel(buf, width, x, y, b, g, r, a);
    }

    // Gray (R=G=B=v) always yields luma == v exactly: (77+150+29) == 256, so
    // v*256 >> 8 == v with no rounding at all. Used throughout to get exact,
    // predictable luminance values without fighting integer rounding.
    private static byte[] MakeGrayBuffer(int width, int height, byte v) => MakeSolidBuffer(width, height, v, v, v);

    [Fact]
    public void Compute_SolidColorBuffer_YieldsUniformGridAndIsSelfSimilar()
    {
        var buf = MakeSolidBuffer(64, 64, b: 50, g: 100, r: 200);
        var sig = FrameSignature.Compute(buf, 64, 64);

        // Expected luma: (77*200 + 150*100 + 29*50) >> 8 = 31850 >> 8 = 124.
        for (int row = 0; row < sig.Rows; row++)
            for (int col = 0; col < sig.Cols; col++)
                Assert.Equal(124, sig.CellLuma(col, row));

        Assert.True(FrameSignature.Similar(sig, sig, 8, 0.0));
    }

    [Fact]
    public void Compute_SameBufferTwice_IsDeterministic()
    {
        var buf = MakeSolidBuffer(64, 64, b: 50, g: 100, r: 200);
        var sig1 = FrameSignature.Compute(buf, 64, 64);
        var sig2 = FrameSignature.Compute(buf, 64, 64);

        Assert.True(FrameSignature.Similar(sig1, sig2, 8, 0.0));
    }

    [Fact]
    public void Similar_OneCellChangedByLargeDelta_IsNotSimilarUnderStrictThresholds()
    {
        // 64x64 with default targetCellPx=16 divides evenly into a 4x4 grid;
        // cell (0,0) is exactly the top-left 16x16 block.
        var white = MakeGrayBuffer(64, 64, 255);
        var mutated = MakeGrayBuffer(64, 64, 255);
        FillRect(mutated, 64, 0, 0, 16, 16, b: 0, g: 0, r: 0);

        var sigWhite = FrameSignature.Compute(white, 64, 64);
        var sigMutated = FrameSignature.Compute(mutated, 64, 64);

        Assert.False(FrameSignature.Similar(sigWhite, sigMutated, 8, 0.0));
    }

    [Fact]
    public void Similar_BalancedPerPixelNoise_CellMeansStayWithinTolerance()
    {
        // Alternate +3/-3 by (x+y) parity: with an even cell edge (16) aligned
        // to multiples of 16, every cell gets exactly half +3 and half -3
        // pixels, so the cell mean (and hence luma, since gray channels are
        // equal) is unchanged even though every single pixel moved.
        const int width = 64, height = 64;
        var baseBuf = MakeGrayBuffer(width, height, 128);
        var noisy = new byte[baseBuf.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = (byte)(((x + y) % 2 == 0) ? 131 : 125);
                SetPixel(noisy, width, x, y, v, v, v);
            }
        }

        var sigBase = FrameSignature.Compute(baseBuf, width, height);
        var sigNoisy = FrameSignature.Compute(noisy, width, height);

        Assert.True(FrameSignature.Similar(sigBase, sigNoisy, 8, 0.0));
    }

    [Fact]
    public void Similar_ToleranceBoundary_UsesStrictGreaterThan()
    {
        var baseSig = FrameSignature.Compute(MakeGrayBuffer(32, 32, 100), 32, 32);
        var plus8 = FrameSignature.Compute(MakeGrayBuffer(32, 32, 108), 32, 32);
        var plus10 = FrameSignature.Compute(MakeGrayBuffer(32, 32, 110), 32, 32);

        // Exactly at the tolerance: strict ">" means this still counts as similar.
        Assert.True(FrameSignature.Similar(baseSig, plus8, 8, 0.0));
        // Past the tolerance: no longer similar.
        Assert.False(FrameSignature.Similar(baseSig, plus10, 8, 0.0));
    }

    [Fact]
    public void Similar_FractionSemantics_OneDifferingCellOfManyPassesAtHighFraction()
    {
        // 256x16 with targetCellPx=16 divides evenly into 16 columns x 1 row = 16 cells.
        const int width = 256, height = 16;
        var white = MakeGrayBuffer(width, height, 255);
        var mutated = MakeGrayBuffer(width, height, 255);
        FillRect(mutated, width, 80, 0, 16, 16, b: 0, g: 0, r: 0); // cell column 5

        var sigWhite = FrameSignature.Compute(white, width, height);
        var sigMutated = FrameSignature.Compute(mutated, width, height);

        Assert.Equal(16, sigWhite.Cols);
        Assert.Equal(1, sigWhite.Rows);

        // Strict: any single differing cell fails.
        Assert.False(FrameSignature.Similar(sigWhite, sigMutated, 8, 0.0));
        // Tolerant: 1 of 16 cells is within an allowance of 8 (floor(16*0.5)).
        Assert.True(FrameSignature.Similar(sigWhite, sigMutated, 8, 0.5));
    }

    [Fact]
    public void Compute_NonDivisibleDimensions_CoversEveryPixelExactlyOnce()
    {
        // 50x16 -> cols=round(50/16)=3, rows=round(34/16)=2: an uneven partition
        // (columns 16/17/17 wide, rows 17/17 tall) that still must cover every
        // pixel with no gaps or overlaps. Changing only the single bottom-right
        // pixel should nudge only the last cell's mean and leave every other
        // cell exactly unchanged -- pinning that the boundary math assigns each
        // pixel to exactly one cell.
        const int width = 50, height = 34;
        var baseBuf = MakeGrayBuffer(width, height, 128);
        var mutated = MakeGrayBuffer(width, height, 128);
        SetPixel(mutated, width, width - 1, height - 1, 0, 0, 0);

        var sigBase = FrameSignature.Compute(baseBuf, width, height);
        var sigMutated = FrameSignature.Compute(mutated, width, height);

        Assert.Equal(3, sigBase.Cols);
        Assert.Equal(2, sigBase.Rows);

        // Every cell except the last (col 2, row 1, which contains the changed
        // bottom-right pixel) must be byte-for-byte unaffected.
        for (int row = 0; row < sigBase.Rows; row++)
        {
            for (int col = 0; col < sigBase.Cols; col++)
            {
                if (col == sigBase.Cols - 1 && row == sigBase.Rows - 1)
                    Assert.NotEqual(sigBase.CellLuma(col, row), sigMutated.CellLuma(col, row));
                else
                    Assert.Equal(sigBase.CellLuma(col, row), sigMutated.CellLuma(col, row));
            }
        }
    }

    [Fact]
    public void Compute_TinyRegion_ClampsToOneCellAndComputesExactLuma()
    {
        var buf = MakeSolidBuffer(5, 5, b: 90, g: 200, r: 10);
        var sig = FrameSignature.Compute(buf, 5, 5);

        Assert.Equal(1, sig.Cols);
        Assert.Equal(1, sig.Rows);
        // (77*10 + 150*200 + 29*90) >> 8 = 33380 >> 8 = 130.
        Assert.Equal(130, sig.CellLuma(0, 0));
    }

    [Fact]
    public void Compute_LargeRegion_ClampsGridToMaxGridDim()
    {
        const int width = 16 * 100, height = 16 * 100; // 1600x1600, ~10MB buffer
        var buf = MakeGrayBuffer(width, height, 64);

        var sig = FrameSignature.Compute(buf, width, height);

        Assert.Equal(64, sig.Cols);
        Assert.Equal(64, sig.Rows);
    }

    [Fact]
    public void Similar_MismatchedGridDimensions_ReturnsFalseWithoutThrowing()
    {
        var sigSmall = FrameSignature.Compute(MakeGrayBuffer(64, 64, 100), 64, 64);
        var sigWide = FrameSignature.Compute(MakeGrayBuffer(128, 64, 100), 128, 64);

        Assert.NotEqual(sigSmall.Cols, sigWide.Cols);
        Assert.False(FrameSignature.Similar(sigSmall, sigWide, 8, 1.0));
    }

    [Fact]
    public void Compute_BufferSmallerThanStatedDimensions_ThrowsArgumentException()
    {
        var tooSmall = new byte[10];
        Assert.Throws<ArgumentException>(() => FrameSignature.Compute(tooSmall, 5, 5));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    public void Compute_NonPositiveDimensions_ThrowsArgumentOutOfRangeException(int width, int height)
    {
        var buf = new byte[100];
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameSignature.Compute(buf, width, height));
    }
}
