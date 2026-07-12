using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowLive.App.Hotkeys;
using WindowLive.Core.Config;

namespace WindowLive.App.Settings;

/// <summary>
/// A small, non-technical-friendly window for rebinding the desktop snip &amp;
/// translate shortcut and the game-mode (region setup/redefine) shortcut
/// without editing config.json. Save persists both hotkeys to
/// <see cref="AppConfig"/> only after the app successfully live-re-registers
/// whichever one(s) changed; on failure the window stays open, shows the
/// error, and leaves config untouched (a partially-applied live rebind is
/// rolled back — see <see cref="OnSave"/>).
///
/// TODO(game mode): the local-LLM debug/status panel (model loaded,
/// llama-server state, GPU in use) that this window used to host for the ONNX
/// engine was removed with that engine and has not been reintroduced.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppConfig _config;
    // Live re-registration hook into the app: (slot, hotkey) -> (ok, errorMessage).
    private readonly Func<string, string, (bool ok, string error)> _reregister;

    private readonly HotkeyCaptureBox _capture;
    private readonly HotkeyCaptureBox _gameCapture;
    private readonly TextBlock _message;
    private readonly Button _saveButton;

    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly Brush InkBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x25, 0x29));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x6B));

    private const string InvalidChordMessage =
        "Please include at least one of Ctrl, Alt, Shift, or Win, plus another key.";

    /// <summary>Raised after a successful save with the new (desktopHotkey, gameModeHotkey) canonical strings.</summary>
    public event Action<string, string>? Saved;

    public SettingsWindow(AppConfig config, Func<string, string, (bool ok, string error)> reregister)
    {
        _config = config;
        _reregister = reregister;

        Title = "WindowLive Settings";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;

        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };

        root.Children.Add(new TextBlock
        {
            Text = "Snip & translate shortcut",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = InkBrush,
            Margin = new Thickness(0, 0, 0, 4),
        });

        root.Children.Add(new TextBlock
        {
            Text = "Click the box and press the keys you want (for example Ctrl + Shift + T).",
            FontSize = 12.5,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        _capture = new HotkeyCaptureBox();
        _capture.Initialize(config.DesktopHotkey);
        _capture.HotkeyChanged += (_, _) => OnChordChanged();
        root.Children.Add(_capture);

        root.Children.Add(new TextBlock
        {
            Text = "Game mode chat-region shortcut",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = InkBrush,
            Margin = new Thickness(0, 18, 0, 4),
        });

        root.Children.Add(new TextBlock
        {
            Text = "Sets up (or redefines) the live game-chat capture region.",
            FontSize = 12.5,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        _gameCapture = new HotkeyCaptureBox();
        _gameCapture.Initialize(config.GameModeHotkey);
        _gameCapture.HotkeyChanged += (_, _) => OnChordChanged();
        root.Children.Add(_gameCapture);

        _message = new TextBlock
        {
            FontSize = 12.5,
            Foreground = ErrorBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_message);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var cancelButton = MakeButton("Cancel", primary: false);
        cancelButton.IsCancel = true;
        cancelButton.Click += (_, _) => Close();
        buttons.Children.Add(cancelButton);

        _saveButton = MakeButton("Save", primary: true);
        _saveButton.Margin = new Thickness(8, 0, 0, 0);
        _saveButton.IsDefault = true;
        _saveButton.Click += (_, _) => OnSave();
        buttons.Children.Add(_saveButton);

        root.Children.Add(buttons);

        Content = root;

        Loaded += (_, _) =>
        {
            _capture.Focus();
            OnChordChanged();
        };
    }

    private static Button MakeButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 88,
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 13.5,
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1),
        };
        if (primary)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xF6));
            button.Foreground = Brushes.White;
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xF6));
        }
        else
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF3, 0xF5));
            button.Foreground = InkBrush;
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCE, 0xD4, 0xDA));
        }
        return button;
    }

    private void OnChordChanged()
    {
        bool ok = _capture.IsValid && _gameCapture.IsValid;
        _saveButton.IsEnabled = ok;
        _saveButton.Opacity = ok ? 1.0 : 0.55;
        if (ok)
        {
            HideMessage();
        }
        else if (_capture.HotkeyString is null || _gameCapture.HotkeyString is null)
        {
            // Box just cleared (Backspace/Delete) — no error text until the user
            // actually types something invalid.
            HideMessage();
        }
        else
        {
            ShowMessage(InvalidChordMessage);
        }
    }

    private void OnSave()
    {
        if (!_capture.IsValid || string.IsNullOrWhiteSpace(_capture.HotkeyString) ||
            !_gameCapture.IsValid || string.IsNullOrWhiteSpace(_gameCapture.HotkeyString))
        {
            ShowMessage(InvalidChordMessage);
            return;
        }

        string newDesktopHotkey = _capture.HotkeyString!;
        string newGameHotkey = _gameCapture.HotkeyString!;

        // Re-register FIRST; only persist if the OS accepted the new binding(s), so we
        // never write a shortcut the app can't actually use. Each slot is only touched
        // if its value actually changed, so an untouched box never risks its existing
        // live registration.
        if (!TryApplyIfChanged(HotkeyManager.DesktopSlot, newDesktopHotkey, _config.DesktopHotkey, out string desktopError))
        {
            ShowMessage(string.IsNullOrWhiteSpace(desktopError)
                ? "That shortcut could not be registered."
                : desktopError);
            return; // keep window open, do not persist
        }

        if (!TryApplyIfChanged(HotkeyManager.GameModeSlot, newGameHotkey, _config.GameModeHotkey, out string gameError))
        {
            // Roll back the desktop slot (best-effort) so we don't leave it live-bound
            // to a value that was never persisted to config.
            if (newDesktopHotkey != _config.DesktopHotkey)
                _reregister(HotkeyManager.DesktopSlot, _config.DesktopHotkey);

            ShowMessage(string.IsNullOrWhiteSpace(gameError)
                ? "That game-mode shortcut could not be registered."
                : gameError);
            return; // keep window open, do not persist
        }

        _config.DesktopHotkey = newDesktopHotkey;
        _config.GameModeHotkey = newGameHotkey;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            ShowMessage($"Settings applied, but saving them failed: {ex.Message}");
            // The bindings are live; surface but don't close so the user notices.
            return;
        }

        Saved?.Invoke(newDesktopHotkey, newGameHotkey);
        Close();
    }

    /// <summary>Live-reregisters <paramref name="slot"/> only if its value changed from <paramref name="currentHotkey"/>.</summary>
    private bool TryApplyIfChanged(string slot, string newHotkey, string currentHotkey, out string error)
    {
        error = string.Empty;
        if (newHotkey == currentHotkey)
            return true; // unchanged — nothing to (re)register
        (bool ok, error) = _reregister(slot, newHotkey);
        return ok;
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        _message.Visibility = Visibility.Collapsed;
    }
}
