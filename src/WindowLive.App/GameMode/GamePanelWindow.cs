using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WindowLive.App.Capture;
using WindowLive.App.Native;
using WindowLive.Core.Geometry;
using WindowLive.Core.Overlay;

namespace WindowLive.App.GameMode;

/// <summary>
/// Persistent, always-topmost overlay panel for continuous game chat
/// translation (docs/window-live-design.md "Persistent overlay panel").
/// Anchored below the saved chat region by default, flipped above if that
/// would exit the monitor bounds — using Core's <see cref="ChipPlacement"/>,
/// the same placement math as the desktop snip chip. Semi-opaque dark
/// background, light text, subtle border — matches the snip chip's styling.
///
/// Click-through and non-activating: after the native window handle exists,
/// <see cref="NativeMethods.WS_EX_NOACTIVATE"/> | <see cref="NativeMethods.WS_EX_TRANSPARENT"/>
/// are OR'd into GWL_EXSTYLE, so the panel never takes keyboard focus and all
/// mouse input passes through to whatever is beneath it (the game). There is
/// no dismiss gesture by design — only <see cref="GameModeController"/> (via
/// <see cref="HidePanel"/>) closes it.
///
/// All placement math is done in physical pixels, virtual-screen coordinates
/// (Core's convention); DIP conversion happens only here, at the WPF
/// rendering boundary, mirroring <c>OverlayWindow.ShowOnMonitor</c>/
/// <c>VirtualToDipRect</c>.
/// </summary>
internal sealed class GamePanelWindow : Window
{
    private readonly Border _border;
    private readonly TextBlock _text;

    private MonitorInfo? _monitor;
    private int _panelX;
    private int _panelY;
    private int _panelWidthPhysical;

    public GamePanelWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
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

        _border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = _text,
        };
        Content = _border;

        SourceInitialized += (_, _) => ApplyClickThroughStyles();
    }

    /// <summary>
    /// OR's WS_EX_NOACTIVATE | WS_EX_TRANSPARENT (plus WS_EX_TOOLWINDOW, so the
    /// panel never gets an Alt-Tab/taskbar entry) into the window's extended
    /// style once its HWND exists. Must run after SourceInitialized — the
    /// style bits are meaningless before the native window is created.
    /// </summary>
    private void ApplyClickThroughStyles()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Shows the panel anchored to <paramref name="region"/> on <paramref name="monitor"/>
    /// (or moves it there if already shown — called again after a region redefine).
    /// </summary>
    public void ShowFor(PixelRect region, MonitorInfo monitor)
    {
        _monitor = monitor;

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

        var helper = new WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero)
        {
            helper.EnsureHandle();
            PlaceWindow(helper.Handle, (int)Math.Round(placement.Height));
            Show();
        }
        PlaceWindow(helper.Handle, (int)Math.Round(placement.Height));
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
        ResizeToFitContent();
    }

    /// <summary>Appends a streamed fragment and grows the window to fit. UI thread only.</summary>
    public void AppendText(string fragment)
    {
        _text.Text += fragment;
        ResizeToFitContent();
    }

    /// <summary>
    /// Re-measures the content at the fixed panel width and resizes the native
    /// window's height to match — top-left stays put, so the panel grows
    /// downward (matches the snip chip's "grows downward" behavior).
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

    /// <summary>Hides the panel (game mode stopped). No dismiss gesture reaches this — only the controller does.</summary>
    public void HidePanel()
    {
        try { Close(); } catch { /* ignore */ }
    }

    private static double ToDip(double physical, MonitorInfo monitor) => physical / monitor.ScaleFactor;
    private static double ToPhysical(double dip, MonitorInfo monitor) => dip * monitor.ScaleFactor;
}
