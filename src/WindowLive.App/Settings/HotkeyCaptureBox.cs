using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowLive.App.Hotkeys;
using WindowLive.App.Ui;

namespace WindowLive.App.Settings;

/// <summary>
/// A focusable chip that captures a keyboard chord, styled per
/// <c>design_handoff_project_window_1b</c> section 3 (HOTKEYS row): mono
/// 10.5px text, 1px <see cref="Theme.Border"/>, padding 2,7, radius 0, dark
/// bg. Click (or Tab) focus enters "recording" mode (border -&gt; mint, text
/// -&gt; "press keys…"); the next non-modifier key press that forms a valid
/// chord COMMITS immediately — raises <see cref="Committed"/> with the
/// canonical string and returns focus to the next tab stop. Owners apply the
/// rebind live (see <see cref="Settings.SettingsWindow"/>'s per-row apply)
/// and call <see cref="RevertTo"/> on failure to restore the previous
/// binding without re-raising <see cref="Committed"/>. Escape, an unsupported
/// key, or simply losing focus without a valid chord cancels back to the
/// last committed value — nothing is written until a valid chord commits.
/// Uses PreviewKeyDown (not KeyDown) so Tab/Space/arrows are captured as
/// chord input rather than used for focus navigation while recording.
/// </summary>
internal sealed class HotkeyCaptureBox : Border
{
    private readonly TextBlock _text;
    private string _current = string.Empty;
    private bool _recording;

    /// <summary>The last committed canonical hotkey string ("Ctrl+Shift+T").</summary>
    public string HotkeyString => _current;

    /// <summary>
    /// Raised when the user completes a new valid chord while recording.
    /// Not raised by <see cref="Initialize"/> or <see cref="RevertTo"/>.
    /// </summary>
    public event Action<string>? Committed;

    public HotkeyCaptureBox()
    {
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
        Background = Brushes.Transparent;
        BorderBrush = Theme.Border;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(0);
        Padding = new Thickness(7, 2, 7, 2);
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;

        _text = new TextBlock
        {
            FontFamily = Theme.MonoFontFamily,
            FontSize = 10.5,
            Foreground = Theme.TextPrimary,
        };
        Child = _text;

        MouseLeftButtonDown += (_, _) => Focus();
        UpdateDisplay();
    }

    /// <summary>Sets the initial chord (e.g. the current config value) without entering recording mode or firing <see cref="Committed"/>.</summary>
    public void Initialize(string hotkey)
    {
        _current = hotkey ?? string.Empty;
        _recording = false;
        UpdateDisplay();
    }

    /// <summary>Restores the chip to <paramref name="hotkey"/> after a failed live re-registration attempt, without raising <see cref="Committed"/>.</summary>
    public void RevertTo(string hotkey)
    {
        _current = hotkey ?? string.Empty;
        _recording = false;
        UpdateDisplay();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _recording = true;
        BorderBrush = Theme.Accent;
        UpdateDisplay();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _recording = false;
        BorderBrush = Theme.Border;
        UpdateDisplay(); // no commit happened during this focus session -> falls back to _current
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!IsKeyboardFocused) return;

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = Keyboard.Modifiers;

        // Esc cancels capture and hands focus to the next tab stop (leaves the current value intact).
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            e.Handled = true;
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
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
        if (formatted is null || !HotkeyManager.TryParse(formatted, out _, out _))
        {
            // Unsupported key (e.g. a numpad key) or an unregistrable chord. Stay in
            // recording mode so the user can try again; losing focus without a valid
            // chord reverts the display to the last committed value.
            ShowTransient("Unsupported key — try another");
            return;
        }

        _current = formatted;
        _recording = false;
        UpdateDisplay();
        Committed?.Invoke(formatted);
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void ShowTransient(string message)
    {
        _text.Text = message;
        _text.Foreground = Theme.Error;
    }

    private void UpdateDisplay()
    {
        _text.Text = _recording ? "press keys…" : _current;
        _text.Foreground = Theme.TextPrimary;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin or
        Key.System or Key.Clear;
}
