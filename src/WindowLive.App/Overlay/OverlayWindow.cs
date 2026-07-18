using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WindowLive.App.Capture;
using WindowLive.App.Native;
using WindowLive.App.Ui;
using WindowLive.Core.Geometry;

namespace WindowLive.App.Overlay;

/// <summary>
/// One borderless, topmost overlay window per physical monitor. Shows that
/// monitor's crop of the frozen screenshot, dimmed ~40%, with a crosshair cursor.
/// The user drags a selection rectangle (confined to this monitor — Stage 1
/// limitation). All Core data stays physical px; conversion to DIPs happens here
/// using the monitor's own DPI.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly Grid _root;
    private readonly Image _background;
    private readonly Canvas _canvas;
    private readonly Path _dimPath;
    private readonly System.Windows.Shapes.Rectangle _selectionBorder;
    private readonly System.Windows.Shapes.Rectangle _selectionFill;
    private Border? _indicator;
    private TextBlock? _indicatorText;

    private bool _dragging;
    private Point _dragStartDip;
    private PixelRect _selectionPhysical; // virtual-screen physical px
    private bool _hasSelection;

    /// <summary>Raised when the user completes a selection on this monitor (physical px rect).</summary>
    public event Action<OverlayWindow, PixelRect>? SelectionCompleted;

    /// <summary>Raised when the user asks to dismiss the whole overlay (Esc, close button, click-out).</summary>
    public event Action? DismissRequested;

    public MonitorInfo Monitor => _monitor;
    public PixelRect Selection => _selectionPhysical;

    public OverlayWindow(MonitorInfo monitor, ImageSource background)
    {
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Title = "WindowLive Overlay";

        _background = new Image { Source = background, Stretch = Stretch.Fill };

        _dimPath = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), // ~40% black
            IsHitTestVisible = false,
        };

        // Snip rectangle per design_handoff_project_window_1b: 1px solid mint
        // stroke, mint-at-4%-alpha fill (Theme.AccentFill04).
        _selectionFill = new System.Windows.Shapes.Rectangle
        {
            Fill = Theme.AccentFill04,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _selectionBorder = new System.Windows.Shapes.Rectangle
        {
            Stroke = Theme.Accent,
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _canvas = new Canvas { Background = Brushes.Transparent };
        _canvas.Children.Add(_dimPath);
        _canvas.Children.Add(_selectionFill);
        _canvas.Children.Add(_selectionBorder);

        _root = new Grid();
        _root.Children.Add(_background);
        _root.Children.Add(_canvas);
        Content = _root;

        Loaded += OnLoaded;
        SizeChanged += (_, _) => UpdateDim();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += OnKeyDown;
    }

    /// <summary>Positions the window over its monitor using PHYSICAL pixel bounds.</summary>
    public void ShowOnMonitor()
    {
        // Create the hwnd first, then place it with physical bounds (WPF Left/Top
        // pre-Show uses primary-monitor DPI and misplaces on mixed-DPI setups).
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        PlaceHwnd(helper.Handle);
        Show();
        PlaceHwnd(helper.Handle); // re-assert after Show
    }

    private void PlaceHwnd(IntPtr hwnd)
    {
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            (int)_monitor.Bounds.X, (int)_monitor.Bounds.Y,
            (int)_monitor.Bounds.Width, (int)_monitor.Bounds.Height,
            NativeMethods.SWP_SHOWWINDOW);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => UpdateDim();

    // ---- Coordinate conversions (this monitor's DPI) ----
    private double ToDip(double physical) => physical / _monitor.ScaleFactor;
    private double ToPhysical(double dip) => dip * _monitor.ScaleFactor;
    private PixelPoint DipToVirtual(Point dip) =>
        new(_monitor.Bounds.X + ToPhysical(dip.X), _monitor.Bounds.Y + ToPhysical(dip.Y));

    private Rect VirtualToDipRect(PixelRect r) => new(
        ToDip(r.X - _monitor.Bounds.X),
        ToDip(r.Y - _monitor.Bounds.Y),
        ToDip(r.Width),
        ToDip(r.Height));

    // ---- Selection drag ----
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_hasSelection)
        {
            // Once a selection is completed, a click outside it dismisses the
            // whole overlay (and, per SnipController, an unpinned translation
            // popup with it) — the popup has its own explicit "✕" for "I'm
            // done reading this."
            var p = DipToVirtual(e.GetPosition(_canvas));
            if (!_selectionPhysical.Contains(p))
                DismissRequested?.Invoke();
            return;
        }

        _dragging = true;
        _dragStartDip = e.GetPosition(_canvas);
        _hasSelection = false;
        _selectionBorder.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cur = e.GetPosition(_canvas);
        double x = Math.Min(cur.X, _dragStartDip.X);
        double y = Math.Min(cur.Y, _dragStartDip.Y);
        double w = Math.Abs(cur.X - _dragStartDip.X);
        double h = Math.Abs(cur.Y - _dragStartDip.Y);
        var dipRect = new Rect(x, y, w, h);
        DrawSelectionDip(dipRect);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var end = e.GetPosition(_canvas);
        double x = Math.Min(end.X, _dragStartDip.X);
        double y = Math.Min(end.Y, _dragStartDip.Y);
        double w = Math.Abs(end.X - _dragStartDip.X);
        double h = Math.Abs(end.Y - _dragStartDip.Y);

        if (w < 4 || h < 4) // treat a tiny drag/click as nothing
        {
            _selectionBorder.Visibility = Visibility.Collapsed;
            _selectionFill.Visibility = Visibility.Collapsed;
            UpdateDim();
            return;
        }

        var start = DipToVirtual(new Point(x, y));
        _selectionPhysical = new PixelRect(start.X, start.Y, ToPhysical(w), ToPhysical(h));
        _hasSelection = true;
        DrawSelectionDip(new Rect(x, y, w, h));
        SelectionCompleted?.Invoke(this, _selectionPhysical);
    }

    /// <summary>Programmatic selection for --auto-test (physical virtual-screen rect).</summary>
    public void SelectProgrammatically(PixelRect selectionPhysical)
    {
        _selectionPhysical = selectionPhysical;
        _hasSelection = true;
        DrawSelectionDip(VirtualToDipRect(selectionPhysical));
        SelectionCompleted?.Invoke(this, _selectionPhysical);
    }

    private void DrawSelectionDip(Rect dipRect)
    {
        Canvas.SetLeft(_selectionFill, dipRect.X);
        Canvas.SetTop(_selectionFill, dipRect.Y);
        _selectionFill.Width = dipRect.Width;
        _selectionFill.Height = dipRect.Height;
        _selectionFill.Visibility = Visibility.Visible;

        Canvas.SetLeft(_selectionBorder, dipRect.X);
        Canvas.SetTop(_selectionBorder, dipRect.Y);
        _selectionBorder.Width = dipRect.Width;
        _selectionBorder.Height = dipRect.Height;
        _selectionBorder.Visibility = Visibility.Visible;
        UpdateDim(dipRect);
    }

    private void UpdateDim() => UpdateDim(_hasSelection ? VirtualToDipRect(_selectionPhysical) : (Rect?)null);

    /// <summary>
    /// Dims the whole window except the selection interior, using an even-odd
    /// geometry (outer rect + selection hole).
    /// </summary>
    private void UpdateDim(Rect? selectionDip)
    {
        double w = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : ActualWidth;
        double h = _canvas.ActualHeight > 0 ? _canvas.ActualHeight : ActualHeight;
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, w, h)));
        if (selectionDip is { } sel && sel.Width > 0 && sel.Height > 0)
            group.Children.Add(new RectangleGeometry(sel));
        _dimPath.Data = group;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DismissRequested?.Invoke();
    }

    // ---- Result rendering (called on UI thread) ----

    /// <summary>Shows a small indicator near the selection ("Translating…" by default).</summary>
    public void ShowTranslatingIndicator(string message = "Translating…")
    {
        _indicatorText = new TextBlock
        {
            Text = message,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
        };
        _indicator = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1E, 0x1E, 0x1E)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Child = _indicatorText,
        };
        var dip = VirtualToDipRect(_selectionPhysical);
        Canvas.SetLeft(_indicator, dip.X);
        Canvas.SetTop(_indicator, Math.Max(0, dip.Y - 34));
        _canvas.Children.Add(_indicator);
    }

    /// <summary>Updates the indicator's text in place (e.g. "Starting translation engine…" → "Translating…").</summary>
    public void SetTranslatingIndicatorText(string message)
    {
        if (_indicatorText is not null)
            _indicatorText.Text = message;
    }

    public void HideTranslatingIndicator()
    {
        if (_indicator is not null)
        {
            _canvas.Children.Remove(_indicator);
            _indicator = null;
            _indicatorText = null;
        }
    }

    /// <summary>
    /// Renders the current overlay (frozen capture + dim + selection) to an
    /// in-memory bitmap at full physical resolution. Shared by
    /// <see cref="SaveToPng"/> and <c>SnipController</c>'s --save-shot popup
    /// compositing (the translation result now lives in a separate
    /// <c>TranslationPopupWindow</c>, not in this canvas). Must be called on
    /// the UI thread after layout.
    /// </summary>
    public RenderTargetBitmap? RenderToBitmap()
    {
        _root.UpdateLayout();
        double dpi = _monitor.Dpi;
        int pxW = (int)Math.Round(_root.ActualWidth * dpi / 96.0);
        int pxH = (int)Math.Round(_root.ActualHeight * dpi / 96.0);
        if (pxW <= 0 || pxH <= 0) return null;

        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(_root);
        return rtb;
    }

    /// <summary>
    /// Renders and saves <see cref="RenderToBitmap"/> to a PNG. Used by the
    /// --save-shot test/demo path so results can be captured without a real
    /// screenshot (the overlay is topmost and would otherwise be masked).
    /// </summary>
    public void SaveToPng(string path)
    {
        RenderTargetBitmap? rtb = RenderToBitmap();
        if (rtb is null) return;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        using var fs = System.IO.File.Create(path);
        encoder.Save(fs);
    }
}
