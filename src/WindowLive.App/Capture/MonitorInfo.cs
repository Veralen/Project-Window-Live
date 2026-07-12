using System;
using System.Collections.Generic;
using WindowLive.App.Native;
using WindowLive.Core.Geometry;

namespace WindowLive.App.Capture;

/// <summary>
/// A physical monitor. All rectangles are physical pixels in virtual-screen
/// coordinates. <see cref="Dpi"/> is the effective DPI (96 = 100% scaling).
/// </summary>
internal sealed record MonitorInfo(
    IntPtr Handle,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint Dpi,
    bool IsPrimary)
{
    /// <summary>WPF scale factor: physical = dip * ScaleFactor.</summary>
    public double ScaleFactor => Dpi / 96.0;

    public static IReadOnlyList<MonitorInfo> EnumerateAll()
    {
        var result = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc callback = (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rc, IntPtr data) =>
        {
            var mi = new NativeMethods.MONITORINFOEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(hMon, ref mi))
            {
                uint dpiX = 96, dpiY = 96;
                NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
                result.Add(new MonitorInfo(
                    hMon,
                    ToRect(mi.rcMonitor),
                    ToRect(mi.rcWork),
                    dpiX,
                    (mi.dwFlags & 1u) != 0)); // MONITORINFOF_PRIMARY = 1
            }
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return result;
    }

    /// <summary>Finds the monitor containing a virtual-screen physical point (nearest fallback).</summary>
    public static MonitorInfo ForPoint(IReadOnlyList<MonitorInfo> monitors, PixelPoint p)
    {
        foreach (var m in monitors)
            if (m.Bounds.Contains(p)) return m;
        // Fallback: nearest by center distance.
        MonitorInfo best = monitors[0];
        double bestDist = double.MaxValue;
        foreach (var m in monitors)
        {
            double dx = m.Bounds.Center.X - p.X, dy = m.Bounds.Center.Y - p.Y;
            double d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = m; }
        }
        return best;
    }

    private static PixelRect ToRect(NativeMethods.RECT r) =>
        new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
