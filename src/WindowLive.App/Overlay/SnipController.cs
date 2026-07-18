using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
/// The result renders in a <see cref="TranslationPopupWindow"/> (design-pack
/// popup) rather than the old in-canvas overlay chip.
/// </summary>
internal sealed class SnipController
{
    private int _inFlightPipelines;
    private readonly Action<string, string> _notify;
    private readonly TranslationBackend _backend;
    private readonly ServerReadiness _readiness;

    private readonly List<OverlayWindow> _overlays = new();
    private FrozenCapture? _frozen;
    private bool _active;
    private bool _selectionHandled;
    private DispatcherTimer? _autoCloseTimer;
    private CancellationTokenSource? _pipelineCts;

    /// <summary>
    /// The popup for the current/most recent snip. Cleared by <see cref="CloseAll"/>
    /// (which closes it unless <see cref="TranslationPopupWindow.IsPinned"/>, in
    /// which case it is only detached — the window itself survives, closed only
    /// by its own "✕" from then on). A new snip always creates a new instance.
    /// </summary>
    private TranslationPopupWindow? _popup;

    public SnipController(Action<string, string> notify, TranslationBackend backend, ServerReadiness readiness)
    {
        _notify = notify;
        _backend = backend;
        _readiness = readiness;
    }

    public bool IsActive => _active;

    /// <summary>True while any snip pipeline is still running.</summary>
    public bool HasInFlightWork => Volatile.Read(ref _inFlightPipelines) > 0;

    /// <summary>When set, the overlay+popup result is saved to this PNG path after rendering (test/demo).</summary>
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
    /// to llama-server. Once OCR (<see cref="TranslationBackend.RecognizeAsync"/>)
    /// returns non-blank text, a <see cref="TranslationPopupWindow"/> is shown near
    /// the selection with a loading indicator, and streamed translation fragments
    /// (<see cref="TranslationBackend.StreamTranscriptTranslationAsync"/>, via
    /// <see cref="StreamTranslationOrNothing"/>) are appended to it as they arrive
    /// (docs/window-live-design.md "Desktop mode" + "Error handling"). The popup
    /// stays up until the user dismisses it (Esc / click-outside / its own "✕", or
    /// survives as a detached window if pinned); every other outcome (empty
    /// result, translation-engine failure, request timeout/HTTP failure, capture
    /// failure) ends the snip by itself.
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
            TranslationPopupWindow? popup = null;
            string transcript;
            try
            {
                // Two explicit steps (recognize, then translate) instead of the
                // old fused StreamImageTranslationAsync — for the default
                // local+vision configuration this issues the identical two HTTP
                // calls (the fused method was internally exactly this sequence),
                // while letting the recognizer be Tesseract and the translator a
                // remote provider when so configured.
                transcript = await _backend.RecognizeAsync(png, ct).ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    overlay.HideTranslatingIndicator();
                    popup = new TranslationPopupWindow();
                    popup.ShowLoading(transcript, _backend.CurrentBadge, _backend.ModelDisplayName);
                    popup.PlaceNear(selection, overlay.Monitor);
                    WirePopup(popup, transcript);
                    _popup = popup;
                }

                await foreach (string fragment in StreamTranslationOrNothing(transcript, ct).ConfigureAwait(true))
                {
                    sb.Append(fragment);
                    popup?.AppendTranslation(fragment);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // handled by the outer catch below (overlay/popup dismissed mid-stream)
            }
            catch (Exception ex)
            {
                // Log the failure but keep the popup (if shown) open with a visible
                // failure note + Retry — silently vanishing reads as "the app did
                // nothing" to the user. If OCR itself failed before the popup ever
                // appeared, fall back to the old indicator-message behavior.
                Log($"[pipeline] Translation request failed: {ex.Message}");
                if (popup is not null)
                {
                    popup.ShowError("Translation failed — press Retry (details in log).");
                }
                else
                {
                    overlay.HideTranslatingIndicator();
                    overlay.ShowTranslatingIndicator("Translation failed — press Esc (details in log)");
                }
                return;
            }

            overlay.HideTranslatingIndicator(); // no-op if already hidden on the popup path

            if (popup is null || sb.ToString().Trim().Length == 0)
            {
                // Design doc "Error handling": empty/untranslatable input → nothing shown.
                Log("[pipeline] Empty/untranslatable result — dismissing overlay.");
                CloseAll();
                return;
            }

            popup.CompleteTranslation();
            Log($"[pipeline] Translation displayed ({sb.Length} chars).");

            if (SaveShotPath is not null)
            {
                try
                {
                    SaveCompositeShot(overlay, popup, SaveShotPath);
                    Log($"[save-shot] Wrote overlay+popup PNG: {SaveShotPath}");
                }
                catch (Exception ex) { Log($"[save-shot] Failed: {ex.Message}"); }
            }
            // Overlay + popup stay open from here — dismissed via Esc / click-outside /
            // the popup's own "✕" (or the popup survives detached if pinned).
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

