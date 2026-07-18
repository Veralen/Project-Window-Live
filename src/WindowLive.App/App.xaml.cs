using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using WindowLive.App.GameMode;
using WindowLive.App.Hotkeys;
using WindowLive.App.Llm;
using WindowLive.App.Logging;
using WindowLive.App.Overlay;
using WindowLive.App.Server;
using WindowLive.Core.Config;

namespace WindowLive.App;

/// <summary>
/// Background (no main window) app shell: system-tray icon, global hotkeys (one
/// slot each for desktop snip mode and game mode), the llama-server child
/// process lifecycle, and the capture-first snip / game-mode pipelines.
/// ShutdownMode is OnExplicitShutdown so the app keeps running in the tray
/// after each overlay closes.
/// </summary>
public partial class App : Application
{
    private AppConfig _config = new();
    private HotkeyManager? _hotkeys;
    private SnipController? _snip;
    private GameModeController? _gameMode;
    private TaskbarIcon? _tray;
    private MenuItem? _pauseResumeItem;
    private MenuItem? _stopGameModeItem;
    private Settings.SettingsWindow? _settingsWindow;

    // llama-server backend. GpuDetector selection happens once at startup; _llmClient
    // and _httpClient are constructed once and reused across Retry attempts, while
    // _serverManager is recreated per attempt (see StartServerAsync).
    private HttpClient? _httpClient;
    private LlamaServerManager? _serverManager;
    private LlamaClient? _llmClient;
    private TranslationBackend? _backend;
    private readonly ServerReadiness _readiness = new();
    private ModelSetupWindow? _setupWindow;

    // Single-instance guard. Held for the process lifetime so a second launch can
    // detect the running copy instead of failing silently (dead hotkey). Session-local
    // namespace is fine — one per user session.
    private const string SingleInstanceName = "WindowLive.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Diagnostics may carry non-ASCII OCR/game-chat text; keep console/redirected logs UTF-8.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* no console attached */ }

