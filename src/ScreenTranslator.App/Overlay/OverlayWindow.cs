using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ScreenTranslator.App.Capture;
using ScreenTranslator.App.Native;
using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Placement;

namespace ScreenTranslator.App.Overlay;

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
    private Button? _closeButton;
    private readonly List<UIElement> _resultElements = new();

    private bool _dragging;
    private Point _dragStartDip;
    private PixelRect _selectionPhysical; // virtual-screen physical px
    private bool _hasSelection;
    private bool _resultsShown;

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
        Title = "ScreenTranslator Overlay";

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
        if (_resultsShown)
        {
            // A click outside the selection dismisses the whole overlay.
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

    /// <summary>Shows a small "Translating…" indicator near the selection.</summary>
    public void ShowTranslatingIndicator()
    {
        _indicator = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1E, 0x1E, 0x1E)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Child = new TextBlock
            {
                Text = "Translating…",
                Foreground = Brushes.White,
                FontFamily = new FontFamily(LabelStyle.FontFamily),
                FontSize = 13,
            },
        };
        var dip = VirtualToDipRect(_selectionPhysical);
        Canvas.SetLeft(_indicator, dip.X);
        Canvas.SetTop(_indicator, Math.Max(0, dip.Y - 34));
        _canvas.Children.Add(_indicator);
    }

    public void HideTranslatingIndicator()
    {
        if (_indicator is not null)
        {
            _canvas.Children.Remove(_indicator);
            _indicator = null;
        }
    }

    /// <summary>Renders translated label chips at their physical LabelBounds.</summary>
    public void RenderLabels(IReadOnlyList<PlacedLabel> labels)
    {
        HideTranslatingIndicator();
        ClearResults();
        foreach (var label in labels)
        {
            var chip = BuildChip(label.Block.TranslatedText, label.FontSize, label.LabelBounds);
            var dip = VirtualToDipRect(label.LabelBounds);
            Canvas.SetLeft(chip, dip.X);
            Canvas.SetTop(chip, dip.Y);
            chip.MaxWidth = dip.Width;
            _canvas.Children.Add(chip);
            _resultElements.Add(chip);
        }
        AddCloseButton();
        _resultsShown = true;
    }

    /// <summary>
    /// Renders the current overlay (frozen capture + dim + selection + chips) to a
    /// PNG at full physical resolution. Used by the --save-shot test/demo path so
    /// results can be captured without a screenshot (the overlay is topmost and
    /// would otherwise be masked). Must be called on the UI thread after layout.
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

    /// <summary>Shows a "No text detected" chip at the selection.</summary>
    public void ShowNoTextChip()
    {
        HideTranslatingIndicator();
        ClearResults();
        var chip = BuildChip("No text detected", 13 * _monitor.ScaleFactor, _selectionPhysical);
        var dip = VirtualToDipRect(_selectionPhysical);
        Canvas.SetLeft(chip, dip.X);
        Canvas.SetTop(chip, dip.Y);
        _canvas.Children.Add(chip);
        _resultElements.Add(chip);
        AddCloseButton();
        _resultsShown = true;
    }

    private Border BuildChip(string text, double fontSizePhysical, PixelRect boundsPhysical)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ToDip(LabelStyle.CornerRadius)),
            Padding = new Thickness(ToDip(LabelStyle.PaddingX), ToDip(LabelStyle.PaddingY),
                                    ToDip(LabelStyle.PaddingX), ToDip(LabelStyle.PaddingY)),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                FontFamily = new FontFamily(LabelStyle.FontFamily),
                FontSize = ToDip(fontSizePhysical),
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private void AddCloseButton()
    {
        _closeButton = new Button
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
        _closeButton.Click += (_, _) => DismissRequested?.Invoke();
        var dip = VirtualToDipRect(_selectionPhysical);
        Canvas.SetLeft(_closeButton, dip.X + dip.Width - 12);
        Canvas.SetTop(_closeButton, Math.Max(0, dip.Y - 12));
        _canvas.Children.Add(_closeButton);
        _resultElements.Add(_closeButton);
    }

    private void ClearResults()
    {
        foreach (var el in _resultElements)
            _canvas.Children.Remove(el);
        _resultElements.Clear();
        if (_closeButton is not null) { _closeButton = null; }
    }
}
