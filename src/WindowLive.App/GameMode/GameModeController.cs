using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindowLive.App.Capture;
using WindowLive.App.Llm;
using WindowLive.App.Logging;
using WindowLive.App.Overlay;
using WindowLive.App.Server;
using WindowLive.Core.Config;
using WindowLive.Core.Geometry;
using WindowLive.Core.Polling;

namespace WindowLive.App.GameMode;

/// <summary>
/// Owns the whole game-mode lifecycle (docs/window-live-design.md "Game mode"):
/// the hotkey-triggered region setup/redefine flow (reusing
/// <see cref="OverlaySetup"/>/<see cref="OverlayWindow"/> — the same
/// capture-first, drag-to-select machinery as the desktop snip pipeline), the
/// background poll/capture/translate loop, and the persistent
/// <see cref="GamePanelWindow"/> that shows the streamed result. Pause/Resume
/// and Stop are exposed for the tray menu.
///
/// Change detection uses <see cref="FrameSignature"/>/<see cref="FrameGate"/>
/// (noise-tolerant per-cell luminance signatures), not exact byte hashing: a
/// live game never repeats bit-identically, so hashing raw bytes starved the
/// gate forever. On top of that, a transcript-level dedup
/// (<see cref="_lastTranscript"/>) skips re-translating when the model
/// transcribes the same on-screen text from two different (visually
/// noisy-but-unchanged) frames.
///
/// Panel update choice: the design doc says "previous translation replaced
/// when new one completes." This implementation instead clears the panel on
/// the FIRST fragment of a new stream (not when it completes) — simpler, and
/// visually equivalent once streaming is fast (the model streams at 200+
/// tok/sec per the design doc), since the old text is only ever on screen for
/// the brief moment before the first new token arrives.
/// </summary>
internal sealed class GameModeController
{
    private readonly AppConfig _config;
    private readonly TranslationBackend _backend;
    private readonly ServerReadiness _readiness;
    private readonly Action<string, string> _notify;

    private readonly FrameGate _gate = new();
    private GamePanelWindow? _panel;

    // Region-setup state (drag-to-select overlays currently on screen).
    private readonly List<OverlayWindow> _setupOverlays = new();
    private bool _setupHandled;

    // Poll-loop state.
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private CancellationTokenSource? _requestCts;
    private int _inFlight;
    private volatile bool _paused;
    private PixelRect _region;

    /// <summary>
    /// Last normalized transcript actually sent for translation. Used to skip
    /// redundant translation calls when the model transcribes the same text
    /// twice in a row (e.g. across two visually-noisy-but-textually-identical
    /// frames). Reset only in <see cref="StartPolling"/> — NOT on a paused
    /// resume, since pausing doesn't change what is on screen.
    /// </summary>
    private string? _lastTranscript;

    public GameModeController(AppConfig config, TranslationBackend backend, ServerReadiness readiness, Action<string, string> notify)
    {
        _config = config;
        _backend = backend;
        _readiness = readiness;
        _notify = notify;
    }

    /// <summary>True once the poll loop is running (a region has been picked at least once).</summary>
    public bool IsRunning => _loopTask is not null;

    /// <summary>True while polling is running but paused (panel keeps showing the last translation).</summary>
    public bool IsPaused => _paused;

    /// <summary>True while the drag-to-select region overlay is on screen.</summary>
    public bool IsSettingUp => _setupOverlays.Count > 0;

    /// <summary>
    /// Game-mode hotkey handler and "Redefine chat region" tray item handler
    /// (docs/window-live-design.md "Game mode" setup flow): first use starts
    /// region setup; if already running, redefines the region (stop polling,
    /// re-select, resume polling on the new region once selected).
    /// </summary>
    public void OnHotkeyPressed()
    {
        if (IsSettingUp)
        {
            // Re-trigger while the drag-to-select overlay is already up — resurface
            // it rather than starting a second capture (mirrors SnipController).
            foreach (var o in _setupOverlays)
            {
                try { o.Activate(); } catch { /* ignore */ }
            }
            return;
        }

        if (IsRunning)
            StopPolling(); // "redefines the region": stop polling, then re-select below.

        BeginRegionSetup();
    }

