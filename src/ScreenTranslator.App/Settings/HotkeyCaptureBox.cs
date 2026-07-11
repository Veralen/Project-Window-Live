using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenTranslator.App.Hotkeys;

namespace ScreenTranslator.App.Settings;

/// <summary>
/// A focusable box that captures a keyboard chord. When focused it listens on
/// PreviewKeyDown (so Tab/Space/arrows are captured for the shortcut rather than
/// used for focus navigation), ignores bare modifier presses, and builds the
/// canonical hotkey string via <see cref="HotkeyManager.Format"/> — the single
/// source of truth shared with the parser. Esc cancels, Backspace/Delete clears.
/// </summary>
internal sealed class HotkeyCaptureBox : Border
{
    private readonly TextBlock _text;
    private static readonly Brush FocusBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xF6));
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0xAD, 0xB5, 0xBD));

    /// <summary>The canonical hotkey string ("Ctrl+Shift+L"), or null if unset.</summary>
    public string? HotkeyString { get; private set; }

    /// <summary>True when <see cref="HotkeyString"/> is a registrable chord.</summary>
    public bool IsValid { get; private set; }

    /// <summary>Raised whenever the captured chord changes.</summary>
    public event EventHandler? HotkeyChanged;

    public HotkeyCaptureBox()
    {
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
        Background = Brushes.White;
        BorderBrush = IdleBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(10, 8, 10, 8);
        MinHeight = 38;
        Cursor = Cursors.Hand;

        _text = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x21, 0x25, 0x29)),
        };
        Child = _text;

        // Clicking anywhere in the box starts capture.
        MouseLeftButtonDown += (_, _) => Focus();
        UpdateDisplay();
    }

    /// <summary>Sets the initial chord (e.g. the current config value) without firing change events.</summary>
    public void Initialize(string? hotkey)
    {
        HotkeyString = string.IsNullOrWhiteSpace(hotkey) ? null : hotkey;
        IsValid = HotkeyString is not null && HotkeyManager.TryParse(HotkeyString, out _, out _);
        UpdateDisplay();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        BorderBrush = FocusBrush;
        BorderThickness = new Thickness(2);
        UpdateDisplay();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        BorderBrush = IdleBrush;
        BorderThickness = new Thickness(1);
        UpdateDisplay();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!IsKeyboardFocused) return;

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = Keyboard.Modifiers;

        // Esc cancels capture and hands focus back (leaves the current value intact).
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            e.Handled = true;
            MoveFocusAway();
            return;
        }

        // Backspace always clears; Delete clears only when pressed alone (with a
        // modifier it is a legitimate chord key, e.g. Ctrl+Delete).
        if (key == Key.Back || (key == Key.Delete && mods == ModifierKeys.None))
        {
            e.Handled = true;
            Set(null, valid: false);
            return;
        }

        // Ignore bare modifier presses — wait for a real key.
        if (IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true; // capture the chord instead of letting Tab/Space/arrows navigate

        string? formatted = HotkeyManager.Format(mods, key);
        if (formatted is null)
        {
            // Unsupported key (e.g. a numpad key). Mark invalid but keep the box open.
            Set(null, valid: false, display: "Unsupported key — try another");
            return;
        }

        bool valid = HotkeyManager.TryParse(formatted, out _, out _);
        Set(formatted, valid);
    }

    private void Set(string? hotkey, bool valid, string? display = null)
    {
        HotkeyString = hotkey;
        IsValid = valid;
        UpdateDisplay(display);
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveFocusAway()
    {
        // Push focus to the next tab stop so the box visibly "commits".
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void UpdateDisplay(string? overrideText = null)
    {
        if (overrideText is not null)
        {
            _text.Text = overrideText;
            _text.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            return;
        }

        if (HotkeyString is not null)
        {
            _text.Text = ToDisplay(HotkeyString);
            _text.Foreground = new SolidColorBrush(Color.FromRgb(0x21, 0x25, 0x29));
        }
        else if (IsKeyboardFocused)
        {
            _text.Text = "Press the keys you want…";
            _text.Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0x8E, 0x96));
        }
        else
        {
            _text.Text = "Click here, then press your shortcut";
            _text.Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0x8E, 0x96));
        }
    }

    /// <summary>Human-readable form ("Ctrl + Shift + L") of a canonical hotkey string.</summary>
    public static string ToDisplay(string canonical) =>
        string.Join(" + ", canonical.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin or
        Key.System or Key.Clear;
}
