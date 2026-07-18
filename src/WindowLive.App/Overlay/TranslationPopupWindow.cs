using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowLive.App.Capture;
using WindowLive.App.Native;
using WindowLive.App.Ui;
using WindowLive.Core.Geometry;
using WindowLive.Core.Overlay;

namespace WindowLive.App.Overlay;

/// <summary>
/// The design-pack translation result popup (<c>Design Pack/design_handoff_project_window_1b/README.md</c>
/// section 2 + <c>1b-reference.html</c>), replacing the old in-canvas
/// <c>OverlayWindow</c> chip. A borderless, always-on-top, never-activating
/// top-level window (mirrors <see cref="GameMode.GamePanelWindow"/>'s
/// WS_EX_NOACTIVATE pattern) so it never steals keyboard focus from the
/// foreground app. 330 DIP wide, height auto (grows downward as translation
/// text streams in, same simplification the old chip used — see
/// <see cref="ResizeToFitContent"/>).
///
/// All placement math is physical pixels, virtual-screen coordinates (Core's
/// convention, see <c>ChipPlacement</c>); DIP conversion happens only here at
/// the WPF rendering boundary, mirroring <c>OverlayWindow</c>/<c>GamePanelWindow</c>.
/// Positioning uses native <c>SetWindowPos</c> (not WPF <c>Window.Left/Top</c>)
/// for the same reason <c>OverlayWindow.ShowOnMonitor</c> does: pre-Show
/// Left/Top uses primary-monitor DPI and misplaces on mixed-DPI setups.
///
/// One instance per snip; a pinned popup detaches from the controller and
/// outlives its overlay (see <c>SnipController</c>), closed only by its own
/// "✕" from then on.
/// </summary>
internal sealed class TranslationPopupWindow : Window
{
    private const double PopupWidthDip = 330.0;

    /// <summary>
    /// Extra transparent margin around the visible bordered box so the
    /// <see cref="Theme.CreatePopupShadow"/> blur/offset isn't clipped at the
    /// window's own edge (AllowsTransparency windows clip all rendering,
    /// including effects, to the window bounds).
    /// </summary>
    private const double ShadowMarginDip = 20.0;

    private readonly Grid _outer;
    private readonly Border _rootBorder;
    private readonly TranslateTransform _fadeTransform;

    private readonly TextBlock _translationText;
    private readonly TextBlock _originalText;
    private readonly StackPanel _dotsPanel;
    private readonly List<Ellipse> _dots = new();
    private readonly StackPanel _errorPanel;
    private readonly TextBlock _errorText;
    private readonly TextButton _retryButton;
    private readonly TextBlock _badgeText;
    private readonly TextBlock _modelText;
    private readonly TextButton _copyButton;
    private readonly TextButton _pinButton;
    private readonly TextButton _closeButton;
    private readonly DispatcherTimer _copyResetTimer;

    private MonitorInfo? _monitor;

    // Physical virtual-screen bounds of the WHOLE native window (including
    // the shadow margin) — set by PlaceNear, updated by ResizeToFitContent.
    private int _x;
    private int _y;
    private int _widthPhysical;
    private int _heightPhysical;

    /// <summary>Raised when the user clicks "✕".</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the user clicks "Retry" after <see cref="ShowError"/>.</summary>
    public event Action? RetryRequested;

    /// <summary>
    /// True once the user has clicked "Pin". A pinned popup is draggable
    /// (interior mouse-down + <see cref="Window.DragMove"/>, buttons excluded)
    /// and, per the controller, survives the overlay/selection being dismissed.
    /// </summary>
    public bool IsPinned { get; private set; }

    /// <summary>Current contents of the translation slot (streamed-so-far text).</summary>
    public string TranslationText => _translationText.Text;

    /// <summary>
    /// Physical virtual-screen bounds of the whole native window (including
    /// the shadow margin) at its current placement/size. Used by
    /// <c>SnipController</c>'s --save-shot compositing to know where to draw
    /// <see cref="RenderToBitmap"/>'s output onto the overlay's saved PNG.
    /// Default (all-zero) before <see cref="PlaceNear"/> has run.
    /// </summary>
    public PixelRect WindowBoundsPhysical => new(_x, _y, _widthPhysical, _heightPhysical);

