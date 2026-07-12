using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WindowLive.App.Capture;
using WindowLive.App.Logging;
using WindowLive.App.Native;
using WindowLive.Core.Config;
using WindowLive.Core.Geometry;
using WindowLive.Core.Overlay;

namespace WindowLive.App.GameMode;

/// <summary>
/// Persistent, always-topmost overlay panel for continuous game chat
/// translation (docs/window-live-design.md "Persistent overlay panel"). By
/// default it is anchored below the saved chat region — using Core's
/// <see cref="ChipPlacement"/>, the same placement math as the desktop snip
/// chip — and auto-grows downward as text streams in. Once the user drags or
/// resizes it, the resulting rect (<see cref="AppConfig.GamePanelRect"/>) is
/// saved and reused verbatim on every later <see cref="ShowFor"/> call, on
/// whichever monitor it currently overlaps.
///
/// This is a normal, mouse-interactive window: it is draggable by clicking
/// anywhere in its interior and resizable from its edges/corners (a custom
/// <c>WM_NCHITTEST</c> hook below implements this over a borderless
/// <see cref="WindowStyle.None"/> window). It is always-topmost and never
/// takes keyboard focus (<see cref="NativeMethods.WS_EX_NOACTIVATE"/>) and
/// never appears in Alt-Tab or the taskbar (<see cref="NativeMethods.WS_EX_TOOLWINDOW"/>),
/// so the game underneath keeps keyboard input at all times. The trade-off is
/// intentional: mouse clicks that land on the panel itself no longer reach the
/// game beneath it (this is no longer a click-through overlay) — only the area
/// the panel occupies is affected, and the user controls where that is by
/// moving/resizing it. There is no dismiss gesture by design — only
/// <see cref="GameModeController"/> (via <see cref="HidePanel"/>) closes it.
///
/// All placement math is done in physical pixels, virtual-screen coordinates
/// (Core's convention); DIP conversion happens only here, at the WPF
/// rendering boundary, mirroring <c>OverlayWindow.ShowOnMonitor</c>/
/// <c>VirtualToDipRect</c>.
/// </summary>
internal sealed class GamePanelWindow : Window
{
    private readonly AppConfig _config;
    private readonly Border _border;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _text;

    private MonitorInfo? _monitor;
    private int _panelX;
    private int _panelY;
    private int _panelWidthPhysical;

    /// <summary>
    /// True once the panel is being shown at a user-chosen rect (either restored
    /// from <see cref="AppConfig.GamePanelRect"/> or set mid-session by a live
    /// drag/resize). In this mode the content fills whatever size the OS gives
    /// the window and streaming only scrolls — it never calls SetWindowPos,
    /// which would otherwise race the user's own in-progress resize.
    /// </summary>
    private bool _userSized;

    public GamePanelWindow(AppConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "WindowLive Game Chat Translation";

        _text = new TextBlock
        {
            Text = string.Empty,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _text,
        };

        _border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = _scroll,
        };
        Content = _border;

        SourceInitialized += (_, _) =>
        {
            ApplyNonActivatingStyle();
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
    }

    /// <summary>
    /// OR's WS_EX_NOACTIVATE (never receives keyboard focus/activation) and
    /// WS_EX_TOOLWINDOW (no taskbar/Alt-Tab entry) into the window's extended
    /// style once its HWND exists. Must run after SourceInitialized — the
    /// style bits are meaningless before the native window is created.
    /// Deliberately does NOT include WS_EX_TRANSPARENT: the panel is a normal
    /// mouse-interactive window now, not click-through (see class doc comment).
    /// </summary>
    private void ApplyNonActivatingStyle()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Implements custom hit-testing (drag-anywhere, resize from edges/corners)
    /// over this borderless window, and persists the panel's rect to
    /// <see cref="AppConfig.GamePanelRect"/> whenever the user finishes moving
    /// or resizing it. Also enforces a minimum track size.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                handled = true;
                return (IntPtr)HitTest(hwnd, lParam);

