using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenTranslator.App.Capture;
using ScreenTranslator.App.Native;
using ScreenTranslator.App.Pipeline;
using ScreenTranslator.App.Rendering;
using ScreenTranslator.Core.Blocks;
using ScreenTranslator.Core.Config;
using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Ocr;
using ScreenTranslator.Core.Placement;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Overlay;

/// <summary>
/// Orchestrates one snip: capture-first, per-monitor overlays, and the
/// OCR → group → translate → place pipeline (all off the UI thread).
/// </summary>
internal sealed class SnipController
{
    private readonly AppConfig _config;
    private readonly IOcrService _ocr;
    private readonly ITranslator _translator;
    private readonly Task _translatorReady;
    private readonly PipelineFactory _factory;
    private readonly WpfTextMeasurer _measurer = new();
    private readonly Action<string, string> _notify;

    private readonly List<OverlayWindow> _overlays = new();
    private FrozenCapture? _frozen;
    private bool _active;
    private bool _selectionHandled;
    private DispatcherTimer? _autoCloseTimer;

    public SnipController(AppConfig config, IOcrService ocr, ITranslator translator,
        Task translatorReady, PipelineFactory factory, Action<string, string> notify)
    {
        _config = config;
        _ocr = ocr;
        _translator = translator;
        _translatorReady = translatorReady;
        _factory = factory;
        _notify = notify;
    }

    public bool IsActive => _active;

    /// <summary>When set, the overlay result is saved to this PNG path after rendering (test/demo).</summary>
    public string? SaveShotPath { get; set; }

    /// <summary>Runs the capture-first snip flow. Must be called on the UI thread.</summary>
    /// <param name="autoTest">Auto-select a region and auto-close (headless smoke test).</param>
    /// <param name="explicitRegion">
    /// When set (with autoTest), snip exactly this virtual-screen physical-px
    /// rectangle instead of the hardcoded centre region. Used by --snip-rect to
    /// drive a deterministic capture over known on-screen text.
    /// </param>
    public void BeginSnip(bool autoTest = false, PixelRect? explicitRegion = null)
    {
        if (_active)
        {
            Log("Snip already active — ignoring trigger.");
            return;
        }
        _active = true;
        _selectionHandled = false;

        // 1) Capture the ENTIRE virtual screen BEFORE showing any UI.
        var swCap = Stopwatch.StartNew();
        _frozen = FrozenCapture.CaptureVirtualScreen();
        swCap.Stop();
        Log($"Captured virtual screen: {_frozen.Width}x{_frozen.Height} at origin ({_frozen.OriginX},{_frozen.OriginY}) in {swCap.ElapsedMilliseconds} ms");

        // 2) Build a frozen BitmapSource of the whole virtual screen.
        int stride = _frozen.Width * 4;
        var full = BitmapSource.Create(_frozen.Width, _frozen.Height, 96, 96,
            PixelFormats.Bgra32, null, _frozen.PixelsBgra32, stride);
        full.Freeze();

        // 3) One overlay per monitor, showing that monitor's crop of the capture.
        var monitors = MonitorInfo.EnumerateAll();
        Log($"Monitors detected: {monitors.Count}");
        foreach (var mon in monitors)
        {
            int cx = (int)(mon.Bounds.X - _frozen.OriginX);
            int cy = (int)(mon.Bounds.Y - _frozen.OriginY);
            int cw = Math.Min((int)mon.Bounds.Width, _frozen.Width - cx);
            int ch = Math.Min((int)mon.Bounds.Height, _frozen.Height - cy);
            ImageSource crop = new CroppedBitmap(full, new Int32Rect(Math.Max(0, cx), Math.Max(0, cy), Math.Max(1, cw), Math.Max(1, ch)));

            var overlay = new OverlayWindow(mon, crop);
            overlay.SelectionCompleted += OnSelectionCompleted;
            overlay.DismissRequested += CloseAll;
            _overlays.Add(overlay);
            overlay.ShowOnMonitor();
            Log($"Overlay created for monitor bounds=({mon.Bounds.X},{mon.Bounds.Y},{mon.Bounds.Width},{mon.Bounds.Height}) dpi={mon.Dpi} primary={mon.IsPrimary}");
        }

        // Focus the overlay under the cursor so Esc works immediately.
        FocusOverlayUnderCursor(monitors);

        if (autoTest)
            ScheduleAutoTest(monitors, explicitRegion);
    }

    private void FocusOverlayUnderCursor(IReadOnlyList<MonitorInfo> monitors)
    {
        if (_overlays.Count == 0) return;
        OverlayWindow target = _overlays[0];
        if (NativeMethods.GetCursorPos(out var pt))
        {
            var mon = MonitorInfo.ForPoint(monitors, new PixelPoint(pt.X, pt.Y));
            foreach (var o in _overlays)
                if (o.Monitor.Handle == mon.Handle) { target = o; break; }
        }
        target.Activate();
        target.Focus();
    }

