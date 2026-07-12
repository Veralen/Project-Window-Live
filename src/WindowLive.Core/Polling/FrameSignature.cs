using System;

namespace WindowLive.Core.Polling;

/// <summary>
/// Downsampled, noise-tolerant fingerprint of a captured frame: a grid of
/// per-cell mean luminances computed from the raw BGRA pixels. Replaces exact
/// byte hashing for game-mode change detection (see docs/window-live-design.md
/// "Live loop") because a live game never repeats bit-identically — animated
/// scenes behind translucent chat, dithering, and per-frame noise change raw
/// bytes constantly even when the on-screen text is unchanged. Averaging
/// 100s of pixels per cell cancels per-pixel noise, while a new chat line
/// shifts the means of the cells it covers by a large margin.
///
/// The grid is partitioned with scaled integer boundaries
/// (xStart = cx * width / cols) so every pixel is covered exactly once
/// regardless of divisibility — no dropped remainder strip at the region
/// edges, where a newly arrived chat line is most likely to sit.
/// </summary>
public sealed class FrameSignature
{
    /// <summary>Approximate cell edge in physical pixels.</summary>
    public const int DefaultTargetCellPx = 16;

    /// <summary>Cap on grid columns/rows so huge regions stay cheap to compare.</summary>
    public const int DefaultMaxGridDim = 64;

    public int Cols { get; }
    public int Rows { get; }

    // Row-major per-cell mean luminance, length Cols * Rows.
    private readonly byte[] _cellLuma;

    private FrameSignature(int cols, int rows, byte[] cellLuma)
    {
        Cols = cols;
        Rows = rows;
        _cellLuma = cellLuma;
    }

    /// <summary>Mean luminance (0-255) of one grid cell — exposed for tests.</summary>
    public byte CellLuma(int col, int row) => _cellLuma[row * Cols + col];

    /// <summary>
    /// Computes the signature of a top-down 32bpp BGRA buffer (no stride
    /// padding — the layout produced by the capture pipeline). Luminance is
    /// integer BT.601: (77R + 150G + 29B) >> 8. Alpha is ignored.
    /// </summary>
    public static FrameSignature Compute(ReadOnlySpan<byte> bgra32, int width, int height,
        int targetCellPx = DefaultTargetCellPx, int maxGridDim = DefaultMaxGridDim)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (targetCellPx <= 0) throw new ArgumentOutOfRangeException(nameof(targetCellPx));
        if (maxGridDim <= 0) throw new ArgumentOutOfRangeException(nameof(maxGridDim));
        if (bgra32.Length < (long)width * height * 4)
            throw new ArgumentException($"Buffer too small: {bgra32.Length} bytes for {width}x{height} BGRA32.", nameof(bgra32));

        int cols = Math.Clamp((int)Math.Round(width / (double)targetCellPx), 1, maxGridDim);
        int rows = Math.Clamp((int)Math.Round(height / (double)targetCellPx), 1, maxGridDim);

        var cellLuma = new byte[cols * rows];
        for (int cy = 0; cy < rows; cy++)
        {
            int yStart = (int)((long)cy * height / rows);
            int yEnd = (int)((long)(cy + 1) * height / rows);
            for (int cx = 0; cx < cols; cx++)
            {
                int xStart = (int)((long)cx * width / cols);
                int xEnd = (int)((long)(cx + 1) * width / cols);

                long sum = 0;
                for (int y = yStart; y < yEnd; y++)
                {
                    int rowOffset = y * width * 4;
                    for (int x = xStart; x < xEnd; x++)
                    {
                        int i = rowOffset + x * 4;
                        byte b = bgra32[i];
                        byte g = bgra32[i + 1];
                        byte r = bgra32[i + 2];
                        sum += (77 * r + 150 * g + 29 * b) >> 8;
                    }
                }
                int pixelCount = (xEnd - xStart) * (yEnd - yStart);
                cellLuma[cy * cols + cx] = (byte)(sum / pixelCount);
            }
        }
        return new FrameSignature(cols, rows, cellLuma);
    }

    /// <summary>
    /// Tolerant comparison: true when at most
    /// floor(cellCount * <paramref name="maxDifferingFraction"/>) cells differ
    /// by more than <paramref name="levelTolerance"/> luminance levels. A
    /// fraction of 0 means strict: any single cell over the tolerance makes the
    /// frames dissimilar. Signatures from different grid dimensions (a resized
    /// or redefined region) are never similar — callers treat that as a change
    /// rather than an error.
    /// </summary>
    public static bool Similar(FrameSignature a, FrameSignature b, int levelTolerance, double maxDifferingFraction)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Cols != b.Cols || a.Rows != b.Rows)
            return false;

        int cellCount = a._cellLuma.Length;
        int allowed = (int)(cellCount * maxDifferingFraction);
        int differing = 0;
        for (int i = 0; i < cellCount; i++)
        {
            int diff = a._cellLuma[i] - b._cellLuma[i];
            if (diff > levelTolerance || diff < -levelTolerance)
            {
                differing++;
                if (differing > allowed)
                    return false;
            }
        }
        return true;
    }
}