            case NativeMethods.WM_ENTERSIZEMOVE:
                // Switch to user-sized layout immediately so the first live
                // resize reflows content instead of snapping at the end.
                _userSized = true;
                _border.Width = double.NaN;
                break;

            case NativeMethods.WM_EXITSIZEMOVE:
                SavePanelRect(hwnd);
                break;

            case NativeMethods.WM_GETMINMAXINFO:
                ApplyMinTrackSize(lParam);
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// WM_NCHITTEST: lParam packs screen coordinates as two SIGNED 16-bit
    /// values — plain int masking breaks on monitors left of/above the primary
    /// monitor, where virtual-screen coordinates go negative. GetWindowRect
    /// gives the window's own bounds (physical px, screen/virtual-screen
    /// coordinates, matching lParam). The resize margin is 8 DIPs scaled by
    /// the panel's current per-monitor DPI. Corners take priority over edges;
    /// everything else in the interior is HTCAPTION (drag anywhere).
    /// </summary>
    private int HitTest(IntPtr hwnd, IntPtr lParam)
    {
        int x = unchecked((short)((long)lParam & 0xFFFF));
        int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
            return NativeMethods.HTCLIENT;

        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int margin = Math.Max(1, (int)Math.Round(8 * dpiScale));

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        int localX = x - rect.Left;
        int localY = y - rect.Top;

        bool onLeft = localX < margin;
        bool onRight = localX >= width - margin;
        bool onTop = localY < margin;
        bool onBottom = localY >= height - margin;

        if (onTop && onLeft) return NativeMethods.HTTOPLEFT;
        if (onTop && onRight) return NativeMethods.HTTOPRIGHT;
        if (onBottom && onLeft) return NativeMethods.HTBOTTOMLEFT;
        if (onBottom && onRight) return NativeMethods.HTBOTTOMRIGHT;
        if (onLeft) return NativeMethods.HTLEFT;
        if (onRight) return NativeMethods.HTRIGHT;
        if (onTop) return NativeMethods.HTTOP;
        if (onBottom) return NativeMethods.HTBOTTOM;
        return NativeMethods.HTCAPTION;
    }