    private void BeginRegionSetup()
    {
        _setupHandled = false;

        FrozenCapture frozen = FrozenCapture.CaptureVirtualScreen();
        var monitors = MonitorInfo.EnumerateAll();
        var overlays = OverlaySetup.CreateOverlays(frozen, monitors);
        _setupOverlays.AddRange(overlays);

        foreach (var overlay in overlays)
        {
            overlay.SelectionCompleted += OnRegionSelected;
            overlay.DismissRequested += OnRegionSetupCancelled;
            overlay.ShowOnMonitor();
        }
        OverlaySetup.FocusOverlayUnderCursor(_setupOverlays, monitors);
    }

    private void OnRegionSelected(OverlayWindow overlay, PixelRect selection)
    {
        if (_setupHandled) return;
        _setupHandled = true;
        CloseSetupOverlays();

        _config.GameChatRegion = selection;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            AppLog.Write($"[GameMode] failed to save chat region to config: {ex.Message}");
            _notify("Game mode", "Chat region set, but saving it to config failed — it will reset on restart.");
        }

        var monitors = MonitorInfo.EnumerateAll();
        MonitorInfo monitor = MonitorInfo.ForPoint(monitors, selection.Center);
        StartPolling(selection, monitor);
    }

    private void OnRegionSetupCancelled()
    {
        if (_setupHandled) return;
        _setupHandled = true;
        CloseSetupOverlays();
        // A cancelled (re)selection simply leaves game mode not running (or, for a
        // redefine, stopped) — the user can press the hotkey again when ready.
    }

    private void CloseSetupOverlays()
    {
        foreach (var o in _setupOverlays)
        {
            o.SelectionCompleted -= OnRegionSelected;
            o.DismissRequested -= OnRegionSetupCancelled;
            try { o.Close(); } catch { /* ignore */ }
        }
        _setupOverlays.Clear();
    }

    // ---- Poll loop ----

    private void StartPolling(PixelRect region, MonitorInfo monitor)
    {
        _region = region;
        _gate.Reset();
        _lastTranscript = null;
        _paused = false;

        if (_panel is null)
        {
            var panel = new GamePanelWindow(_config);
            // Safety net: if the window is ever closed through any path (WPF
            // shutdown, an unexpected Close), drop the reference so the next
            // start builds a fresh instance instead of re-showing a dead one.
            panel.Closed += (_, _) => { if (ReferenceEquals(_panel, panel)) _panel = null; };
            _panel = panel;
        }
        _panel.ShowFor(region, monitor);

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => PollLoopAsync(_loopCts.Token));
    }

    private void StopPolling()
    {
        _loopCts?.Cancel();
        _requestCts?.Cancel();
        _loopCts = null;
        _loopTask = null;
        _panel?.HidePanel();
    }

    /// <summary>
    /// Debug/test affordance mirroring App's <c>--snip-rect</c>: starts polling
    /// directly on an explicit virtual-screen physical-px rectangle, skipping
    /// the drag-to-select UI entirely. Transient — unlike
    /// <see cref="OnRegionSelected"/>, this does NOT save to
    /// <see cref="AppConfig.GameChatRegion"/>.
    /// </summary>
    public void StartForDebugRect(PixelRect region)
    {
        if (IsRunning)
            StopPolling();

        var monitors = MonitorInfo.EnumerateAll();
        MonitorInfo monitor = MonitorInfo.ForPoint(monitors, region.Center);
        StartPolling(region, monitor);
    }

    /// <summary>Toggles pause; resuming re-arms <see cref="FrameGate"/> (design doc: "FrameGate.Reset() on region redefinition and resume").</summary>
    public void TogglePause()
    {
        if (!IsRunning) return;
        if (_paused)
        {
            _gate.Reset();
            _paused = false;
        }
        else
        {
            _paused = true;
        }
    }

    /// <summary>Tray "Stop game mode": tears down polling (and any in-progress setup) entirely.</summary>
    public void Stop()
    {
        if (IsSettingUp)
        {
            _setupHandled = true;
            CloseSetupOverlays();
        }
        StopPolling();
    }

    /// <summary>Called on app exit — cancels any in-flight call and disposes the loop/panel.</summary>
    public void Shutdown()
    {
        Stop();
        _panel?.CloseForExit(); // Stop() only hides the reusable panel; app exit closes it for real
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        int intervalMs = Math.Max(50, _config.PollIntervalMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_paused) continue;
                await RunCycleAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop()/redefine cancelled the loop — normal shutdown path.
        }
    }

    /// <summary>
    /// One poll cycle: capture + hash + gate every tick (so the debounce state
    /// machine sees a continuous stream of observations), but only actually
    /// issue a translation call when the gate says Send AND no previous call is
    /// still in flight — per docs/window-live-design.md "Threading": no
    /// queuing, just drop the cycle.
    /// </summary>
    private async Task RunCycleAsync(CancellationToken loopCt)
    {
        CapturedRegion captured;
        try
        {
            captured = FrozenCapture.CaptureRegion(_region);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[GameMode] capture failed: {ex.Message}");
            return;
        }

        if (!_readiness.IsReady)
        {
            // Translation engine still starting (or failed) — skip BEFORE the
            // gate observes, so a Send is never consumed while we can't act on
            // it. (Verified live 2026-07-19: with the old order, the gate's
            // initial Send fired during server startup, was skipped here, and a
            // fully static region then never triggered again — the first
            // translation only appeared after the on-screen text changed.)
            AppLog.Write("[GameMode] translation engine not ready — skipping this cycle.");
            return;
        }

        FrameSignature signature = FrameSignature.Compute(captured.PixelsBgra32, captured.PixelWidth, captured.PixelHeight);
        if (_gate.Observe(signature) != FrameAction.Send)
            return;

        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            // Design doc "Threading": previous call still in flight — drop this
            // cycle rather than queue it.
            AppLog.Write("[GameMode] previous translation still in flight — dropping this cycle.");
            return;
        }

        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
        _requestCts = requestCts;
        try
        {
            byte[] png = ImageUpscaler.EncodeUpscaledPng(captured);
            string transcript = await _backend.RecognizeAsync(png, requestCts.Token).ConfigureAwait(false);

            string normalized = NormalizeTranscript(transcript);
            if (normalized.Length == 0)
                return; // Nothing legible transcribed — leave the panel untouched.

            if (string.Equals(normalized, _lastTranscript, StringComparison.Ordinal))
            {
                // Same on-screen text as last time we translated — skip the
                // redundant call. Length only: never log transcript content.
                AppLog.Write($"[GameMode] transcript unchanged ({normalized.Length} chars) — skipping translation.");
                return;
            }

            bool started = false;
            var sb = new StringBuilder();

            // Raw (non-normalized) transcript: StreamTranscriptTranslationAsync
            // does its own line-split/trim; normalized is only for the equality
            // check above.
            await foreach (string fragment in _backend.StreamTranscriptTranslationAsync(transcript, requestCts.Token).ConfigureAwait(false))
            {
                if (!started)
                {
                    started = true;
                    // Replace-on-first-fragment (see class doc comment for why).
                    await RunOnUiAsync(() => _panel?.BeginNewTranslation()).ConfigureAwait(false);
                }
                sb.Append(fragment);
                string frag = fragment;
                await RunOnUiAsync(() => _panel?.AppendText(frag)).ConfigureAwait(false);
            }

            // Commit only after the full translation streamed. Committing before
            // would let a mid-stream failure mark this text as "done", making a
            // later forced refresh dedup-skip it and never show its translation.
            _lastTranscript = normalized;

            if (started && sb.ToString().Trim().Length == 0)
            {
                // Empty/whitespace-only result: design doc "Error handling" says
                // keep showing the previous translation, but we already cleared
                // it on the first (empty) fragment — nothing more streamed in, so
                // the panel is just left blank. This is the one case the
                // "clear on first fragment" simplification loses fidelity on
                // versus "replace on completion"; accepted as a rare edge case
                // (an empty string that still produced at least one SSE token).
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled (paused/stopped/redefined mid-flight) — not an error.
        }
        catch (Exception ex)
        {
            // Design doc "Error handling": timeout/HTTP failure -> log, skip
            // cycle, keep polling, keep showing whatever the panel already has.
            AppLog.Write($"[GameMode] translation request failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_requestCts, requestCts)) _requestCts = null;
            requestCts.Dispose();
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    /// <summary>Trims each line, drops empty lines, and rejoins with "\n" — used only for the transcript-unchanged equality check, never logged or displayed verbatim.</summary>
    private static string NormalizeTranscript(string transcript) =>
        string.Join('\n', transcript.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    private static Task RunOnUiAsync(Action action)
    {
        var app = System.Windows.Application.Current;
        return app is null ? Task.CompletedTask : app.Dispatcher.InvokeAsync(action).Task;
    }
}
