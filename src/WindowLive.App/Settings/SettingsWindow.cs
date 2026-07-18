using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using WindowLive.App.Hotkeys;
using WindowLive.App.Llm;
using WindowLive.App.Ui;
using WindowLive.Core.Config;
using WindowLive.Core.Language;
using WindowLive.Core.Llm;

namespace WindowLive.App.Settings;

/// <summary>
/// The Settings window, restyled to <c>design_handoff_project_window_1b</c>
/// section 3: a 400dip-wide borderless dark panel with a custom title bar,
/// six stacked sections (MODEL, LANGUAGES, OCR, API KEY, PROMPT, HOTKEYS),
/// and NO Save button — every control commits its change to
/// <see cref="AppConfig"/> (+ <see cref="AppConfig.Save"/>) immediately, per
/// the design's "Interactions &amp; Behavior: settings apply immediately"
/// rule. Text inputs debounce ~600ms before committing/saving; segmented
/// controls and combo boxes commit on the spot. Hotkeys are the one
/// exception to "commit optimistically": a captured chord is only persisted
/// after <paramref name="tryReplaceHotkey"/> (passed to the constructor)
/// confirms the OS accepted the live re-registration — see
/// <see cref="OnHotkeyCommitted"/>.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private const string CustomProviderValue = "custom";
    private const string LocalProviderValue = "local";
    private const string TesseractOcrValue = "tesseract";
    private const string VisionOcrValue = "vision";
    private const string AutoLanguageCode = "auto";
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(600);

    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly Func<string, string, (bool Ok, string Error)> _tryReplaceHotkey;
    private readonly Action _onBackendSettingsChanged;

    // MODEL
    private readonly SegmentedControl _providerControl;
    private readonly Border _localModelBox;
    private readonly StackPanel _customFieldsPanel;
    private readonly PlaceholderTextBox _endpointUrlBox;
    private readonly PlaceholderTextBox _customModelNameBox;
    private readonly TextBlock _statusLine;
    private readonly DispatcherTimer _customFieldsDebounce;
    private CancellationTokenSource? _healthCts;

    // LANGUAGES
    private readonly TextBlock _localNoteText;

    // API KEY
    private readonly StackPanel _apiKeySection;
    private readonly PasswordBox _apiKeyPasswordBox;
    private readonly TextBox _apiKeyTextBox;
    private readonly TextButton _showApiKeyButton;
    private bool _apiKeyVisible;

    // PROMPT
    private readonly TextBox _promptBox;
    private bool _suppressPromptEvents;
    private readonly DispatcherTimer _promptDebounce;

    // HOTKEYS
    private readonly HotkeyCaptureBox _desktopCapture;
    private readonly HotkeyCaptureBox _gameCapture;

    /// <summary>Raised (slot, newHotkey) after a hotkey row's captured chord is successfully re-registered live and persisted.</summary>
    public event Action<string, string>? HotkeyChanged;

    public SettingsWindow(
        AppConfig config,
        HttpClient http,
        Func<string, string, (bool Ok, string Error)> tryReplaceHotkey,
        Action onBackendSettingsChanged)
    {
        _config = config;
        _http = http;
        _tryReplaceHotkey = tryReplaceHotkey;
        _onBackendSettingsChanged = onBackendSettingsChanged;

        Title = "Settings";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        FontFamily = Theme.UiFontFamily;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _customFieldsDebounce = new DispatcherTimer { Interval = DebounceDelay };
        _customFieldsDebounce.Tick += (_, _) =>
        {
            _customFieldsDebounce.Stop();
            CommitCustomFields();
        };
        _promptDebounce = new DispatcherTimer { Interval = DebounceDelay };
        _promptDebounce.Tick += (_, _) =>
        {
            _promptDebounce.Stop();
            CommitPromptEdit();
        };

        // ---- Title bar ----
        var titleText = new TextBlock
        {
            Text = "Settings",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var closeButton = new TextButton("✕") { VerticalAlignment = VerticalAlignment.Center };
        closeButton.Click += Close;

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(closeButton, 1);
        titleRow.Children.Add(titleText);
        titleRow.Children.Add(closeButton);

        var titleBar = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = titleRow,
        };
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, closeButton)) return;
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { /* not in a WM_LBUTTONDOWN — ignore */ }
        };

        // ---- MODEL ----
        _providerControl = new SegmentedControl("Local", "Custom endpoint");
        _providerControl.SelectedIndex = IsCustomProvider(config.Provider) ? 1 : 0;
        _providerControl.SelectionChanged += OnProviderChanged;

        _localModelBox = new Border
        {
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            Child = new TextBlock
            {
                Text = StripGgufExtension(config.ModelFile),
                FontFamily = Theme.MonoFontFamily,
                FontSize = 11.5,
                Foreground = Theme.TextPrimary,
            },
        };

        _endpointUrlBox = new PlaceholderTextBox("https://api.openai.com", config.CustomEndpointUrl);
        _customModelNameBox = new PlaceholderTextBox("model name", config.CustomModelName) { Margin = new Thickness(0, 7, 0, 0) };
        _customFieldsPanel = new StackPanel();
        _customFieldsPanel.Children.Add(_endpointUrlBox);
        _customFieldsPanel.Children.Add(_customModelNameBox);
        _endpointUrlBox.TextChanged += (_, _) => ScheduleCustomFieldsDebounce();
        _customModelNameBox.TextChanged += (_, _) => ScheduleCustomFieldsDebounce();

        _statusLine = new TextBlock { FontSize = 10.5, Foreground = Theme.TextMuted, Text = "● checking…" };

        var modelSection = Section("MODEL", _providerControl, _localModelBox, _customFieldsPanel, _statusLine);

        // ---- LANGUAGES ----
        var sourceOptions = new System.Collections.Generic.List<LangOption> { new(AutoLanguageCode, "Auto-detect") };
        sourceOptions.AddRange(LanguageCatalog.All.Select(l => new LangOption(l.Code, l.DisplayName)));
        var targetOptions = LanguageCatalog.All.Select(l => new LangOption(l.Code, l.DisplayName)).ToList();

        var sourceCombo = new ComboBox { Style = ComboBoxStyle, ItemsSource = sourceOptions };
        sourceCombo.SelectedItem = sourceOptions.FirstOrDefault(o => string.Equals(o.Code, config.SourceLanguage, StringComparison.OrdinalIgnoreCase))
            ?? sourceOptions[0];
        sourceCombo.SelectionChanged += (_, _) =>
        {
            if (sourceCombo.SelectedItem is not LangOption opt) return;
            _config.SourceLanguage = opt.Code;
            _config.Save();
            _onBackendSettingsChanged();
        };

        var targetCombo = new ComboBox { Style = ComboBoxStyle, ItemsSource = targetOptions };
        targetCombo.SelectedItem = targetOptions.FirstOrDefault(o => string.Equals(o.Code, config.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            ?? targetOptions.First(o => o.Code == "en");
        targetCombo.SelectionChanged += (_, _) =>
        {
            if (targetCombo.SelectedItem is not LangOption opt) return;
            _config.TargetLanguage = opt.Code;
            _config.Save();
            _onBackendSettingsChanged();
        };

        var arrow = new TextBlock
        {
            Text = "→",
            Foreground = Theme.Accent,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };
        var langRow = new Grid();
        langRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        langRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        langRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(sourceCombo, 0);
        Grid.SetColumn(arrow, 1);
        Grid.SetColumn(targetCombo, 2);
        langRow.Children.Add(sourceCombo);
        langRow.Children.Add(arrow);
        langRow.Children.Add(targetCombo);

        _localNoteText = new TextBlock
        {
            Text = "Local model outputs English — edit the prompt below to change it.",
            FontSize = 10,
            Foreground = Theme.TextMuted,
            TextWrapping = TextWrapping.Wrap,
        };

        var languagesSection = Section("LANGUAGES", langRow, _localNoteText);

        // ---- OCR ----
        var ocrControl = new SegmentedControl("Tesseract", "Vision");
        ocrControl.SelectedIndex = string.Equals(config.OcrEngine, TesseractOcrValue, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        ocrControl.SelectionChanged += index =>
        {
            _config.OcrEngine = index == 0 ? TesseractOcrValue : VisionOcrValue;
            _config.Save();
            _onBackendSettingsChanged();
        };
        var ocrSection = Section("OCR", ocrControl);

        // ---- API KEY ----
        _apiKeyPasswordBox = new PasswordBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.TextMuted,
            FontFamily = Theme.MonoFontFamily,
            FontSize = 11.5,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null,
            Password = config.CustomApiKey,
        };
        _apiKeyTextBox = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.TextMuted,
            FontFamily = Theme.MonoFontFamily,
            FontSize = 11.5,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null,
            Text = config.CustomApiKey,
            Visibility = Visibility.Collapsed,
        };
        _showApiKeyButton = new TextButton("show") { IsAccent = true, VerticalAlignment = VerticalAlignment.Center };
        _showApiKeyButton.Click += ToggleApiKeyVisibility;

        var apiKeyFieldGrid = new Grid();
        apiKeyFieldGrid.Children.Add(_apiKeyPasswordBox);
        apiKeyFieldGrid.Children.Add(_apiKeyTextBox);

        var apiKeyRow = new Grid();
        apiKeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        apiKeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(apiKeyFieldGrid, 0);
        Grid.SetColumn(_showApiKeyButton, 1);
        apiKeyRow.Children.Add(apiKeyFieldGrid);
        apiKeyRow.Children.Add(_showApiKeyButton);

        var apiKeyBorder = new Border
        {
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            Child = apiKeyRow,
        };
        _apiKeySection = Section("API KEY", apiKeyBorder);
        _apiKeyPasswordBox.PasswordChanged += (_, _) => ScheduleCustomFieldsDebounce();
        _apiKeyTextBox.TextChanged += (_, _) => ScheduleCustomFieldsDebounce();

        // ---- PROMPT ----
        _promptBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Theme.TileBg,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            Foreground = Theme.TextPrimary,
            CaretBrush = Theme.TextPrimary,
            FontFamily = Theme.MonoFontFamily,
            FontSize = 11.5,
            Padding = new Thickness(10, 7, 10, 7),
            Height = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FocusVisualStyle = null,
            Text = GetActiveTemplateText(),
        };
        _promptBox.TextChanged += (_, _) =>
        {
            if (_suppressPromptEvents) return;
            _promptDebounce.Stop();
            _promptDebounce.Start();
        };

        var legend = new TextBlock
        {
            Text = "Placeholders: {text} {source} {target}",
            FontSize = 10,
            Foreground = Theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var resetPromptButton = new TextButton("reset to default") { IsAccent = true, VerticalAlignment = VerticalAlignment.Center };
        resetPromptButton.Click += ResetPromptToDefault;

        var promptFooter = new Grid();
        promptFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        promptFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(legend, 0);
        Grid.SetColumn(resetPromptButton, 1);
        promptFooter.Children.Add(legend);
        promptFooter.Children.Add(resetPromptButton);

        var promptSection = Section("PROMPT", _promptBox, promptFooter);

        // ---- HOTKEYS ----
        _desktopCapture = new HotkeyCaptureBox();
        _desktopCapture.Initialize(config.DesktopHotkey);
        _gameCapture = new HotkeyCaptureBox();
        _gameCapture.Initialize(config.GameModeHotkey);

        var desktopRow = BuildHotkeyRow("Snip & translate", _desktopCapture, out TextBlock desktopError);
        var gameRow = BuildHotkeyRow("Game mode region", _gameCapture, out TextBlock gameError);
        _desktopCapture.Committed += hotkey => OnHotkeyCommitted(HotkeyManager.DesktopSlot, _desktopCapture, desktopError, hotkey);
        _gameCapture.Committed += hotkey => OnHotkeyCommitted(HotkeyManager.GameModeSlot, _gameCapture, gameError, hotkey);

        var hotkeysSection = Section("HOTKEYS", desktopRow, gameRow);

        // ---- Body ----
        var body = new StackPanel { Margin = new Thickness(16) };
        UIElement[] sections = [modelSection, languagesSection, ocrSection, _apiKeySection, promptSection, hotkeysSection];
        for (int i = 0; i < sections.Length; i++)
        {
            if (i > 0 && sections[i] is FrameworkElement fe)
                fe.Margin = new Thickness(0, 18, 0, 0);
            body.Children.Add(sections[i]);
        }

        var rootDock = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        rootDock.Children.Add(titleBar);
        rootDock.Children.Add(body);

        Content = new Border
        {
            Background = Theme.WindowBg,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            Child = rootDock,
        };

        UpdateProviderVisibility();

        Loaded += (_, _) => RunHealthCheckAsync();
        Closed += (_, _) =>
        {
            _customFieldsDebounce.Stop();
            _promptDebounce.Stop();
            _healthCts?.Cancel();
        };
        PreviewKeyDown += OnPreviewKeyDownForEscape;
    }

    // ---- MODEL / provider ----

    private static bool IsCustomProvider(string provider) =>
        string.Equals(provider, CustomProviderValue, StringComparison.OrdinalIgnoreCase);

    private static string StripGgufExtension(string modelFile) =>
        modelFile.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? modelFile[..^5] : modelFile;

    private void OnProviderChanged(int index)
    {
        FlushPromptDebounce();
        _config.Provider = index == 1 ? CustomProviderValue : LocalProviderValue;
        _config.Save();
        UpdateProviderVisibility();
        LoadPromptBoxForActiveProvider();
        RunHealthCheckAsync();
        _onBackendSettingsChanged();
    }

    private void UpdateProviderVisibility()
    {
        bool isCustom = _providerControl.SelectedIndex == 1;
        _localModelBox.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
        _customFieldsPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        _localNoteText.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
        _apiKeySection.IsEnabled = isCustom;
        _apiKeySection.Opacity = isCustom ? 1.0 : 0.5;
    }

    private void ScheduleCustomFieldsDebounce()
    {
        _customFieldsDebounce.Stop();
        _customFieldsDebounce.Start();
    }

    private void CommitCustomFields()
    {
        _config.CustomEndpointUrl = _endpointUrlBox.GetText().Trim();
        _config.CustomModelName = _customModelNameBox.GetText().Trim();
        _config.CustomApiKey = _apiKeyVisible ? _apiKeyTextBox.Text : _apiKeyPasswordBox.Password;
        _config.Save();
        _onBackendSettingsChanged();
        RunHealthCheckAsync();
    }

    private async void RunHealthCheckAsync()
    {
        _healthCts?.Cancel();
        var cts = new CancellationTokenSource();
        _healthCts = cts;

        (bool ok, string message) = await BackendHealthCheck.CheckAsync(_http, _config, cts.Token).ConfigureAwait(true);

        if (cts.IsCancellationRequested) return; // superseded by a newer check
        _statusLine.Text = $"● {message}";
        _statusLine.Foreground = ok ? Theme.Accent : Theme.Error;
    }

    // ---- API KEY ----

    private void ToggleApiKeyVisibility()
    {
        _apiKeyVisible = !_apiKeyVisible;
        if (_apiKeyVisible)
        {
            _apiKeyTextBox.Text = _apiKeyPasswordBox.Password;
            _apiKeyPasswordBox.Visibility = Visibility.Collapsed;
            _apiKeyTextBox.Visibility = Visibility.Visible;
            _showApiKeyButton.Text = "hide";
            _apiKeyTextBox.Focus();
            _apiKeyTextBox.CaretIndex = _apiKeyTextBox.Text.Length;
        }
        else
        {
            _apiKeyPasswordBox.Password = _apiKeyTextBox.Text;
            _apiKeyTextBox.Visibility = Visibility.Collapsed;
            _apiKeyPasswordBox.Visibility = Visibility.Visible;
            _showApiKeyButton.Text = "show";
        }
    }

    // ---- PROMPT ----

    private string GetActiveTemplateText()
    {
        bool isCustom = _providerControl.SelectedIndex == 1;
        string? stored = isCustom ? _config.CustomPromptTemplate : _config.LocalPromptTemplate;
        return stored ?? (isCustom ? PromptTemplate.DefaultCustomTemplate : PromptTemplate.DefaultLocalTemplate);
    }

    private void LoadPromptBoxForActiveProvider()
    {
        _suppressPromptEvents = true;
        _promptBox.Text = GetActiveTemplateText();
        _suppressPromptEvents = false;
    }

    private void FlushPromptDebounce()
    {
        if (!_promptDebounce.IsEnabled) return;
        _promptDebounce.Stop();
        CommitPromptEdit();
    }

    private void CommitPromptEdit()
    {
        bool isCustom = _providerControl.SelectedIndex == 1;
        string text = _promptBox.Text;
        string builtinDefault = isCustom ? PromptTemplate.DefaultCustomTemplate : PromptTemplate.DefaultLocalTemplate;
        string? toStore = text == builtinDefault ? null : text;
        if (isCustom) _config.CustomPromptTemplate = toStore;
        else _config.LocalPromptTemplate = toStore;
        _config.Save();
        _onBackendSettingsChanged();
    }

    private void ResetPromptToDefault()
    {
        _promptDebounce.Stop();
        bool isCustom = _providerControl.SelectedIndex == 1;
        if (isCustom) _config.CustomPromptTemplate = null;
        else _config.LocalPromptTemplate = null;
        _config.Save();
        LoadPromptBoxForActiveProvider();
        _onBackendSettingsChanged();
    }

    // ---- HOTKEYS ----

    private static UIElement BuildHotkeyRow(string label, HotkeyCaptureBox capture, out TextBlock errorText)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = Theme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(capture, 1);
        row.Children.Add(labelBlock);
        row.Children.Add(capture);

        errorText = new TextBlock
        {
            FontSize = 10,
            Foreground = Theme.Error,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var wrap = new StackPanel();
        wrap.Children.Add(row);
        wrap.Children.Add(errorText);
        return wrap;
    }

    private void OnHotkeyCommitted(string slot, HotkeyCaptureBox capture, TextBlock errorText, string newHotkey)
    {
        string previous = slot == HotkeyManager.DesktopSlot ? _config.DesktopHotkey : _config.GameModeHotkey;
        if (newHotkey == previous)
        {
            errorText.Visibility = Visibility.Collapsed;
            return;
        }

        (bool ok, string error) = _tryReplaceHotkey(slot, newHotkey);
        if (ok)
        {
            if (slot == HotkeyManager.DesktopSlot) _config.DesktopHotkey = newHotkey;
            else _config.GameModeHotkey = newHotkey;
            _config.Save();
            errorText.Visibility = Visibility.Collapsed;
            HotkeyChanged?.Invoke(slot, newHotkey);
        }
        else
        {
            capture.RevertTo(previous);
            errorText.Text = string.IsNullOrWhiteSpace(error) ? "That shortcut could not be registered." : error;
            errorText.Visibility = Visibility.Visible;
        }
    }

    // ---- Window chrome / Esc ----

    private void OnPreviewKeyDownForEscape(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        // Let a currently-recording hotkey chip handle Escape itself (its own
        // PreviewKeyDown runs after this one, on the way down to the focused element).
        if (ReferenceEquals(Keyboard.FocusedElement, _desktopCapture) || ReferenceEquals(Keyboard.FocusedElement, _gameCapture))
            return;
        e.Handled = true;
        Close();
    }

    // ---- Layout helper ----

    /// <summary>Builds a section: uppercase mono label, then <paramref name="controls"/> each separated by a 7px gap.</summary>
    private static StackPanel Section(string label, params UIElement[] controls)
    {
        var stack = new StackPanel();
        stack.Children.Add(Theme.CreateSectionLabel(label));
        foreach (UIElement control in controls)
        {
            if (control is FrameworkElement fe)
                fe.Margin = new Thickness(fe.Margin.Left, 7, fe.Margin.Right, fe.Margin.Bottom);
            stack.Children.Add(control);
        }
        return stack;
    }

    private sealed record LangOption(string Code, string Display)
    {
        public override string ToString() => Display;
    }

    // ---- Dark ComboBox style ----
    // WPF's default ComboBox chrome is OS-themed and can't be recolored via simple
    // property setters, so the closed-state chrome and the popup list are replaced
    // with a compact flat template (1px Theme.Border frame, Theme.TileBg fill, "▾"
    // caret) matching the design's other inputs. Not pixel-perfect against native
    // dropdown behaviors (e.g. keyboard type-to-select highlight styling uses the
    // same hover color rather than a distinct one) — acceptable per the brief
    // ("don't fight WPF for pixel-perfection on the dropdown list itself").
    private static readonly Style ComboBoxStyle = (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="ComboBox">
          <Setter Property="Background" Value="#1c1c1c"/>
          <Setter Property="BorderBrush" Value="#2c2c2c"/>
          <Setter Property="BorderThickness" Value="1"/>
          <Setter Property="Foreground" Value="#f2f2f2"/>
          <Setter Property="FontFamily" Value="Segoe UI"/>
          <Setter Property="FontSize" Value="11.5"/>
          <Setter Property="SnapsToDevicePixels" Value="True"/>
          <Setter Property="ItemContainerStyle">
            <Setter.Value>
              <Style TargetType="ComboBoxItem">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Foreground" Value="#f2f2f2"/>
                <Setter Property="Padding" Value="10,7"/>
                <Setter Property="FontSize" Value="11.5"/>
                <Setter Property="FontFamily" Value="Segoe UI"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ComboBoxItem">
                      <Border x:Name="Bg" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                        <ContentPresenter/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsHighlighted" Value="True">
                          <Setter TargetName="Bg" Property="Background" Value="#1f1f1f"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                          <Setter TargetName="Bg" Property="Background" Value="#1f1f1f"/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </Setter.Value>
          </Setter>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ComboBox">
                <Grid>
                  <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press"
                                IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                    <ToggleButton.Template>
                      <ControlTemplate TargetType="ToggleButton">
                        <Border Background="#1c1c1c" BorderBrush="#2c2c2c" BorderThickness="1"/>
                      </ControlTemplate>
                    </ToggleButton.Template>
                  </ToggleButton>
                  <ContentPresenter x:Name="ContentSite" IsHitTestVisible="False"
                                     Content="{TemplateBinding SelectionBoxItem}"
                                     Margin="10,7,24,7" VerticalAlignment="Center" HorizontalAlignment="Left"/>
                  <TextBlock Text="&#9662;" Foreground="#5c5c5c" FontSize="9" VerticalAlignment="Center"
                             HorizontalAlignment="Right" Margin="0,0,10,0" IsHitTestVisible="False"/>
                  <Popup x:Name="Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}"
                         AllowsTransparency="True" Focusable="False" PopupAnimation="None">
                    <Border Background="#1c1c1c" BorderBrush="#2c2c2c" BorderThickness="1"
                            MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                      <ScrollViewer MaxHeight="220" SnapsToDevicePixels="True">
                        <ItemsPresenter/>
                      </ScrollViewer>
                    </Border>
                  </Popup>
                </Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);
}

