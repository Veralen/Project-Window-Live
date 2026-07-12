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
using WindowLive.Core.Geometry;
using WindowLive.Core.Overlay;

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
    private Border? _indicator;
    private TextBlock? _indicatorText;
    private Border? _chip;
    private TextBlock? _chipText;
    private Button? _chipCloseButton;

    private bool _dragging;
    private Point _dragStartDip;
    private PixelRect _selectionPhysical; // virtual-screen physical px
    private bool _hasSelection;
    private bool _chipShown;

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

        _selectionBorder = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0xC8, 0xFF)),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _canvas = new Canvas { Background = Brushes.Transparent };
        _canvas.Children.Add(_dimPath);
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
        if (_chipShown)
        {
            // Once the translation chip is up, a click outside the original
            // selection dismisses the whole overlay — the chip has its own
            // explicit close button for "I'm done reading this."
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
    /// Shows an initially empty translation chip near the selection
    /// (docs/window-live-design.md "Overlay chip placement"): below by default,
    /// flipped above if that would exit the monitor, positioned once via Core's
    /// <see cref="ChipPlacement"/> — width capped at min(selection width * 1.5,
    /// 600 DIP) with wrapping. Height is placed using a single-line estimate and
    /// simply grows downward as text streams in via
    /// <see cref="AppendTranslationChipText"/>; re-placing on every token would
    /// be distracting for a chip that only ever holds one short sentence.
    /// </summary>
    public void ShowTranslationChip(PixelRect selectionPhysical)
    {
        HideTranslationChip();

        double maxWidthDip = Math.Clamp(ToDip(selectionPhysical.Width) * 1.5, 80.0, 600.0);

        _chipText = new TextBlock
        {
            Text = string.Empty,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };

        var chipBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Width = maxWidthDip,
            Child = _chipText,
        };

        // Measure a single non-breaking space at the fixed width to get a stable
        // single-line height estimate for placement before any real text arrives.
        _chipText.Text = " ";
        chipBorder.Measure(new Size(maxWidthDip, double.PositiveInfinity));
        double estimatedHeightDip = chipBorder.DesiredSize.Height;
        _chipText.Text = string.Empty;

        int chipWidthPhysical = (int)Math.Round(ToPhysical(maxWidthDip));
        int chipHeightPhysical = (int)Math.Round(ToPhysical(estimatedHeightDip));
        int gapPhysical = (int)Math.Round(ToPhysical(8));

        PixelRect placement = ChipPlacement.Place(
            selectionPhysical, chipWidthPhysical, chipHeightPhysical, _monitor.Bounds, gapPhysical);
        Rect dip = VirtualToDipRect(placement);

        Canvas.SetLeft(chipBorder, dip.X);
        Canvas.SetTop(chipBorder, dip.Y);

        _chipCloseButton = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 24,
            FontSize = 13,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x40, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Arrow,
        };
        _chipCloseButton.Click += (_, _) => DismissRequested?.Invoke();
        Canvas.SetLeft(_chipCloseButton, dip.X + dip.Width - 12);
        Canvas.SetTop(_chipCloseButton, dip.Y - 12);

        _chip = chipBorder;
        _canvas.Children.Add(_chip);
        _canvas.Children.Add(_chipCloseButton);
        _chipShown = true;
    }

    /// <summary>Appends one streamed fragment to the chip's text. UI thread only.</summary>
    public void AppendTranslationChipText(string fragment)
    {
        if (_chipText is not null)
            _chipText.Text += fragment;
    }

    /// <summary>Removes the translation chip and its close button, if shown.</summary>
    public void HideTranslationChip()
    {
        if (_chip is not null)
        {
            _canvas.Children.Remove(_chip);
            _chip = null;
        }
        if (_chipCloseButton is not null)
        {
            _canvas.Children.Remove(_chipCloseButton);
            _chipCloseButton = null;
        }
        _chipText = null;
        _chipShown = false;
    }

    /// <summary>
    /// Renders the current overlay (frozen capture + dim + selection + any result
    /// chip) to a PNG at full physical resolution. Used by the --save-shot test/demo
    /// path so results can be captured without a screenshot (the overlay is topmost
    /// and would otherwise be masked). Must be called on the UI thread after layout.
    /// </summary>
    public void SaveToPng(string path)
    {
        _root.UpdateLayout();
        double dpi = _monitor.Dpi;
        int pxW = (int)Math.Round(_root.ActualWidth * dpi / 96.0);
        int pxH = (int)Math.Round(_root.ActualHeight * dpi / 96.0);
        if (pxW <= 0 || pxH <= 0) return;

        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(_root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        using var fs = System.IO.File.Create(path);
        encoder.Save(fs);
    }
}