    /// <summary>Reads the window's current rect and saves it as the persisted panel rect (same save pattern as <see cref="GameModeController.OnRegionSelected"/>).</summary>
    private void SavePanelRect(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
            return;

        var pixelRect = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        _config.GamePanelRect = pixelRect;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            AppLog.Write($"[GamePanelWindow] failed to save panel rect to config: {ex.Message}");
        }
    }

    private static void ApplyMinTrackSize(IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        mmi.ptMinTrackSize = new NativeMethods.POINT(160, 48);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    /// <summary>
    /// Shows the panel for this cycle's chat region (or moves/resizes it there
    /// if already shown — called again after a region redefine). If a saved
    /// panel rect exists and still overlaps some monitor, the panel is placed
    /// exactly there (user-sized mode) instead of auto-placed below the chat
    /// region — the saved rect and the chat region are otherwise unrelated.
    /// </summary>
    public void ShowFor(PixelRect region, MonitorInfo monitor)
    {
        _monitor = monitor;

        var monitors = MonitorInfo.EnumerateAll();
        PixelRect saved = _config.GamePanelRect;
        bool useSaved = !saved.IsEmpty && monitors.Any(m => saved.IntersectsWith(m.Bounds));

        int placementHeightPhysical;
        if (useSaved)
        {
            _userSized = true;
            _border.Width = double.NaN;

            _panelX = (int)Math.Round(saved.X);
            _panelY = (int)Math.Round(saved.Y);
            _panelWidthPhysical = (int)Math.Round(saved.Width);
            placementHeightPhysical = (int)Math.Round(saved.Height);
        }
        else
        {
            _userSized = false;

            double maxWidthDip = Math.Clamp(ToDip(region.Width, monitor) * 1.2, 200.0, 640.0);
            _border.Width = maxWidthDip;

            // Single-line estimate for initial placement, same pattern as the snip
            // chip (OverlayWindow.ShowTranslationChip) — height then simply grows
            // downward as text streams in via AppendText/ResizeToFitContent.
            _border.Measure(new Size(maxWidthDip, double.PositiveInfinity));
            double estimatedHeightDip = Math.Max(36, _border.DesiredSize.Height);

            _panelWidthPhysical = (int)Math.Round(ToPhysical(maxWidthDip, monitor));
            int panelHeightPhysical = (int)Math.Round(ToPhysical(estimatedHeightDip, monitor));
            int gapPhysical = (int)Math.Round(ToPhysical(8, monitor));

            PixelRect placement = ChipPlacement.Place(region, _panelWidthPhysical, panelHeightPhysical, monitor.Bounds, gapPhysical);
            _panelX = (int)Math.Round(placement.X);
            _panelY = (int)Math.Round(placement.Y);
            placementHeightPhysical = (int)Math.Round(placement.Height);
        }

        var helper = new WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero)
        {
            helper.EnsureHandle();
            PlaceWindow(helper.Handle, placementHeightPhysical);
        }
        // Show() must run on every re-show, not only on first handle creation:
        // HidePanel() hides via WPF (Visibility = Hidden) and the handle survives,
        // so SetWindowPos alone cannot bring the window back — WPF still considers
        // itself hidden and the panel stays invisible while the loop translates.
        if (!IsVisible)
            Show();
        PlaceWindow(helper.Handle, placementHeightPhysical);
    }

    private void PlaceWindow(IntPtr hwnd, int heightPhysical)
    {
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            _panelX, _panelY, _panelWidthPhysical, Math.Max(1, heightPhysical),
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Clears the panel for a new translation stream. UI thread only.</summary>
    public void BeginNewTranslation()
    {
        _text.Text = string.Empty;
        if (_userSized)
            _scroll.ScrollToEnd();
        else
            ResizeToFitContent();
    }

    /// <summary>Appends a streamed fragment. UI thread only. Auto-grow mode resizes the native window to fit; user-sized mode only scrolls (never fights an in-progress user resize).</summary>
    public void AppendText(string fragment)
    {
        _text.Text += fragment;
        if (_userSized)
            _scroll.ScrollToEnd();
        else
            ResizeToFitContent();
    }

    /// <summary>
    /// Re-measures the content at the fixed panel width and resizes the native
    /// window's height to match — top-left stays put, so the panel grows
    /// downward (matches the snip chip's "grows downward" behavior). Auto-grow
    /// mode only — never called while <see cref="_userSized"/> is true.
    /// </summary>
    private void ResizeToFitContent()
    {
        if (_monitor is null) return;
        _border.Measure(new Size(_border.Width, double.PositiveInfinity));
        double heightDip = Math.Max(36, _border.DesiredSize.Height);
        int heightPhysical = (int)Math.Round(ToPhysical(heightDip, _monitor));

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            PlaceWindow(hwnd, heightPhysical);
    }

    /// <summary>
    /// Hides the panel (game mode stopped). Hide, not Close: WPF forbids showing
    /// a Window again after Close, and this instance is reused across game-mode
    /// restarts — closing here made the second Ctrl+Shift+G setup throw
    /// InvalidOperationException in <see cref="ShowFor"/>. No dismiss gesture
    /// reaches this — only the controller does.
    /// </summary>
    public void HidePanel()
    {
        try { Hide(); } catch { /* ignore */ }
    }

    /// <summary>Permanently closes the panel — app shutdown only, never between game-mode sessions.</summary>
    public void CloseForExit()
    {
        try { Close(); } catch { /* ignore */ }
    }

    private static double ToDip(double physical, MonitorInfo monitor) => physical / monitor.ScaleFactor;
    private static double ToPhysical(double dip, MonitorInfo monitor) => dip * monitor.ScaleFactor;
}