    /// <summary>
    /// Wires a freshly-shown popup's "✕" and "Retry" to controller behavior.
    /// "✕" always closes this popup and dismisses the rest of the snip
    /// (<see cref="CloseAll"/>), regardless of pin state — an explicit close
    /// click overrides Pin's "survive Esc/click-outside" protection. "Retry"
    /// re-runs only the translation step against the cached <paramref name="transcript"/>,
    /// reusing the still-live pipeline <see cref="CancellationTokenSource"/> if
    /// one exists, otherwise a fresh one scoped to the retry itself; a simple
    /// closure-captured flag guards against overlapping retries from repeated
    /// clicks.
    /// </summary>
    private void WirePopup(TranslationPopupWindow popup, string transcript)
    {
        popup.CloseRequested += () =>
        {
            if (ReferenceEquals(_popup, popup)) _popup = null;
            try { popup.Close(); } catch { /* ignore */ }
            CloseAll();
        };

        bool retryInFlight = false;
        popup.RetryRequested += () => _ = RetryAsync();

        async Task RetryAsync()
        {
            if (retryInFlight) return;
            retryInFlight = true;
            CancellationTokenSource? ownedCts = null;
            try
            {
                CancellationToken retryCt;
                try
                {
                    if (_pipelineCts is { } shared && !shared.IsCancellationRequested)
                        retryCt = shared.Token;
                    else
                        throw new ObjectDisposedException(string.Empty);
                }
                catch (ObjectDisposedException)
                {
                    ownedCts = new CancellationTokenSource();
                    retryCt = ownedCts.Token;
                }

                popup.ShowLoading(transcript, _backend.CurrentBadge, _backend.ModelDisplayName);
                var retrySb = new StringBuilder();
                try
                {
                    await foreach (string fragment in StreamTranslationOrNothing(transcript, retryCt).ConfigureAwait(true))
                    {
                        retrySb.Append(fragment);
                        popup.AppendTranslation(fragment);
                    }
                }
                catch (OperationCanceledException)
                {
                    return; // popup/overlay dismissed mid-retry
                }
                catch (Exception ex)
                {
                    Log($"[pipeline] Retry translation failed: {ex.Message}");
                    popup.ShowError("Translation failed — press Retry (details in log).");
                    return;
                }

                if (retrySb.ToString().Trim().Length == 0)
                    popup.ShowError("No translation returned.");
                else
                    popup.CompleteTranslation();
            }
            finally
            {
                ownedCts?.Dispose();
                retryInFlight = false;
            }
        }
    }

    /// <summary>
    /// Composites the popup's rendered appearance onto the overlay's rendered
    /// appearance (both in-memory, via <see cref="OverlayWindow.RenderToBitmap"/>/
    /// <see cref="TranslationPopupWindow.RenderToBitmap"/>) and saves the result —
    /// the --save-shot harness requirement that the saved PNG show the translation
    /// text at its placed position, now that the popup is a separate top-level
    /// window rather than part of the overlay's own canvas.
    /// </summary>
    private static void SaveCompositeShot(OverlayWindow overlay, TranslationPopupWindow popup, string path)
    {
        RenderTargetBitmap? overlayBmp = overlay.RenderToBitmap();
        if (overlayBmp is null) return;

        BitmapSource finalBmp = overlayBmp;
        RenderTargetBitmap? popupBmp = popup.RenderToBitmap();
        if (popupBmp is not null)
        {
            double scale = overlay.Monitor.ScaleFactor;
            PixelRect popupBounds = popup.WindowBoundsPhysical;
            double offXDip = (popupBounds.X - overlay.Monitor.Bounds.X) / scale;
            double offYDip = (popupBounds.Y - overlay.Monitor.Bounds.Y) / scale;

            var dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.DrawImage(overlayBmp, new Rect(0, 0, overlayBmp.PixelWidth / scale, overlayBmp.PixelHeight / scale));
                dc.DrawImage(popupBmp, new Rect(offXDip, offYDip, popupBmp.PixelWidth / scale, popupBmp.PixelHeight / scale));
            }

            var composite = new RenderTargetBitmap(overlayBmp.PixelWidth, overlayBmp.PixelHeight,
                overlay.Monitor.Dpi, overlay.Monitor.Dpi, PixelFormats.Pbgra32);
            composite.Render(dv);
            finalBmp = composite;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(finalBmp));
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        using var fs = System.IO.File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>
    /// Mirrors the old fused pipeline's contract: a blank transcript streams
    /// nothing (the caller's "no fragments" branch then dismisses the overlay,
    /// per design doc "Error handling"), otherwise the transcript's translation
    /// is streamed via the active backend.
    /// </summary>
    private async IAsyncEnumerable<string> StreamTranslationOrNothing(
        string transcript, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            yield break;
        await foreach (string fragment in _backend.StreamTranscriptTranslationAsync(transcript, ct).ConfigureAwait(true))
            yield return fragment;
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

        if (_popup is not null)
        {
            var popup = _popup;
            _popup = null;
            if (!popup.IsPinned)
            {
                try { popup.Close(); } catch { /* ignore */ }
            }
            // Pinned: leave it open and untouched — it survives detached from the
            // controller, closed only by its own "✕" from here on.
        }
    }

    private static void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);
        try { Console.WriteLine(line); } catch { /* no console attached */ }
        Logging.AppLog.Write(line);
    }
}
