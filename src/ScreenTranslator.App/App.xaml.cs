using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using ScreenTranslator.App.Hotkeys;
using ScreenTranslator.App.Ocr;
using ScreenTranslator.App.Overlay;
using ScreenTranslator.App.Pipeline;
using ScreenTranslator.Core.Config;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App;

/// <summary>
/// Background (no main window) app shell: system-tray icon, global hotkey, and
/// the capture-first snip pipeline. ShutdownMode is OnExplicitShutdown so the app
/// keeps running in the tray after each overlay closes.
/// </summary>
public partial class App : Application
{
    private AppConfig _config = new();
    private HotkeyManager? _hotkeys;
    private ITranslator? _translator;
    private SnipController? _snip;
    private TaskbarIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _config = AppConfig.LoadOrDefault();

        Action<string, string> notify = (title, message) =>
            Dispatcher.Invoke(() => ShowBalloon(title, message));

        var ocr = new WindowsOcrService(_config.OcrLanguage, notify);
        var factory = new PipelineFactory(_config);
        _translator = factory.CreateTranslator();
        // Kick off model load once at startup; the snip pipeline awaits this
        // same task before its first translate so an early hotkey can't race
        // an uninitialized engine.
        Task translatorReady = InitializeTranslatorAsync(_translator);

        _snip = new SnipController(_config, ocr, _translator, translatorReady, factory, notify);

        BuildTrayIcon();

        _hotkeys = new HotkeyManager();
        _hotkeys.Triggered += () => _snip!.BeginSnip(autoTest: false);
        if (!_hotkeys.TryRegister(_config.Hotkey, out string error))
        {
            Log($"Hotkey registration failed: {error}");
            ShowBalloon("Hotkey unavailable",
                $"Could not register '{_config.Hotkey}'. Use the tray menu to snip. ({error})");
        }
        else
        {
            Log($"Hotkey '{_config.Hotkey}' registered.");
        }

        // Debug affordances.
        bool snipNow = e.Args.Any(a => string.Equals(a, "--snip", StringComparison.OrdinalIgnoreCase));
        bool autoTest = e.Args.Any(a => string.Equals(a, "--auto-test", StringComparison.OrdinalIgnoreCase));
        if (snipNow)
        {
            Log($"--snip requested (auto-test={autoTest}).");
            Dispatcher.BeginInvoke(new Action(() => _snip!.BeginSnip(autoTest)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private static async System.Threading.Tasks.Task InitializeTranslatorAsync(ITranslator translator)
    {
        try { await translator.InitializeAsync(); }
        catch (Exception ex) { Log($"Translator init failed (using as-is): {ex.Message}"); }
    }

    private void BuildTrayIcon()
    {
        var menu = new ContextMenu();

        var snipItem = new MenuItem { Header = "Snip & translate" };
        snipItem.Click += (_, _) => _snip!.BeginSnip(autoTest: false);
        menu.Items.Add(snipItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        _tray = new TaskbarIcon
        {
            ToolTipText = "ScreenTranslator",
            Icon = BuildTrayIconGraphic(),
            ContextMenu = menu,
        };
        _tray.TrayLeftMouseUp += (_, _) => _snip!.BeginSnip(autoTest: false);
        _tray.ForceCreate();
    }

    /// <summary>
    /// Draws a simple 32x32 tray icon (blue rounded square + "T") as a
    /// System.Drawing.Icon — the reliable H.NotifyIcon path (IconSource requires a
    /// pack-URI BitmapImage; the Icon property takes any HICON).
    /// </summary>
    private static System.Drawing.Icon BuildTrayIconGraphic()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x2D, 0x7D, 0xF6));
            g.FillRectangle(bg, 2, 2, 28, 28);
            using var font = new System.Drawing.Font("Segoe UI", 16, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var sf = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
            };
            g.DrawString("T", font, white, new System.Drawing.RectangleF(0, 0, 32, 32), sf);
        }
        IntPtr hIcon = bmp.GetHicon();
        // Clone so the Icon owns managed data independent of the transient HICON.
        using var tmp = System.Drawing.Icon.FromHandle(hIcon);
        return (System.Drawing.Icon)tmp.Clone();
    }

    private void ShowBalloon(string title, string message)
    {
        try { _tray?.ShowNotification(title, message); }
        catch (Exception ex) { Log($"Balloon failed ({title}: {message}) — {ex.Message}"); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _snip?.CloseAll();
        _hotkeys?.Dispose();
        _translator?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }

    private static void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);
        Console.WriteLine(line);
    }
}
