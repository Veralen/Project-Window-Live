using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowLive.App.Capture;
using WindowLive.App.Native;
using WindowLive.Core.Geometry;

namespace WindowLive.App.Overlay;

/// <summary>
/// Shared capture-first, per-monitor overlay construction used by both the
/// one-shot snip pipeline (<see cref="SnipController"/>) and game-mode region
/// setup (<see cref="GameMode.GameModeController"/>) — the "one overlay per
/// monitor, showing that monitor's crop of a frozen full-screen capture" part
/// of docs/window-live-design.md is identical for both flows; only what
/// happens after a selection differs.
/// </summary>
internal static class OverlaySetup
{
    /// <summary>
    /// Builds (but does not show) one <see cref="OverlayWindow"/> per monitor
    /// from an already-captured <see cref="FrozenCapture"/>.
    /// </summary>
    public static List<OverlayWindow> CreateOverlays(FrozenCapture frozen, IReadOnlyList<MonitorInfo> monitors)
    {
        int stride = frozen.Width * 4;
        var full = BitmapSource.Create(frozen.Width, frozen.Height, 96, 96,
            PixelFormats.Bgra32, null, frozen.PixelsBgra32, stride);
        full.Freeze();

        var overlays = new List<OverlayWindow>(monitors.Count);
        foreach (var mon in monitors)
        {
            int cx = (int)(mon.Bounds.X - frozen.OriginX);
            int cy = (int)(mon.Bounds.Y - frozen.OriginY);
            int cw = Math.Min((int)mon.Bounds.Width, frozen.Width - cx);
            int ch = Math.Min((int)mon.Bounds.Height, frozen.Height - cy);
            ImageSource crop = new CroppedBitmap(full,
                new Int32Rect(Math.Max(0, cx), Math.Max(0, cy), Math.Max(1, cw), Math.Max(1, ch)));
            overlays.Add(new OverlayWindow(mon, crop));
        }
        return overlays;
    }

    /// <summary>Activates and focuses whichever overlay is on the monitor under the cursor.</summary>
    public static void FocusOverlayUnderCursor(IReadOnlyList<OverlayWindow> overlays, IReadOnlyList<MonitorInfo> monitors)
    {
        if (overlays.Count == 0) return;
        OverlayWindow target = overlays[0];
        if (NativeMethods.GetCursorPos(out var pt))
        {
            var mon = MonitorInfo.ForPoint(monitors, new PixelPoint(pt.X, pt.Y));
            foreach (var o in overlays)
                if (o.Monitor.Handle == mon.Handle) { target = o; break; }
        }
        target.Activate();
        target.Focus();
    }
}