    private void ScheduleAutoTest(IReadOnlyList<MonitorInfo> monitors, PixelRect? explicitRegion)
    {
        MonitorInfo primary = monitors[0];
        foreach (var m in monitors) if (m.IsPrimary) { primary = m; break; }

        // Default: 600x200 physical region centred on the primary monitor.
        PixelRect region = explicitRegion ?? new PixelRect(
            primary.Bounds.X + (primary.Bounds.Width - 600) / 2,
            primary.Bounds.Y + (primary.Bounds.Height - 200) / 2,
            600, 200);

        // Pick the overlay whose monitor contains the region's centre.
        MonitorInfo target = MonitorInfo.ForPoint(monitors, region.Center);

        var startTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        startTimer.Tick += (_, _) =>
        {
            startTimer.Stop();
            Log($"[auto-test] Auto-selecting region ({region.X},{region.Y},{region.Width},{region.Height}) on monitor {target.Handle}.");
            foreach (var o in _overlays)
                if (o.Monitor.Handle == target.Handle) { o.SelectProgrammatically(region); break; }
        };
        startTimer.Start();

        // Longer window when snipping real content so results stay visible for a screenshot.
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(explicitRegion is null ? 5 : 12) };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer!.Stop();
            Log("[auto-test] Auto-closing overlays and shutting down.");
            CloseAll();
            Application.Current?.Shutdown();
        };
        _autoCloseTimer.Start();
    }

    private void OnSelectionCompleted(OverlayWindow overlay, PixelRect selection)
    {
        if (_selectionHandled) return;
        _selectionHandled = true;
        Log($"Selection completed: ({selection.X},{selection.Y},{selection.Width},{selection.Height}) physical px.");
        overlay.ShowTranslatingIndicator();
        _ = RunPipelineAsync(overlay, selection);
    }

    private async Task RunPipelineAsync(OverlayWindow overlay, PixelRect selection)
    {
        var frozen = _frozen!;
        PixelRect monitorWorkArea = overlay.Monitor.WorkArea;
        try
        {
            var result = await Task.Run(async () =>
            {
                var total = Stopwatch.StartNew();

                var sw = Stopwatch.StartNew();
                CapturedRegion region = frozen.Crop(selection);
                sw.Stop();
                Log($"[pipeline] Crop: {region.PixelWidth}x{region.PixelHeight} px in {sw.ElapsedMilliseconds} ms");

                sw.Restart();
                OcrRegionResult ocr = await _ocr.RecognizeAsync(region).ConfigureAwait(false);
                sw.Stop();
                Log($"[pipeline] OCR: {ocr.Lines.Count} lines (lang={ocr.LanguageTag}) in {sw.ElapsedMilliseconds} ms");

                sw.Restart();
                IReadOnlyList<TextBlockGroup> blocks = _factory.CreateGrouper().Group(ocr);
                sw.Stop();
                Log($"[pipeline] Group: {ocr.Lines.Count} lines -> {blocks.Count} blocks in {sw.ElapsedMilliseconds} ms");

                sw.Restart();
                // Ensure the model finished loading before the first translate
                // (init runs from startup; an early snip could otherwise race it).
                await _translatorReady.ConfigureAwait(false);
                var translated = new List<TranslatedBlock>(blocks.Count);
                foreach (var block in blocks)
                {
                    string t;
                    try
                    {
                        t = await _translator.TranslateAsync(block.Text).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Degrade gracefully: a single block's failure shouldn't
                        // abort the whole snip. Show the source text instead.
                        Log($"[pipeline] Translate failed for a block: {ex.Message}");
                        t = block.Text;
                    }
                    translated.Add(new TranslatedBlock(block, t));
                }
                sw.Stop();
                Log($"[pipeline] Translate: {translated.Count} blocks in {sw.ElapsedMilliseconds} ms");

                sw.Restart();
                IReadOnlyList<PlacedLabel> placed = _factory.CreatePlacer().Place(translated, monitorWorkArea, _measurer);
                sw.Stop();
                total.Stop();
                Log($"[pipeline] Place: {placed.Count} labels in {sw.ElapsedMilliseconds} ms | pipeline total {total.ElapsedMilliseconds} ms");

                return (ocr.Lines.Count, placed);
            }).ConfigureAwait(true); // resume on UI thread (Dispatcher)

            // Back on the UI thread.
            if (result.Item1 == 0)
                overlay.ShowNoTextChip();
            else
                overlay.RenderLabels(result.placed);

            if (SaveShotPath is not null)
            {
                // Let the chips lay out, then snapshot the overlay to PNG.
                await overlay.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        overlay.SaveToPng(SaveShotPath);
                        Log($"[save-shot] Wrote overlay PNG: {SaveShotPath}");
                    }
                    catch (Exception ex) { Log($"[save-shot] Failed: {ex.Message}"); }
                }, DispatcherPriority.Loaded);
            }
        }
        catch (Exception ex)
        {
            Log($"[pipeline] ERROR: {ex}");
            overlay.HideTranslatingIndicator();
            _notify("Translation failed", ex.Message);
        }
    }

    public void CloseAll()
    {
        _autoCloseTimer?.Stop();
        foreach (var o in _overlays)
        {
            try { o.Close(); } catch { /* ignore */ }
        }
        _overlays.Clear();
        _frozen = null;
        _active = false;
    }

    private static void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);
        Console.WriteLine(line);
    }
}
