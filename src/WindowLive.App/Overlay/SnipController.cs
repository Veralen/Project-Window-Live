using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WindowLive.App.Capture;
using WindowLive.App.Llm;
using WindowLive.App.Server;
using WindowLive.Core.Geometry;

namespace WindowLive.App.Overlay;

/// <summary>
/// Orchestrates one snip: capture-first, per-monitor overlays, drag-to-select,
/// cropping the selected region, and dispatching it to llama-server for
/// translation (docs/window-live-design.md "Desktop mode (one-shot snip)").
/// </summary>
internal sealed class SnipController
{
    private int _inFlightPipelines;
    private readonly Action<string, string> _notify;
    private readonly LlamaClient _llm;
    private readonly ServerReadiness _readiness;

    private readonly List<OverlayWindow> _overlays = new();
    private FrozenCapture? _frozen;
    private bool _active;
    private bool _selectionHandled;
    private DispatcherTimer? _autoCloseTimer;
    private CancellationTokenSource? _pipelineCts;

    public SnipController(Action<string, string> notify, LlamaClient llm, ServerReadiness readiness)
    {
        _notify = notify;
        _llm = llm;
        _readiness = readiness;
    }

    public bool IsActive => _active;

    /// <summary>True while any snip pipeline is still running.</summary>
    public bool HasInFlightWork => Volatile.Read(ref _inFlightPipelines) > 0;

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
            // A "dead" hotkey press (icon hidden, user unsure it's running) should
            // visibly resurface the in-progress overlay rather than doing nothing.
            Log("Snip already active — resurfacing existing overlay(s).");
            ResurfaceOverlays();
            return;
        }
        _active = true;
        _selectionHandled = false;

        // 1) Capture the ENTIRE virtual screen BEFORE showing any UI.
        var swCap = Stopwatch.StartNew();
        _frozen = FrozenCapture.CaptureVirtualScreen();
        swCap.Stop();
        Log($"Captured virtual screen: {_frozen.Width}x{_frozen.Height} at origin ({_frozen.OriginX},{_frozen.OriginY}) in {swCap.ElapsedMilliseconds} ms");

        // 2) One overlay per monitor, showing that monitor's crop of the capture
        // (construction shared with game-mode region setup — see OverlaySetup).
        var monitors = MonitorInfo.EnumerateAll();
        Log($"Monitors detected: {monitors.Count}");
        foreach (var overlay in OverlaySetup.CreateOverlays(_frozen, monitors))
        {
            overlay.SelectionCompleted += OnSelectionCompleted;
            overlay.DismissRequested += CloseAll;
            _overlays.Add(overlay);
            overlay.ShowOnMonitor();
            Log($"Overlay created for monitor bounds=({overlay.Monitor.Bounds.X},{overlay.Monitor.Bounds.Y}," +
                $"{overlay.Monitor.Bounds.Width},{overlay.Monitor.Bounds.Height}) dpi={overlay.Monitor.Dpi} primary={overlay.Monitor.IsPrimary}");
        }

        // Focus the overlay under the cursor so Esc works immediately.
        OverlaySetup.FocusOverlayUnderCursor(_overlays, monitors);

        if (autoTest)
            ScheduleAutoTest(monitors, explicitRegion);
    }

    /// <summary>
    /// Brings the already-open overlays back to the foreground (no second capture)
    /// so a re-trigger while a snip is active is not a silent no-op.
    /// </summary>
    private void ResurfaceOverlays()
    {
        if (_overlays.Count == 0) return;
        foreach (var o in _overlays)
        {
            try { o.Activate(); } catch { /* ignore */ }
        }
        OverlaySetup.FocusOverlayUnderCursor(_overlays, MonitorInfo.EnumerateAll());
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
        // If the server is still starting up, say so rather than showing a plain
        // "Translating…" that would otherwise sit unexplained for a while.
        overlay.ShowTranslatingIndicator(_readiness.IsReady ? "Translating…" : "Starting translation engine…");
        _ = RunPipelineAsync(overlay, selection);
    }

    /// <summary>
    /// Crops the selected region off the UI thread, waits for llama-server to be
    /// ready if it isn't yet, then PNG-encodes the crop in memory and streams it
    /// to llama-server, appending fragments to a translation chip as they arrive
    /// (docs/window-live-design.md "Desktop mode" + "Error handling"). The chip
    /// stays up until the user dismisses it (Esc / click-outside / close button);
    /// every other outcome (empty result, translation-engine failure, request
    /// timeout/HTTP failure, capture failure) ends the snip by itself.
    /// </summary>
    private async Task RunPipelineAsync(OverlayWindow overlay, PixelRect selection)
    {
        var frozen = _frozen!;
        Interlocked.Increment(ref _inFlightPipelines);
        var cts = new CancellationTokenSource();
        _pipelineCts = cts;
        CancellationToken ct = cts.Token;
        try
        {
            CapturedRegion region;
            try
            {
                region = await Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    CapturedRegion cropped = frozen.Crop(selection);
                    sw.Stop();
                    Log($"[pipeline] Crop: {cropped.PixelWidth}x{cropped.PixelHeight} px in {sw.ElapsedMilliseconds} ms");
                    return cropped;
                }, ct).ConfigureAwait(true); // resume on UI thread (Dispatcher)
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"[pipeline] ERROR (capture): {ex}");
                overlay.HideTranslatingIndicator();
                _notify("Capture failed", ex.Message);
                CloseAll();
                return;
            }

            Log($"[pipeline] Region ready for translation dispatch: {region.PixelWidth}x{region.PixelHeight} px at " +
                $"({region.Bounds.X},{region.Bounds.Y}).");

            // Design doc "Error handling": no server → dialog, no crash. Waits
            // no-op immediately if the server is already ready (or already failed).
            await _readiness.WaitUntilSettledAsync(ct).ConfigureAwait(true);
            if (_readiness.IsFailed)
            {
                Log($"[pipeline] Translation engine unavailable: {_readiness.FailureMessage}");
                overlay.HideTranslatingIndicator();
                CloseAll();
                MessageBox.Show(
                    "WindowLive's translation engine failed to start, so this snip can't be translated." +
                    (string.IsNullOrWhiteSpace(_readiness.FailureMessage) ? "" : $"\n\n{_readiness.FailureMessage}"),
                    "WindowLive — translation engine unavailable",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            overlay.SetTranslatingIndicatorText("Translating…");

            byte[] png = ImageUpscaler.EncodeUpscaledPng(region);

            var sb = new StringBuilder();
            bool chipStarted = false;
            try
            {
                await foreach (string fragment in _llm.StreamImageTranslationAsync(png, ct).ConfigureAwait(true))
                {
                    if (!chipStarted)
                    {
                        // First fragment arrived — swap the "Translating…" indicator
                        // for the growing result chip (design doc "Streaming").
                        overlay.HideTranslatingIndicator();
                        overlay.ShowTranslationChip(selection);
                        chipStarted = true;
                    }
                    sb.Append(fragment);
                    overlay.AppendTranslationChipText(fragment);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // handled by the outer catch below (overlay dismissed mid-stream)
            }
            catch (Exception ex)
            {
                // Design doc "Error handling": timeout/HTTP failure → log, dismiss, no crash.
                Log($"[pipeline] Translation request failed: {ex.Message}");
                overlay.HideTranslatingIndicator();
                if (chipStarted) overlay.HideTranslationChip();
                CloseAll();
                return;
            }

            overlay.HideTranslatingIndicator();

            if (!chipStarted || sb.ToString().Trim().Length == 0)
            {
                // Design doc "Error handling": empty/untranslatable input → nothing shown.
                Log("[pipeline] Empty/untranslatable result — dismissing overlay.");
                if (chipStarted) overlay.HideTranslationChip();
                CloseAll();
                return;
            }

            Log($"[pipeline] Translation displayed ({sb.Length} chars).");
            if (SaveShotPath is not null)
            {
                try
                {
                    overlay.SaveToPng(SaveShotPath);
                    Log($"[save-shot] Wrote overlay PNG: {SaveShotPath}");
                }
                catch (Exception ex) { Log($"[save-shot] Failed: {ex.Message}"); }
            }
            // Overlay stays open from here — dismissed via Esc / click-outside / chip close button.
        }
        catch (OperationCanceledException)
        {
            Log("[pipeline] cancelled (overlay dismissed).");
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightPipelines);
            if (ReferenceEquals(_pipelineCts, cts)) _pipelineCts = null;
            cts.Dispose();
        }
    }

    public void CloseAll()
    {
        _autoCloseTimer?.Stop();
        _pipelineCts?.Cancel(); // abort any in-flight translation request
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
        try { Console.WriteLine(line); } catch { /* no console attached */ }
        Logging.AppLog.Write(line);
    }
}
