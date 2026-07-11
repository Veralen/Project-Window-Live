using System;
using ScreenTranslator.App.Native;
using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Ocr;

namespace ScreenTranslator.App.Capture;

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

        IntPtr hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, vw, vh);
        IntPtr hOld = NativeMethods.SelectObject(hdcMem, hBitmap);
        try
        {
            NativeMethods.BitBlt(hdcMem, 0, 0, vw, vh, hdcScreen, vx, vy,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            var buffer = new byte[vw * vh * 4];
            var bi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = vw,
                    biHeight = -vh, // negative => top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                }
            };
            NativeMethods.GetDIBits(hdcMem, hBitmap, 0, (uint)vh, buffer, ref bi, NativeMethods.DIB_RGB_COLORS);
            return new FrozenCapture(buffer, vw, vh, vx, vy);
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
    /// Core <see cref="CapturedRegion"/>. Clamps the rect to the captured bounds.
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