    public TranslationPopupWindow()
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
        UseLayoutRounding = true;
        Title = "WindowLive Translation";
        SizeToContent = SizeToContent.Manual;

        // ---- Translation slot (dots while loading / streamed text / error) ----
        _dotsPanel = BuildDots();

        _translationText = new TextBlock
        {
            FontFamily = Theme.UiFontFamily,
            FontSize = 15,
            Foreground = Theme.TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 15 * 1.55,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Visibility = Visibility.Collapsed,
        };

        _errorText = new TextBlock
        {
            FontFamily = Theme.UiFontFamily,
            FontSize = 11.5,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
        };
        _retryButton = new TextButton("Retry") { Margin = new Thickness(0, 8, 0, 0) };
        _retryButton.Click += () => RetryRequested?.Invoke();
        _errorPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _errorPanel.Children.Add(_errorText);
        _errorPanel.Children.Add(_retryButton);

        var slot = new Grid { Margin = new Thickness(16, 14, 16, 12) };
        slot.Children.Add(_dotsPanel);
        slot.Children.Add(_translationText);
        slot.Children.Add(_errorPanel);

        // ---- Original text (demoted) ----
        _originalText = new TextBlock
        {
            FontFamily = Theme.UiFontFamily,
            FontSize = 11,
            Foreground = Theme.TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 0, 16, 12),
        };

        // ---- Footer ----
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 10 });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _badgeText = new TextBlock
        {
            FontFamily = Theme.MonoFontFamily,
            FontSize = 10,
            Foreground = Theme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        // TextButton (Ui/TextButton.cs) only exposes a secondary-gray / accent-mint
        // variant, not the README's dedicated muted "#5c5c5c" shade for model name —
        // reusing TextMuted directly here (not a button) matches the README exactly.
        _modelText = new TextBlock
        {
            FontFamily = Theme.MonoFontFamily,
            FontSize = 10,
            Foreground = Theme.TextMuted,
            MaxWidth = 110,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _copyButton = new TextButton("Copy") { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _pinButton = new TextButton("Pin") { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _closeButton = new TextButton("✕") { VerticalAlignment = VerticalAlignment.Center };

        Grid.SetColumn(_badgeText, 0);
        Grid.SetColumn(_modelText, 1);
        Grid.SetColumn(_copyButton, 3);
        Grid.SetColumn(_pinButton, 4);
        Grid.SetColumn(_closeButton, 5);
        footerGrid.Children.Add(_badgeText);
        footerGrid.Children.Add(_modelText);
        footerGrid.Children.Add(_copyButton);
        footerGrid.Children.Add(_pinButton);
        footerGrid.Children.Add(_closeButton);

        var footerBorder = new Border
        {
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 9, 16, 9),
            Child = footerGrid,
        };

        var mainStack = new StackPanel();
        mainStack.Children.Add(slot);
        mainStack.Children.Add(_originalText);
        mainStack.Children.Add(footerBorder);

        _fadeTransform = new TranslateTransform(0, 0);
        _rootBorder = new Border
        {
            Width = PopupWidthDip,
            Background = Theme.PopupBg,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Effect = Theme.CreatePopupShadow(),
            RenderTransform = _fadeTransform,
            Child = mainStack,
        };
        _rootBorder.MouseLeftButtonDown += OnRootMouseLeftButtonDown;

        _outer = new Grid { Margin = new Thickness(ShadowMarginDip) };
        _outer.Children.Add(_rootBorder);
        Content = _outer;

        _copyButton.Click += OnCopyClick;
        _pinButton.Click += OnPinClick;
        _closeButton.Click += () => CloseRequested?.Invoke();

        _copyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _copyResetTimer.Tick += (_, _) =>
        {
            _copyResetTimer.Stop();
            _copyButton.Text = "Copy";
        };

        SourceInitialized += (_, _) => ApplyNonActivatingStyle();
    }

    /// <summary>
    /// OR's WS_EX_NOACTIVATE (never receives keyboard focus/activation) and
    /// WS_EX_TOOLWINDOW (no taskbar/Alt-Tab entry) — same pattern as
    /// <see cref="GameMode.GamePanelWindow"/>. Combined with
    /// <c>ShowActivated = false</c> and never calling <c>Focus()</c>, the
    /// popup can never steal focus from the foreground app, even while being
    /// dragged (pinned + DragMove).
    /// </summary>
    private void ApplyNonActivatingStyle()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    private StackPanel BuildDots()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Height = 15, // matches the 15px translation-text line it stands in for
        };
        for (int i = 0; i < 3; i++)
        {
            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = Theme.Accent,
                Opacity = 0.3,
                Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0),
            };
            _dots.Add(dot);
            panel.Children.Add(dot);
        }
        return panel;
    }

    private void StartDotAnimation()
    {
        for (int i = 0; i < _dots.Count; i++)
        {
            var anim = new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(500))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 160),
            };
            _dots[i].BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    private void StopDotAnimation()
    {
        foreach (var dot in _dots)
            dot.BeginAnimation(UIElement.OpacityProperty, null);
    }

    // ---- Public streaming/display API (consumed by SnipController) ----

    /// <summary>Shows the popup's content with the original text set, badge/model set, and an empty translation slot pulsing its loading dots. Also used to reset to the loading state for a Retry.</summary>
    public void ShowLoading(string originalText, string badge, string modelName)
    {
        _originalText.Text = originalText;
        _badgeText.Text = badge;
        _modelText.Text = modelName;

        _translationText.Text = string.Empty;
        _translationText.Visibility = Visibility.Collapsed;
        _errorPanel.Visibility = Visibility.Collapsed;
        _dotsPanel.Visibility = Visibility.Visible;
        StartDotAnimation();

        ResizeToFitContent();
    }

    /// <summary>Appends one streamed fragment. The first call hides the dots and reveals the translation slot.</summary>
    public void AppendTranslation(string fragment)
    {
        if (_dotsPanel.Visibility == Visibility.Visible)
        {
            StopDotAnimation();
            _dotsPanel.Visibility = Visibility.Collapsed;
        }
        _errorPanel.Visibility = Visibility.Collapsed;
        _translationText.Visibility = Visibility.Visible;
        _translationText.Text += fragment;
        ResizeToFitContent();
    }

    /// <summary>Marks the stream finished. No-op beyond stopping the dots if a caller skipped straight here while they were still running.</summary>
    public void CompleteTranslation()
    {
        StopDotAnimation();
        if (_dotsPanel.Visibility == Visibility.Visible)
            _dotsPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Replaces the translation slot with an error message + Retry button.</summary>
    public void ShowError(string message)
    {
        StopDotAnimation();
        _dotsPanel.Visibility = Visibility.Collapsed;
        _translationText.Visibility = Visibility.Collapsed;
        _errorText.Text = message;
        _errorPanel.Visibility = Visibility.Visible;
        ResizeToFitContent();
    }

    // ---- Placement (physical px, virtual-screen coords — Core's convention) ----

    /// <summary>
    /// Places the popup near <paramref name="selection"/> on <paramref name="monitor"/>
    /// using Core's <see cref="ChipPlacement"/> (below by default, flips above if
    /// that would exit the monitor, clamped horizontally) and shows it with the
    /// README's 120ms fade-in + 4px→0 translate-y. Call once per popup, after the
    /// initial <see cref="ShowLoading"/> so the placement measurement reflects real
    /// content height.
    /// </summary>
    public void PlaceNear(PixelRect selection, MonitorInfo monitor)
    {
        _monitor = monitor;

        _rootBorder.Measure(new Size(PopupWidthDip, double.PositiveInfinity));
        double contentHeightDip = Math.Max(1, _rootBorder.DesiredSize.Height);

        int visibleWidthPhysical = (int)Math.Round(ToPhysical(PopupWidthDip, monitor));
        int visibleHeightPhysical = (int)Math.Round(ToPhysical(contentHeightDip, monitor));
        int gapPhysical = (int)Math.Round(ToPhysical(8, monitor));
        int marginPhysical = (int)Math.Round(ToPhysical(ShadowMarginDip, monitor));

        // ChipPlacement operates on the VISIBLE bordered box (matches the selection
        // gap visually); the shadow margin is then applied as a pure outward
        // inflate around that placement for the actual native window rect.
        PixelRect placement = ChipPlacement.Place(selection, visibleWidthPhysical, visibleHeightPhysical, monitor.Bounds, gapPhysical);

        _x = (int)Math.Round(placement.X) - marginPhysical;
        _y = (int)Math.Round(placement.Y) - marginPhysical;
        _widthPhysical = visibleWidthPhysical + 2 * marginPhysical;
        _heightPhysical = visibleHeightPhysical + 2 * marginPhysical;

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        ApplyWindowPos();
        if (!IsVisible)
            Show();
        ApplyWindowPos(); // re-assert after Show, mirrors OverlayWindow.ShowOnMonitor

        BeginFadeIn();
    }

    /// <summary>
    /// Re-measures the content at the fixed 330 DIP width and resizes the native
    /// window's height to match (top-left stays put — the popup grows downward,
    /// same simplification the old chip used). No-op before the first
    /// <see cref="PlaceNear"/>.
    /// </summary>
    private void ResizeToFitContent()
    {
        if (_monitor is null) return;

        _rootBorder.Measure(new Size(PopupWidthDip, double.PositiveInfinity));
        double contentHeightDip = Math.Max(1, _rootBorder.DesiredSize.Height);
        int visibleHeightPhysical = (int)Math.Round(ToPhysical(contentHeightDip, _monitor));
        int marginPhysical = (int)Math.Round(ToPhysical(ShadowMarginDip, _monitor));

        _heightPhysical = visibleHeightPhysical + 2 * marginPhysical;
        ApplyWindowPos();
    }

    private void ApplyWindowPos()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, _x, _y, _widthPhysical, Math.Max(1, _heightPhysical),
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }

    private void BeginFadeIn()
    {
        _fadeTransform.Y = 4;
        Opacity = 0;

        var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var translateAnim = new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, opacityAnim);
        _fadeTransform.BeginAnimation(TranslateTransform.YProperty, translateAnim);
    }

    private static double ToPhysical(double dip, MonitorInfo monitor) => dip * monitor.ScaleFactor;

    /// <summary>
    /// Renders the popup's current on-screen appearance (chrome + shadow) to an
    /// in-memory bitmap at the monitor's DPI. Used by <c>SnipController</c>'s
    /// --save-shot harness to composite the popup into the saved overlay PNG
    /// without depending on real desktop screen capture. Null before the first
    /// <see cref="PlaceNear"/> or if the window has zero size.
    /// </summary>
    public RenderTargetBitmap? RenderToBitmap()
    {
        if (_monitor is null) return null;
        _outer.UpdateLayout();
        double dpi = _monitor.Dpi;
        int pxW = (int)Math.Round(_outer.ActualWidth * dpi / 96.0);
        int pxH = (int)Math.Round(_outer.ActualHeight * dpi / 96.0);
        if (pxW <= 0 || pxH <= 0) return null;

        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(_outer);
        return rtb;
    }

    // ---- Footer button handlers ----

    private void OnCopyClick()
    {
        try { if (_translationText.Text.Length > 0) Clipboard.SetText(_translationText.Text); }
        catch { /* best-effort — clipboard access can fail transiently */ }

        _copyButton.Text = "Copied";
        _copyResetTimer.Stop();
        _copyResetTimer.Start();
    }

    private void OnPinClick()
    {
        IsPinned = !IsPinned;
        _pinButton.Text = IsPinned ? "Unpin" : "Pin";
    }

    /// <summary>
    /// Pin makes the whole popup body draggable (README "Pin keeps it always-on-top
    /// and draggable"). Excludes the footer's TextButtons (Copy/Pin/✕ must still be
    /// clickable) by walking up from the click's original source looking for one.
    /// </summary>
    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsPinned) return;
        if (OriginatesFromButton(e.OriginalSource)) return;
        try { DragMove(); } catch (InvalidOperationException) { /* not in a state DragMove accepts — ignore */ }
    }

    private bool OriginatesFromButton(object originalSource)
    {
        DependencyObject? d = originalSource as DependencyObject;
        while (d is not null && !ReferenceEquals(d, _rootBorder))
        {
            if (d is TextButton) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }
}
