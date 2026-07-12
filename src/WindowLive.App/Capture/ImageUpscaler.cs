using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace WindowLive.App.Capture;

/// <summary>
/// Shared "captured region -> 2x upscale -> PNG bytes" helper for the image
/// translation path (docs/window-live-design.md "Image input"). Live testing
/// against the model established that transcription quality requires 2x
/// upscaling of the captured crop before it is sent: at 1x the model misreads
/// on-screen chat text, while at 2x it reads Spanish and Japanese chat lines
/// correctly. Both <c>SnipController</c> and <c>GameModeController</c> route
/// their image encoding through here so the upscale factor and PNG encoding
/// logic live in exactly one place instead of being duplicated per call site.
/// Everything here is in-memory only — no disk writes (CLAUDE.md hard rule).
/// </summary>
internal static class ImageUpscaler
{
    private const double UpscaleFactor = 2.0;

    /// <summary>
    /// Ceiling on the pixel area of the encoded image. Vision-token cost scales
    /// with pixel area (~1 token per ~1000 px² observed with this model's mmproj),
    /// and the llama-server context is finite — an unbounded 2x upscale of a large
    /// snip produced 1200+ image tokens and a 400 exceed_context_size_error from
    /// the server. ~1.0 MP keeps worst-case image tokens near ~1000, safely inside
    /// a 2048-token slot alongside the instruction and generation budget.
    /// </summary>
    private const double MaxOutputPixels = 1_000_000;

    /// <summary>
    /// Upscales <paramref name="region"/> up to 2x with high-quality bicubic
    /// interpolation — reduced proportionally so the output never exceeds
    /// <see cref="MaxOutputPixels"/> (large regions may get no upscale at all) —
    /// and PNG-encodes the result. All intermediate GDI+ objects (source/scaled
    /// bitmaps, the drawing surface) are disposed before returning; nothing
    /// touches disk.
    /// </summary>
    public static byte[] EncodeUpscaledPng(CapturedRegion region)
    {
        // The pinned handle must stay alive for as long as GDI+ reads through
        // the Bitmap constructed directly over this array's memory below —
        // otherwise the GC is free to move/collect the array mid-draw.
        GCHandle handle = GCHandle.Alloc(region.PixelsBgra32, GCHandleType.Pinned);
        try
        {
            int stride = region.PixelWidth * 4;
            using var source = new Bitmap(
                region.PixelWidth, region.PixelHeight, stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());

            double area = (double)region.PixelWidth * region.PixelHeight;
            double scale = Math.Min(UpscaleFactor, Math.Sqrt(MaxOutputPixels / area));
            scale = Math.Max(scale, 1.0); // never downscale below the captured size
            int scaledWidth = Math.Max(1, (int)Math.Round(region.PixelWidth * scale));
            int scaledHeight = Math.Max(1, (int)Math.Round(region.PixelHeight * scale));
            using var scaled = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, 0, 0, scaledWidth, scaledHeight);
            }

            using var ms = new MemoryStream();
            scaled.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}