/// <summary>
/// A single-line dark <see cref="TextBox"/> with a placeholder shown when
/// empty (WPF's TextBox has no built-in placeholder). Styled to the design's
/// input look: mono 11.5px, <see cref="Theme.TileBg"/> fill, 1px
/// <see cref="Theme.Border"/>, padding 7,10. Used for the MODEL section's
/// custom-endpoint URL and model-name fields.
/// </summary>
internal sealed class PlaceholderTextBox : Grid
{
    private readonly TextBox _box;
    private readonly TextBlock _placeholder;

    /// <summary>Raised on every keystroke, mirroring the inner TextBox's TextChanged.</summary>
    public event EventHandler? TextChanged;

    public PlaceholderTextBox(string placeholder, string initialText = "")
    {
        _box = new TextBox
        {
            Background = Theme.TileBg,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            Foreground = Theme.TextPrimary,
            CaretBrush = Theme.TextPrimary,
            FontFamily = Theme.MonoFontFamily,
            FontSize = 11.5,
            Padding = new Thickness(10, 7, 10, 7),
            FocusVisualStyle = null,
            Text = initialText,
        };
        _placeholder = new TextBlock
        {
            Text = placeholder,
            Foreground = Theme.TextMuted,
            FontFamily = Theme.MonoFontFamily,
            FontSize = 11.5,
            Margin = new Thickness(11, 8, 10, 8),
            IsHitTestVisible = false,
            Visibility = string.IsNullOrEmpty(initialText) ? Visibility.Visible : Visibility.Collapsed,
        };

        Children.Add(_box);
        Children.Add(_placeholder);

        // Wired after the initial Text assignment above so construction-time
        // TextChanged (from setting Text = initialText) never reaches this
        // handler or the outer TextChanged subscriber (attached by the caller
        // only once this constructor returns).
        _box.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = string.IsNullOrEmpty(_box.Text) ? Visibility.Visible : Visibility.Collapsed;
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public string GetText() => _box.Text;

    public void SetText(string text) => _box.Text = text;
}
