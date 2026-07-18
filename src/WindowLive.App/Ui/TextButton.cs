using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowLive.App.Ui;

/// <summary>
/// Minimal text-only button per <c>design_handoff_project_window_1b</c>
/// (popup footer "Copy" / "Pin" / "✕", settings "show" / "reset to default"
/// links). No background or border in any state; only the text color
/// changes. <see cref="TextBlock.Text"/> (inherited) sets the label.
/// </summary>
internal sealed class TextButton : TextBlock
{
    private bool _isAccent;
    private bool _isPressed;

    /// <summary>Raised on a completed click (mouse-down and mouse-up both inside the control).</summary>
    public event Action? Click;

    /// <summary>
    /// True for the mint "link" variant (e.g. "show", "reset to default");
    /// false (default) for the standard secondary-gray text button ("Copy",
    /// "Pin", "✕").
    /// </summary>
    public bool IsAccent
    {
        get => _isAccent;
        set
        {
            _isAccent = value;
            UpdateVisual(hovered: IsMouseOver);
        }
    }

    public TextButton()
    {
        FontFamily = Theme.UiFontFamily;
        FontSize = 11;
        Cursor = Cursors.Hand;
        Focusable = false;
        // Transparent (not null) background so the whole bounding box — not
        // just glyph ink — is hit-testable/clickable.
        Background = Brushes.Transparent;
        UpdateVisual(hovered: false);

        MouseEnter += (_, _) => UpdateVisual(hovered: true);
        MouseLeave += (_, _) =>
        {
            _isPressed = false;
            UpdateVisual(hovered: false);
        };
        MouseLeftButtonDown += (_, _) =>
        {
            _isPressed = true;
            UpdateVisual(hovered: true);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            bool wasPressed = _isPressed;
            _isPressed = false;
            UpdateVisual(hovered: IsMouseOver);
            if (wasPressed && IsMouseOver)
                Click?.Invoke();
        };
    }

    public TextButton(string text) : this() => Text = text;

    private void UpdateVisual(bool hovered)
    {
        Foreground = hovered
            ? (_isAccent ? Theme.AccentBright : Theme.TextPrimary)
            : (_isAccent ? Theme.Accent : Theme.TextSecondary);
        Opacity = _isPressed ? 0.7 : 1.0;
    }
}