        // File logging + global exception surfacing come first: a WinExe has no
        // console, so without these an early failure is completely invisible.
        AppLog.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Single-instance enforcement BEFORE any heavy init (config, tray, hotkey).
        // A second launch must not steal state or register a duplicate hotkey.
        if (!TryAcquireSingleInstance())
        {
            Log("Another instance is running; exiting.");
            MessageBox.Show(
                "WindowLive is already running.\n\n" +
                "It lives in the system tray. Click the ^ (hidden icons) arrow near the " +
                "clock to find the blue W icon, then press your snip shortcut to use it.",
                "WindowLive is already running",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Version? version = GetType().Assembly.GetName().Version;
        Log($"WindowLive starting — version {version}, args: [{string.Join(" ", e.Args)}]");
        Log("Single-instance lock acquired.");

        _config = AppConfig.LoadOrDefault();

        BuildTrayIcon();

        // Startup visibility: Windows 11 hides new tray icons in the overflow
        // chevron, so users think the app never launched. One balloon per launch
        // tells them it's running and how to reach it, using the real hotkey.
        string hotkeyDisplay = _config.DesktopHotkey;
        ShowBalloon("WindowLive is running",
            $"Press {hotkeyDisplay} to snip. Right-click the tray icon for Settings " +
            "(click ^ near the clock if the icon is hidden).");

        // GpuDetector selection (after mutex/tray init, per docs/window-live-design.md
        // "GPU support"): a hard requirement, never a soft fallback. On failure this
        // shows a dialog and shuts down cleanly — the tray icon (already built above)
        // is torn down by OnExit like any other shutdown path.
        if (!InitializeTranslationBackend())
            return;

        Action<string, string> notify = (title, message) =>
            Dispatcher.Invoke(() => ShowBalloon(title, message));

        _snip = new SnipController(notify, _backend!, _readiness);
        _gameMode = new GameModeController(_config, _backend!, _readiness, notify);

        _hotkeys = new HotkeyManager();
        _hotkeys.Triggered += slot =>
        {
            if (slot == HotkeyManager.DesktopSlot) _snip!.BeginSnip(autoTest: false);
            else if (slot == HotkeyManager.GameModeSlot) _gameMode!.OnHotkeyPressed();
        };

        if (!_hotkeys.TryRegister(HotkeyManager.DesktopSlot, _config.DesktopHotkey, out string error))
        {
            Log($"Hotkey registration failed: {error}");
            ShowBalloon("Hotkey unavailable",
                $"Could not register '{_config.DesktopHotkey}'. Use the tray menu to snip. ({error})");
            MessageBox.Show(
                $"The shortcut '{hotkeyDisplay}' could not be registered — usually another " +
                "app (or another WindowLive instance) already owns it.\n\n" +
                "WindowLive is still running in the tray; you can snip from the tray menu " +
                "or pick a different shortcut via the tray icon → Settings…",
                "WindowLive — shortcut unavailable",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            Log($"Hotkey '{_config.DesktopHotkey}' registered.");
        }

        if (!_hotkeys.TryRegister(HotkeyManager.GameModeSlot, _config.GameModeHotkey, out string gameHotkeyError))
        {
            Log($"Game-mode hotkey registration failed: {gameHotkeyError}");
            ShowBalloon("Game-mode hotkey unavailable",
                $"Could not register '{_config.GameModeHotkey}'. Use the tray menu to set up game " +
                $"mode. ({gameHotkeyError})");
        }
        else
        {
            Log($"Game-mode hotkey '{_config.GameModeHotkey}' registered.");
        }

        // Launch llama-server in the background — never block the UI thread. A
        // progress window appears only if a real download happens or startup fails
        // (see StartServerAsync / OnDownloadProgress); a cached-model run warms up
        // silently. A custom-endpoint provider has no startup phase at all: mark
        // the gate ready immediately; per-request failures surface in the popup
        // and the settings health check instead.
        if (IsCustomProvider)
        {
            Log("Provider is 'custom' — skipping llama-server launch; backend readiness is immediate.");
            _readiness.MarkReady();
        }
        else
        {
            _ = StartServerAsync();
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
                DispatcherPriority.ApplicationIdle);
        }

        // --game-rect L,T,W,H : deterministically start game mode polling on an
        // explicit virtual-screen physical-px rectangle, skipping the
        // drag-to-select UI (mirrors --snip-rect; for testing/demos).
        Core.Geometry.PixelRect? explicitGameRegion = ParseGameRect(e.Args);
        if (explicitGameRegion is not null)
        {
            Log($"--game-rect requested: {explicitGameRegion} — skipping drag-to-select UI.");
            Dispatcher.BeginInvoke(new Action(() => _gameMode!.StartForDebugRect(explicitGameRegion.Value)),
                DispatcherPriority.ApplicationIdle);
        }

        // --settings: open the Settings window immediately (smoke-test affordance).
        if (e.Args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Log("--settings requested; opening Settings window.");
            Dispatcher.BeginInvoke(new Action(OpenSettings), DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    /// GPU detection (docs/window-live-design.md "GPU support") plus the shared
    /// HttpClient/LlamaClient construction. Returns false (after showing a dialog
    /// and calling Shutdown) if no supported GPU is present — CPU fallback is
    /// never attempted (CLAUDE.md hard rule). Does not start the server itself;
    /// see <see cref="StartServerAsync"/>.
    /// </summary>
    private bool InitializeTranslationBackend()
    {
        // GPU detection guards the EMBEDDED server only (no-CPU-fallback hard
        // rule). A custom-endpoint user needs no local GPU at all — skip the
        // check entirely so the app runs on any machine in that mode.
        if (!IsCustomProvider)
        {
            try
            {
                string binaryName = GpuDetector.SelectServerBinaryName();
                Log($"GPU detection selected server binary: {binaryName}");
            }
            catch (NoSupportedGpuException ex)
            {
                Log($"[fatal] {ex.Message}");
                MessageBox.Show(ex.Message, "WindowLive — no supported GPU",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return false;
            }
            catch (Exception ex)
            {
                // Anything unexpected out of DXGI/interop must still end in a clean
                // shutdown — a startup that limps on with no backend leaves the tray
                // alive but every dependent field null (zombie state).
                Log($"[fatal] GPU detection failed unexpectedly: {ex}");
                MessageBox.Show(
                    "WindowLive could not detect your GPU and cannot start.\n\nDetails: " + ex.Message +
                    $"\n\nLog: {AppLog.LogPath}",
                    "WindowLive — GPU detection failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return false;
            }
        }

        _httpClient = new HttpClient();
        _llmClient = new LlamaClient(_httpClient, _config);
        _backend = new TranslationBackend(_llmClient, _httpClient, _config, _readiness);

        // On-device language detection (source language "auto"): warm the
        // Lingua models in the background so the first snip doesn't pay the
        // load cost; detection itself is lazy/thread-safe either way.
        var detector = new Core.Language.TextLanguageDetector();
        _backend.SetLanguageDetector(detector.DetectCode);
        _ = Task.Run(() => detector.WarmUpAsync());
        return true;
    }

    /// <summary>
    /// Launches llama-server and waits for it to become ready, updating
    /// <see cref="_readiness"/> throughout so the snip pipeline can wait on it.
    /// Safe to call again (Retry from the setup window): builds a fresh
    /// <see cref="LlamaServerManager"/> each time since one whose child already
    /// exited refuses to StartAsync a second time. Never throws — all failures
    /// are logged and surfaced via the setup window instead.
    /// </summary>
    private async Task StartServerAsync()
    {
        _readiness.Reset();

        _serverManager?.Dispose();
        var manager = new LlamaServerManager(_config);
        manager.DownloadProgressChanged += pct =>
            Dispatcher.BeginInvoke(new Action(() => OnDownloadProgress(pct)));
        _serverManager = manager;

        try
        {
            Log("Starting llama-server…");
            await manager.StartAsync().ConfigureAwait(false);
            // Generous timeout: first run may pull ~600 MB (model + mmproj) on a slow
            // connection. WaitForReadyAsync polls /health every 300ms and fails fast
            // if the child process exits, so this is a ceiling, not a typical wait.
            await manager.WaitForReadyAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            Log("llama-server ready.");
            _readiness.MarkReady();
            await Dispatcher.InvokeAsync(() =>
            {
                _setupWindow?.Close();
                _setupWindow = null;
            });
        }
        catch (Exception ex)
        {
            Log($"[fatal] llama-server startup failed: {ex}");
            _readiness.MarkFailed(ex.Message);
            await Dispatcher.InvokeAsync(() =>
            {
                EnsureSetupWindow();
                _setupWindow!.ShowFailure(ex.Message);
            });
        }
    }

    /// <summary>UI-thread callback for DownloadProgressChanged — only fires during an actual download.</summary>
    private void OnDownloadProgress(int pct)
    {
        EnsureSetupWindow();
        _setupWindow!.SetStatus($"Downloading translation engine files… {pct}%");
        _setupWindow.SetProgress(pct);
    }

    /// <summary>
    /// Creates and shows the setup/progress window on first use. Only called from
    /// a download-progress tick or a startup failure — a cached-model run that
    /// starts cleanly never shows this window at all.
    /// </summary>
    private void EnsureSetupWindow()
    {
        if (_setupWindow is not null)
            return;

        var window = new ModelSetupWindow();
        window.RetryRequested += () =>
        {
            window.ResetForRetry();
            _ = StartServerAsync();
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_setupWindow, window))
                _setupWindow = null;
        };
        _setupWindow = window;
        window.Show();
    }

    /// <summary>
    /// Opens the Settings window (single instance — activates the existing one if
    /// already open). Settings apply immediately (design pack): backend-affecting
    /// changes flow through <see cref="ApplyBackendSettings"/>, and each hotkey
    /// row live-re-registers via <see cref="HotkeyManager.TryReplace"/>, persisting
    /// only on success (balloon per successful rebind).
    /// </summary>
    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        if (_httpClient is null)
        {
            // Backend init failed/aborted — the settings health check and
            // apply-immediately plumbing need it; nothing sensible to show.
            Log("Settings requested before backend init — ignoring.");
            return;
        }

        var window = new Settings.SettingsWindow(
            _config,
            _httpClient,
            (slot, hotkey) =>
            {
                // _hotkeys is created late in OnStartup; it is null if startup is
                // still in flight or aborted. Fail the re-bind gracefully — never NRE.
                if (_hotkeys is null)
                    return (false, "WindowLive is still starting up (or startup failed) — try again in a moment.");
                bool ok = _hotkeys.TryReplace(slot, hotkey, out string error);
                if (ok) Log($"Hotkey '{slot}' re-registered to '{hotkey}'.");
                else Log($"Hotkey '{slot}' re-registration to '{hotkey}' failed: {error}");
                return (ok, error);
            },
            ApplyBackendSettings);
        window.HotkeyChanged += (slot, hotkey) =>
        {
            // _config is already updated by the window (same instance); per-row
            // apply-immediately means one balloon per successful rebind.
            string label = slot == HotkeyManager.DesktopSlot ? "Snip & translate" : "Game mode";
            ShowBalloon("Shortcut updated", $"{label}: {hotkey}");
        };
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
        window.Activate();
    }

    private bool IsCustomProvider =>
        string.Equals(_config.Provider, "custom", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Called by the Settings window after any backend-affecting setting
    /// (provider, endpoint, key, model, OCR engine, languages, prompt) has been
    /// saved to config. Rebuilds the backend facade and reconciles the
    /// llama-server lifecycle with the selected provider: custom needs no
    /// server (the gate is marked ready; the running server, if any, is left
    /// alive so switching back is instant); switching back to local lazily
    /// GPU-checks and starts the server. Unlike the startup path, a runtime
    /// GPU failure keeps the app alive — the user just stays on the failed
    /// readiness state until they pick the custom provider again.
    /// </summary>
    internal void ApplyBackendSettings()
    {
        if (_backend is null) return;
        _backend.Rebuild();

        if (IsCustomProvider)
        {
            if (!_readiness.IsReady)
            {
                _readiness.Reset();
                _readiness.MarkReady();
            }
            return;
        }

        if (_readiness.IsReady && _serverManager is not null)
            return; // local already up (or coming up) — nothing to reconcile

        try
        {
            string binaryName = GpuDetector.SelectServerBinaryName();
            Log($"GPU detection (runtime provider switch) selected server binary: {binaryName}");
        }
        catch (Exception ex)
        {
            Log($"GPU detection failed on runtime provider switch: {ex.Message}");
            _readiness.Reset();
            _readiness.MarkFailed(ex.Message);
            MessageBox.Show(
                ex.Message + "\n\nSwitch the provider back to Custom endpoint to keep translating.",
                "WindowLive — no supported GPU",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _ = StartServerAsync();
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

    private static Core.Geometry.PixelRect? ParseGameRect(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, "--game-rect", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return null;
        string[] p = args[i + 1].Split(',', StringSplitOptions.TrimEntries);
        if (p.Length != 4) return null;
        if (double.TryParse(p[0], out double l) && double.TryParse(p[1], out double t) &&
            double.TryParse(p[2], out double w) && double.TryParse(p[3], out double h))
            return new Core.Geometry.PixelRect(l, t, w, h);
        return null;
    }

    private void BuildTrayIcon()
    {
        // Design-pack tray styling (Ui/TrayMenuStyles.xaml, merged in App.xaml).
        // Keyed styles, applied explicitly so nothing else in the app inherits
        // them; missing resources (defensive) just fall back to default chrome.
        var menuStyle = TryFindResource("TrayContextMenu") as Style;
        var itemStyle = TryFindResource("TrayMenuItem") as Style;
        var separatorStyle = TryFindResource("TrayMenuSeparator") as Style;

        var menu = new ContextMenu();
        if (menuStyle is not null) menu.Style = menuStyle;

        MenuItem StyledItem(string header)
        {
            var item = new MenuItem { Header = header };
            if (itemStyle is not null) item.Style = itemStyle;
            return item;
        }
        Separator StyledSeparator()
        {
            var sep = new Separator();
            if (separatorStyle is not null) sep.Style = separatorStyle;
            return sep;
        }

        var snipItem = StyledItem("Snip & translate");
        snipItem.Click += (_, _) => _snip!.BeginSnip(autoTest: false);
        menu.Items.Add(snipItem);

        var settingsItem = StyledItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(StyledSeparator());

        var redefineItem = StyledItem("Set up / redefine game chat region");
        redefineItem.Click += (_, _) => _gameMode?.OnHotkeyPressed();
        menu.Items.Add(redefineItem);

        _pauseResumeItem = StyledItem("Pause translation");
        _pauseResumeItem.IsEnabled = false;
        _pauseResumeItem.Click += (_, _) => _gameMode?.TogglePause();
        menu.Items.Add(_pauseResumeItem);

        _stopGameModeItem = StyledItem("Stop game mode");
        _stopGameModeItem.IsEnabled = false;
        _stopGameModeItem.Click += (_, _) => _gameMode?.Stop();
        menu.Items.Add(_stopGameModeItem);

        // Refresh game-mode item state and hotkey glyph hints right before the
        // menu opens (rather than reactively on every state change) — cheap,
        // always current, and picks up Settings rebinds automatically.
        menu.Opened += (_, _) =>
        {
            bool running = _gameMode?.IsRunning ?? false;
            bool settingUp = _gameMode?.IsSettingUp ?? false;
            bool paused = _gameMode?.IsPaused ?? false;
            _pauseResumeItem.Header = paused ? "Resume translation" : "Pause translation";
            _pauseResumeItem.IsEnabled = running;
            _stopGameModeItem.IsEnabled = running || settingUp;
            snipItem.InputGestureText = Ui.HotkeyDisplay.ToGlyphs(_config.DesktopHotkey);
            redefineItem.InputGestureText = Ui.HotkeyDisplay.ToGlyphs(_config.GameModeHotkey);
        };

        menu.Items.Add(StyledSeparator());

        var exitItem = StyledItem("Exit");
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        _tray = new TaskbarIcon
        {
            ToolTipText = "WindowLive",
            Icon = BuildTrayIconGraphic(),
            ContextMenu = menu,
        };
        _tray.TrayLeftMouseUp += (_, _) => _snip!.BeginSnip(autoTest: false);
        _tray.ForceCreate();
    }

    /// <summary>
    /// Loads the 32px frame of the design-pack app icon (Assets/WindowLive.ico,
    /// embedded as a WPF pack resource) for the tray — the reliable H.NotifyIcon
    /// path (IconSource requires a pack-URI BitmapImage; the Icon property takes
    /// any HICON). Falls back to a drawn placeholder if the resource is missing,
    /// so a packaging mistake degrades visibly instead of crashing startup.
    /// </summary>
    private static System.Drawing.Icon BuildTrayIconGraphic()
    {
        try
        {
            var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/WindowLive.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream, 32, 32);
            }
        }
        catch (Exception ex)
        {
            Log($"Tray icon resource load failed, using fallback: {ex.Message}");
        }
        return BuildFallbackTrayIconGraphic();
    }

    private static System.Drawing.Icon BuildFallbackTrayIconGraphic()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x1C, 0x1C, 0x1C));
            g.FillRectangle(bg, 2, 2, 28, 28);
            using var font = new System.Drawing.Font("Segoe UI", 16, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            using var mint = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x47, 0xD6, 0xA2));
            using var sf = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
            };
            g.DrawString("W", font, mint, new System.Drawing.RectangleF(0, 0, 32, 32), sf);
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
        // Stop game mode's poll loop (cancels any in-flight request, disposes the
        // timer, hides the panel) before the server manager — which owns the
        // llama-server child process — is torn down.
        _gameMode?.Shutdown();
        _snip?.CloseAll();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _backend?.Dispose(); // releases cached Tesseract engines
        _serverManager?.Dispose(); // kills the llama-server child process
        _httpClient?.Dispose();
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
                $"WindowLive hit an unexpected error: {e.Exception.Message}. " +
                $"Details: {AppLog.LogPath}",
                "WindowLive error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
