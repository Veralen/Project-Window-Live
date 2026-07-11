using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenTranslator.App.Pipeline;
using ScreenTranslator.Core.Config;

namespace ScreenTranslator.App.Settings;

/// <summary>
/// A small, non-technical-friendly window for rebinding the snip &amp; translate
/// shortcut without editing config.json. Save persists the new hotkey to
/// <see cref="AppConfig"/> only after the app successfully live-re-registers it;
/// on failure the window stays open, shows the error, and leaves config untouched.
///
/// <para>Also hosts the translation debug panel: switch the model (opus/NLLB) and
/// execution provider (CPU/DirectML) without editing config.json, plus a live
/// status readout of the engine actually loaded, whether it runs on CPU or GPU,
/// and whether the configured OCR language pack is installed (a missing pack
/// silently falls back to English OCR, which misreads CJK text — the classic
/// "garbage labels" failure).</para>
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppConfig _config;
    // Live re-registration hook into the app: returns (ok, errorMessage).
    private readonly Func<string, (bool ok, string error)> _reregister;
    // Snapshot of the live translation/OCR runtime state (app shell owns the engine).
    private readonly Func<TranslationStatus> _translationStatus;
    // Rebuilds the translation engine after Engine/ExecutionProvider changed in config.
    private readonly Action _applyTranslation;

    private readonly HotkeyCaptureBox _capture;
    private readonly TextBlock _message;
    private readonly Button _saveButton;
    private readonly ComboBox _engineBox;
    private readonly ComboBox _providerBox;
    private readonly TextBlock _modelWarning;
    private readonly TextBlock _statusText;
    private readonly TextBlock _ocrWarning;
    private readonly DispatcherTimer _statusTimer;

    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x6A, 0x00));
    private static readonly Brush InkBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x25, 0x29));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x6B));

    private const string InvalidChordMessage =
        "Please include at least one of Ctrl, Alt, Shift, or Win, plus another key.";

    /// <summary>Raised after a successful save with the new canonical hotkey string.</summary>
    public event Action<string>? Saved;

    public SettingsWindow(AppConfig config, Func<string, (bool ok, string error)> reregister,
        Func<TranslationStatus> translationStatus, Action applyTranslation)
    {
        _config = config;
        _reregister = reregister;
        _translationStatus = translationStatus;
        _applyTranslation = applyTranslation;

        Title = "ScreenTranslator Settings";
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
            Text = "Click the box and press the keys you want (for example Ctrl + Shift + L).",
            FontSize = 12.5,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        _capture = new HotkeyCaptureBox();
        _capture.Initialize(config.Hotkey);
        _capture.HotkeyChanged += (_, _) => OnChordChanged();
        root.Children.Add(_capture);

        _message = new TextBlock
        {
            FontSize = 12.5,
            Foreground = ErrorBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_message);

        // ---- Translation debug panel -------------------------------------------------

        root.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xEF)),
            Margin = new Thickness(0, 16, 0, 14),
        });

        root.Children.Add(new TextBlock
        {
            Text = "Translation (debug)",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = InkBrush,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _engineBox = MakeCombo(
            ("opus", "opus-mt zh→en (default, fast)"),
            ("nllb", "NLLB-200 600M (higher quality, heavier)"));
        root.Children.Add(MakeLabeledRow("Model", _engineBox));

        _providerBox = MakeCombo(
            ("cpu", "CPU (default)"),
            ("directml", "GPU (DirectML)"));
        root.Children.Add(MakeLabeledRow("Run on", _providerBox));

        SelectByTag(_engineBox, _config.ResolveEngine());
        SelectByTag(_providerBox, _config.ResolveExecutionProvider());
        _engineBox.SelectionChanged += (_, _) => UpdateModelWarning();
        _providerBox.SelectionChanged += (_, _) => UpdateModelWarning();

        _modelWarning = new TextBlock
        {
            FontSize = 12.5,
            Foreground = WarnBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_modelWarning);

        _statusText = new TextBlock
        {
            FontSize = 12.5,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        root.Children.Add(_statusText);

        _ocrWarning = new TextBlock
        {
            FontSize = 12.5,
            Foreground = ErrorBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_ocrWarning);

        // The engine loads in the background (and can silently fall back from GPU to
        // CPU), so the status is polled while the window is open rather than sampled once.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        Closed += (_, _) => _statusTimer.Stop();

        // ------------------------------------------------------------------------------

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
            UpdateModelWarning();
            RefreshStatus();
            _statusTimer.Start();
        };
    }

    // ---- Translation debug helpers ---------------------------------------------------

    private static ComboBox MakeCombo(params (string tag, string label)[] items)
    {
        var box = new ComboBox { FontSize = 13, MinWidth = 260 };
        foreach ((string tag, string label) in items)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        return box;
    }

    private static UIElement MakeLabeledRow(string label, ComboBox box)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = InkBrush,
            Width = 64,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        row.Children.Add(box);
        return row;
    }

    private static string SelectedTag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        box.SelectedIndex = 0;
    }

    /// <summary>
    /// Pre-save advisory for the current dropdown selection: missing model files
    /// (the engine would silently fall back to echo passthrough) and the DirectML
    /// caveats. Purely informational — Save is never blocked.
    /// </summary>
    private void UpdateModelWarning()
    {
        if (_engineBox is null || _providerBox is null || _modelWarning is null) return;

        string engine = SelectedTag(_engineBox);
        string provider = SelectedTag(_providerBox);
        string warning = string.Empty;

        if (engine == "nllb" && !PipelineFactory.NllbModelPresent(_config.ResolveNllbModelDirectory()))
            warning = $"NLLB model files not found in '{_config.ResolveNllbModelDirectory()}' — " +
                      "labels would show \"[no model]\" passthrough. Run scripts/download-model-nllb.ps1 first.";
        else if (engine == "opus" && !PipelineFactory.OpusModelPresent(_config.ResolveModelDirectory()))
            warning = $"opus-mt model files not found in '{_config.ResolveModelDirectory()}' — " +
                      "labels would show \"[no model]\" passthrough. Run scripts/download-model.ps1 first.";
        else if (provider == "directml")
            warning = "GPU (DirectML) needs fp32 model weights; int8-only models stay on CPU, and if GPU " +
                      "init fails the engine falls back to CPU automatically (see status below after saving).";

        _modelWarning.Text = warning;
        _modelWarning.Visibility = warning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Refreshes the live status readout (active model, CPU/GPU, OCR pack).</summary>
    private void RefreshStatus()
    {
        TranslationStatus s;
        try { s = _translationStatus(); }
        catch (Exception ex)
        {
            _statusText.Text = $"Status unavailable: {ex.Message}";
            return;
        }

        string state = s.IsReady
            ? "ready"
            : s.InitError is null ? "loading…" : $"FAILED to load — {s.InitError}";
        string modelLine = s.IsEcho
            ? $"Active model: {s.EngineLabel}"
            : $"Active model: {s.EngineLabel} — {state}";

        string providerLine;
        if (s.IsEcho)
            providerLine = "Running on: — (no model loaded)";
        else
        {
            providerLine = $"Running on: {ProviderDisplay(s.ActiveProvider)}";
            if (s.IsReady && s.RequestedProvider != s.ActiveProvider)
                providerLine += $" ({ProviderDisplay(s.RequestedProvider)} requested — fell back)";
        }

        string dirLine = s.ModelDirectory is null ? string.Empty : $"\nModel folder: {s.ModelDirectory}";
        _statusText.Text = modelLine + "\n" + providerLine + dirLine;

        if (s.OcrMatchedLanguage is not null)
        {
            _ocrWarning.Visibility = Visibility.Collapsed;
            _statusText.Text += $"\nOCR language: {s.OcrConfiguredLanguage} (pack installed: {s.OcrMatchedLanguage})";
        }
        else
        {
            _ocrWarning.Text =
                $"No OCR pack matches '{s.OcrConfiguredLanguage}' — OCR falls back to " +
                $"'{s.OcrFallbackLanguage ?? "none"}' and will misread the source text " +
                "(this produces garbage labels). Run scripts/install-ocr-language.ps1 as " +
                "administrator, then restart the app.";
            _ocrWarning.Visibility = Visibility.Visible;
        }
    }

    private static string ProviderDisplay(string provider) => provider switch
    {
        "directml" => "GPU (DirectML)",
        "cpu" => "CPU",
        _ => provider,
    };

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
        bool ok = _capture.IsValid;
        _saveButton.IsEnabled = ok;
        _saveButton.Opacity = ok ? 1.0 : 0.55;
        if (ok || _capture.HotkeyString is null)
            HideMessage();
        else
            ShowMessage(InvalidChordMessage);
    }

    private void OnSave()
    {
        if (!_capture.IsValid || string.IsNullOrWhiteSpace(_capture.HotkeyString))
        {
            ShowMessage(InvalidChordMessage);
            return;
        }

        string newHotkey = _capture.HotkeyString!;
        string newEngine = SelectedTag(_engineBox);
        string newProvider = SelectedTag(_providerBox);
        bool translationChanged =
            !string.Equals(newEngine, _config.ResolveEngine(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(newProvider, _config.ResolveExecutionProvider(), StringComparison.OrdinalIgnoreCase);

        // Re-register FIRST; only persist if the OS accepted the new binding, so we
        // never write a shortcut the app can't actually use.
        (bool ok, string error) = _reregister(newHotkey);
        if (!ok)
        {
            ShowMessage(string.IsNullOrWhiteSpace(error)
                ? "That shortcut could not be registered."
                : error);
            return; // keep window open, do not persist
        }

        _config.Hotkey = newHotkey;
        _config.Engine = newEngine;
        _config.ExecutionProvider = newProvider;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            ShowMessage($"Settings applied, but saving them failed: {ex.Message}");
            // The binding is live; surface but don't close so the user notices.
            return;
        }

        // Rebuild the engine only when the model/provider actually changed — a
        // hotkey-only save must not reload a multi-hundred-MB model.
        if (translationChanged)
            _applyTranslation();

        Saved?.Invoke(newHotkey);
        Close();
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
