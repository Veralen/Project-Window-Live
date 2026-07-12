using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowLive.App.Server;

/// <summary>
/// First-run (or startup-failure) progress window for the llama-server child
/// process: a status line, a determinate progress bar fed by
/// <see cref="LlamaServerManager.DownloadProgressChanged"/>, and a Retry button
/// that only appears once startup has failed (docs/window-live-design.md
/// "Error handling" — "Model download fails: show progress UI with retry
/// option"). App.xaml.cs owns the show/hide lifecycle and decides when this
/// window is even created — it only renders whatever state it's told.
/// </summary>
internal sealed class ModelSetupWindow : Window
{
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _retryButton;

    private static readonly Brush InkBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x25, 0x29));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));

    /// <summary>Raised when the user clicks Retry after a startup failure.</summary>
    public event Action? RetryRequested;

    public ModelSetupWindow()
    {
        Title = "WindowLive — setting up";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;

        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };

        root.Children.Add(new TextBlock
        {
            Text = "Setting up the translation engine",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = InkBrush,
            Margin = new Thickness(0, 0, 0, 12),
        });

        _status = new TextBlock
        {
            Text = "Starting…",
            FontSize = 12.5,
            Foreground = InkBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        root.Children.Add(_status);

        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 8,
            IsIndeterminate = true,
        };
        root.Children.Add(_progress);

        _retryButton = new Button
        {
            Content = "Retry",
            MinWidth = 88,
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 13.5,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xF6)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xF6)),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
        };
        _retryButton.Click += (_, _) => RetryRequested?.Invoke();
        root.Children.Add(_retryButton);

        Content = root;
    }

    /// <summary>Updates the status line without touching progress/Retry state.</summary>
    public void SetStatus(string text)
    {
        _status.Foreground = InkBrush;
        _status.Text = text;
    }

    /// <summary>Switches the progress bar to determinate mode at the given percentage.</summary>
    public void SetProgress(int pct)
    {
        _progress.IsIndeterminate = false;
        _progress.Value = Math.Clamp(pct, 0, 100);
    }

    /// <summary>Switches to the failed state: error text, progress reset, Retry shown.</summary>
    public void ShowFailure(string message)
    {
        _status.Foreground = ErrorBrush;
        _status.Text = "Setup failed: " + message;
        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        _retryButton.Visibility = Visibility.Visible;
    }

    /// <summary>Resets to the "starting" state before a new attempt begins.</summary>
    public void ResetForRetry()
    {
        _retryButton.Visibility = Visibility.Collapsed;
        SetStatus("Starting…");
        _progress.IsIndeterminate = true;
    }
}
