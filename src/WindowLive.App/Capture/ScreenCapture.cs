using System;
using WindowLive.App.Native;
using WindowLive.Core.Geometry;

namespace WindowLive.App.Capture;

/// <summary>
/// A cropped region of a <see cref="FrozenCapture"/>: 32bpp BGRA pixels, top-down,
/// no stride padding, plus the physical virtual-screen bounds it was cropped from.
/// </summary>
internal sealed record CapturedRegion(byte[] PixelsBgra32, int PixelWidth, int PixelHeight, PixelRect Bounds);

/// <summary>
/// A frozen full-virtual-screen screenshot in 32bpp BGRA, top-down, no stride
/// padding. Captured with GDI BitBlt BEFORE any overlay UI is shown, so it never
/// contains our own dim layer (docs/architecture.md §Capture-first rule).
/// </summary>
internal sealed class FrozenCapture
{
    public byte[] PixelsBgra32 { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Virtual-screen origin (physical px). Can be negative.</summary>
    public int OriginX { get; }
    public int OriginY { get; }

    public PixelRect VirtualBounds => new(OriginX, OriginY, Width, Height);

    private FrozenCapture(byte[] pixels, int width, int height, int originX, int originY)
    {
        PixelsBgra32 = pixels;
        Width = width;
        Height = height;
        OriginX = originX;
        OriginY = originY;
    }

    /// <summary>BitBlt the entire virtual screen into a top-down BGRA32 buffer.</summary>
    public static FrozenCapture CaptureVirtualScreen()
    {
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vw <= 0) vw = 1;
        if (vh <= 0) vh = 1;

        byte[] pixels = CaptureRaw(vx, vy, vw, vh);
        return new FrozenCapture(pixels, vw, vh, vx, vy);
    }

    /// <summary>
    /// Captures just a virtual-screen physical rectangle directly — a single
    /// BitBlt scoped to that rect, not a full-screen capture + crop. Used by
    /// game mode's polling loop (docs/window-live-design.md "Live loop"), which
    /// grabs the same small region many times per second and would otherwise
    /// waste work re-capturing (and cropping out of) the whole virtual screen
    /// every 300ms.
    /// </summary>
    public static CapturedRegion CaptureRegion(PixelRect region)
    {
        int x = (int)Math.Round(region.X);
        int y = (int)Math.Round(region.Y);
        int w = Math.Max(1, (int)Math.Round(region.Width));
        int h = Math.Max(1, (int)Math.Round(region.Height));

        byte[] pixels = CaptureRaw(x, y, w, h);
        return new CapturedRegion(pixels, w, h, new PixelRect(x, y, w, h));
    }

    /// <summary>
    /// Shared BitBlt + GetDIBits capture of one screen-coordinate rectangle into
    /// a top-down 32bpp BGRA buffer (no stride padding), used by both
    /// <see cref="CaptureVirtualScreen"/> and <see cref="CaptureRegion"/>.
    /// <paramref name="x"/>/<paramref name="y"/> are physical virtual-screen
    /// coordinates (can be negative); <paramref name="w"/>/<paramref name="h"/>
    /// must be &gt;= 1.
    /// </summary>
    private static byte[] CaptureRaw(int x, int y, int w, int h)
    {
        IntPtr hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, w, h);
        IntPtr hOld = NativeMethods.SelectObject(hdcMem, hBitmap);
        try
        {
            NativeMethods.BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            var buffer = new byte[w * h * 4];
            var bi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // negative => top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                }
            };
            NativeMethods.GetDIBits(hdcMem, hBitmap, 0, (uint)h, buffer, ref bi, NativeMethods.DIB_RGB_COLORS);
            return buffer;
        }
        finally
        {
            NativeMethods.SelectObject(hdcMem, hOld);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(hdcMem);
            NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>
    /// Crops a virtual-screen physical rectangle out of the frozen capture into a
    /// <see cref="CapturedRegion"/>. Clamps the rect to the captured bounds.
    /// </summary>
    public CapturedRegion Crop(PixelRect selection)
    {
        // Clamp to captured bounds and snap to integer pixels.
        int sx = (int)Math.Round(selection.X);
        int sy = (int)Math.Round(selection.Y);
        int sw = (int)Math.Round(selection.Width);
        int sh = (int)Math.Round(selection.Height);

        int left = Math.Max(sx, OriginX);
        int top = Math.Max(sy, OriginY);
        int right = Math.Min(sx + sw, OriginX + Width);
        int bottom = Math.Min(sy + sh, OriginY + Height);
        int cw = Math.Max(1, right - left);
        int ch = Math.Max(1, bottom - top);

        var dst = new byte[cw * ch * 4];
        int srcStride = Width * 4;
        int dstStride = cw * 4;
        for (int row = 0; row < ch; row++)
        {
            int srcY = (top - OriginY) + row;
            int srcOffset = srcY * srcStride + (left - OriginX) * 4;
            int dstOffset = row * dstStride;
            Buffer.BlockCopy(PixelsBgra32, srcOffset, dst, dstOffset, dstStride);
        }

        return new CapturedRegion(dst, cw, ch, new PixelRect(left, top, cw, ch));
    }
}
