using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using ScreenTranslator.App.Hotkeys;
using ScreenTranslator.App.Logging;
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
    private Settings.SettingsWindow? _settingsWindow;

    // Single-instance guard. Held for the process lifetime so a second launch can
    // detect the running copy instead of failing silently (dead hotkey, extra
    // ~500MB model copy). Session-local namespace is fine — one per user session.
    private const string SingleInstanceName = "ScreenTranslator.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Diagnostics carry Chinese OCR text; keep console/redirected logs UTF-8.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* no console attached */ }

        // File logging + global exception surfacing come first: a WinExe has no
        // console, so without these an early failure is completely invisible.
        AppLog.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Single-instance enforcement BEFORE any heavy init (config, OCR, model,
        // tray, hotkey). A second launch must not load the model or steal state.
        if (!TryAcquireSingleInstance())
        {
            Log("Another instance is running; exiting.");
            MessageBox.Show(
                "ScreenTranslator is already running.\n\n" +
                "It lives in the system tray. Click the ^ (hidden icons) arrow near the " +
                "clock to find the blue T icon, then press your snip shortcut to use it.",
                "ScreenTranslator is already running",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Version? version = GetType().Assembly.GetName().Version;
        Log($"ScreenTranslator starting — version {version}, args: [{string.Join(" ", e.Args)}]");
        Log("Single-instance lock acquired.");

        _config = AppConfig.LoadOrDefault();
        Log($"Model directory resolved: {_config.ResolveModelDirectory()}");

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

        // Startup visibility: Windows 11 hides new tray icons in the overflow
        // chevron, so users think the app never launched. One balloon per launch
        // tells them it's running and how to reach it, using the real hotkey.
        string hotkeyDisplay = Settings.HotkeyCaptureBox.ToDisplay(_config.Hotkey);
        ShowBalloon("ScreenTranslator is running",
            $"Press {hotkeyDisplay} to snip. Right-click the tray icon for Settings " +
            "(click ^ near the clock if the icon is hidden).");

        _hotkeys = new HotkeyManager();
        _hotkeys.Triggered += () => _snip!.BeginSnip(autoTest: false);
        if (!_hotkeys.TryRegister(_config.Hotkey, out string error))
        {
            Log($"Hotkey registration failed: {error}");
            ShowBalloon("Hotkey unavailable",
                $"Could not register '{_config.Hotkey}'. Use the tray menu to snip. ({error})");
            MessageBox.Show(
                $"The shortcut '{hotkeyDisplay}' could not be registered — usually another " +
                "app (or another ScreenTranslator instance) already owns it.\n\n" +
                "ScreenTranslator is still running in the tray; you can snip from the tray menu " +
                "or pick a different shortcut via the tray icon → Settings…",
                "ScreenTranslator — shortcut unavailable",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            Log($"Hotkey '{_config.Hotkey}' registered.");
        }

        // Debug affordances.
        bool snipNow = e.Args.Any(a => string.Equals(a, "--snip", StringComparison.OrdinalIgnoreCase));
        bool autoTest = e.Args.Any(a => string.Equals(a, "--auto-test", StringComparison.OrdinalIgnoreCase));
        // --snip-rect L,T,W,H : deterministically snip an explicit virtual-screen
        // physical-px rectangle (implies --snip --auto-test). For testing/demos.
        Core.Geometry.PixelRect? explicitRegion = ParseSnipRect(e.Args);
        if (explicitRegion is not null)
        {
            snipNow = true;
            autoTest = true;
        }
        _snip.SaveShotPath = GetArgValue(e.Args, "--save-shot");
        if (snipNow)
        {
            Log($"--snip requested (auto-test={autoTest}, rect={explicitRegion}, saveShot={_snip.SaveShotPath}).");
            Dispatcher.BeginInvoke(new Action(() => _snip!.BeginSnip(autoTest, explicitRegion)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // --settings: open the Settings window immediately (smoke-test affordance).
        if (e.Args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Log("--settings requested; opening Settings window.");
            Dispatcher.BeginInvoke(new Action(OpenSettings),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // --ocr-image <path> [--crop L,T,W,H] : run OCR→group→translate on a static
        // image file (deterministic, no live screen capture) and log each zh→en
        // block, then exit. Verification/demo affordance.
        string? ocrImage = GetArgValue(e.Args, "--ocr-image");
        if (ocrImage is not null)
        {
            Core.Geometry.PixelRect? crop = ParseCrop(GetArgValue(e.Args, "--crop"));
            _ = RunOcrImageTestAsync(ocrImage, crop, ocr, factory, translatorReady);
        }
    }

    private async System.Threading.Tasks.Task RunOcrImageTestAsync(
        string path, Core.Geometry.PixelRect? crop, Ocr.WindowsOcrService ocr,
        PipelineFactory factory, Task translatorReady)
    {
        try
        {
            var region = LoadImageRegion(path, crop);
            Log($"[ocr-image] Loaded region {region.PixelWidth}x{region.PixelHeight} from {path}");
            var ocrResult = await ocr.RecognizeAsync(region);
            Log($"[ocr-image] OCR: {ocrResult.Lines.Count} lines (lang={ocrResult.LanguageTag})");
            var blocks = factory.CreateGrouper().Group(ocrResult);
            Log($"[ocr-image] Group: {blocks.Count} blocks");
            await translatorReady;
            int i = 0;
            foreach (var b in blocks)
            {
                string en = await _translator!.TranslateAsync(b.Text);
                Log($"[ocr-image] block {++i} ZH: {b.Text}");
                Log($"[ocr-image] block {i}   EN: {en}");
            }
            Log("[ocr-image] done.");
        }
        catch (Exception ex) { Log($"[ocr-image] ERROR: {ex}"); }
        finally { Shutdown(); }
    }

    private static Core.Ocr.CapturedRegion LoadImageRegion(string path, Core.Geometry.PixelRect? crop)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.UriSource = new Uri(System.IO.Path.GetFullPath(path));
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.EndInit();

        BitmapSource bmp = src;
        if (crop is { } c)
        {
            int cx = (int)Math.Clamp(c.X, 0, bmp.PixelWidth - 1);
            int cy = (int)Math.Clamp(c.Y, 0, bmp.PixelHeight - 1);
            int cw = (int)Math.Min(c.Width, bmp.PixelWidth - cx);
            int ch = (int)Math.Min(c.Height, bmp.PixelHeight - cy);
            bmp = new CroppedBitmap(bmp, new Int32Rect(cx, cy, cw, ch));
        }

        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        conv.CopyPixels(px, stride, 0);
        return new Core.Ocr.CapturedRegion(px, w, h, new Core.Geometry.PixelRect(0, 0, w, h));
    }

    private static Core.Geometry.PixelRect? ParseCrop(string? value)
    {
        if (value is null) return null;
        string[] p = value.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length == 4 &&
            double.TryParse(p[0], out double l) && double.TryParse(p[1], out double t) &&
            double.TryParse(p[2], out double w) && double.TryParse(p[3], out double h))
            return new Core.Geometry.PixelRect(l, t, w, h);
        return null;
    }

    /// <summary>
    /// Opens the Settings window (single instance — activates the existing one if
    /// already open). Save live-re-registers the hotkey via <see cref="HotkeyManager.TryReplace"/>
    /// and only persists on success; on success the current binding + a tray balloon update.
    /// </summary>
    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new Settings.SettingsWindow(_config, hotkey =>
        {
            bool ok = _hotkeys!.TryReplace(hotkey, out string error);
            if (ok) Log($"Hotkey re-registered to '{hotkey}'.");
            else Log($"Hotkey re-registration to '{hotkey}' failed: {error}");
            return (ok, error);
        });
        window.Saved += hotkey =>
        {
            // _config.Hotkey is already updated by the window (same instance), so a
            // later Settings open shows the current binding.
            ShowBalloon("Shortcut updated", $"Shortcut updated to {Settings.HotkeyCaptureBox.ToDisplay(hotkey)}");
        };
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
        window.Activate();
    }

    private static string? GetArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }

    private static Core.Geometry.PixelRect? ParseSnipRect(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, "--snip-rect", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return null;
        string[] p = args[i + 1].Split(',', StringSplitOptions.TrimEntries);
        if (p.Length != 4) return null;
        if (double.TryParse(p[0], out double l) && double.TryParse(p[1], out double t) &&
            double.TryParse(p[2], out double w) && double.TryParse(p[3], out double h))
            return new Core.Geometry.PixelRect(l, t, w, h);
        return null;
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

        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

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
        if (_singleInstanceMutex is not null)
        {
            try { if (_ownsSingleInstance) _singleInstanceMutex.ReleaseMutex(); } catch { /* ignore */ }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

    /// <summary>
    /// Acquires the process-wide single-instance mutex without blocking. Returns
    /// true when this process now owns it (including the abandoned-mutex case,
    /// where a previous owner exited without releasing).
    /// </summary>
    private bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceName);
        try
        {
            _ownsSingleInstance = _singleInstanceMutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Prior owner crashed without releasing; ownership transfers to us.
            _ownsSingleInstance = true;
        }
        return _ownsSingleInstance;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log($"[fatal] DispatcherUnhandledException: {e.Exception}");
        try
        {
            MessageBox.Show(
                $"ScreenTranslator hit an unexpected error: {e.Exception.Message}. " +
                $"Details: {AppLog.LogPath}",
                "ScreenTranslator error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* never let error handling throw */ }
        // Keep the tray app alive; a single failed snip shouldn't kill the process.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log($"[fatal] AppDomain.UnhandledException (terminating={e.IsTerminating}): {e.ExceptionObject}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log($"[fatal] UnobservedTaskException: {e.Exception}");
        e.SetObserved();
    }

    private static void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);
        try { Console.WriteLine(line); } catch { /* no console attached */ }
        AppLog.Write(line);
    }
}
