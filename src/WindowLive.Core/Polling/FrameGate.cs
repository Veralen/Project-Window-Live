namespace WindowLive.Core.Polling;

/// <summary>Decision returned by <see cref="FrameGate.Observe"/> for a polled frame.</summary>
public enum FrameAction
{
    /// <summary>Do not translate this cycle.</summary>
    Skip,

    /// <summary>Send this frame's captured region to the model.</summary>
    Send,
}

/// <summary>
/// Game-mode change-detection + debounce state machine (see
/// docs/window-live-design.md, "Live loop" and "Debounce"), operating on
/// noise-tolerant <see cref="FrameSignature"/>s instead of exact hashes — a
/// live game never repeats bit-identically, so exact hashing starved the gate
/// forever (translations only appeared when the static snip overlay happened
/// to cover the region).
///
/// A frame is sent when it (a) differs from the last frame actually sent and
/// (b) has stabilized — is similar to the immediately preceding observation.
/// Two different tolerances are deliberately used for those two checks:
///
///  - CHANGE detection (vs. last sent) is STRICT (any single cell over the
///    tolerance counts): missing a small new message ("ok") would starve the
///    user of a translation, while a false positive merely costs a redundant
///    transcription that the caller's transcript dedup makes invisible.
///  - STABILITY detection (vs. previous observation) is TOLERANT (a small
///    fraction of cells may differ): it only decides how fast the send
///    happens — a noisy-but-stable frame should take the fast path rather
///    than wait for the force-send timeout.
///
/// FORCE-SEND BACKSTOP: with constant motion behind the chat (animated scene,
/// flashing UI) the frame may never stabilize at all. If the frame has
/// differed from the last sent for <see cref="_forceSendAfterTicks"/>
/// consecutive observations without stabilizing, it is sent anyway. This is
/// the actual correctness guarantee; the tolerances above only tune how much
/// redundant work happens in the steady state. Worst-case latency from a real
/// change to its send is forceSendAfterTicks * poll interval (~1.8s at
/// defaults), measured from divergence and assuming the loop keeps cadence.
///
/// The unstable streak resets on every send (stabilized or forced), whenever
/// the frame matches the last sent (nothing pending), and on
/// <see cref="Reset"/>. Not thread-safe; intended for a single polling loop.
/// </summary>
public sealed class FrameGate
{
    /// <summary>Strict per-cell tolerance for "did anything change since the last send".</summary>
    public const int ChangeLevelTolerance = 8;

    /// <summary>Strict fraction for change detection: no cell may exceed the tolerance.</summary>
    public const double ChangeMaxDifferingFraction = 0.0;

    /// <summary>Tolerant per-cell threshold for "has the frame stabilized".</summary>
    public const int StableLevelTolerance = 10;

    /// <summary>Tolerant fraction for stability: up to 2% of cells may exceed the threshold.</summary>
    public const double StableMaxDifferingFraction = 0.02;

    /// <summary>Default force-send backstop: 6 ticks at the 300ms poll = ~1.8s worst case.</summary>
    public const int DefaultForceSendAfterTicks = 6;

    private readonly int _forceSendAfterTicks;

    private FrameSignature? _lastSent;
    private FrameSignature? _prevObserved;
    private int _unstableStreak;

    public FrameGate(int forceSendAfterTicks = DefaultForceSendAfterTicks)
    {
        if (forceSendAfterTicks < 1)
            throw new System.ArgumentOutOfRangeException(nameof(forceSendAfterTicks));
        _forceSendAfterTicks = forceSendAfterTicks;
    }

    /// <summary>
    /// Feeds the next observed frame signature into the state machine and
    /// returns whether it should be sent for translation.
    /// </summary>
    public FrameAction Observe(FrameSignature signature)
    {
        System.ArgumentNullException.ThrowIfNull(signature);

        bool pendingChange = _lastSent is null ||
            !FrameSignature.Similar(signature, _lastSent, ChangeLevelTolerance, ChangeMaxDifferingFraction);

        FrameAction action;
        if (!pendingChange)
        {
            // Visually identical to what we last sent — nothing new to deliver.
            action = FrameAction.Skip;
            _unstableStreak = 0;
        }
        else
        {
            bool stableRepeat = _prevObserved is not null &&
                FrameSignature.Similar(signature, _prevObserved, StableLevelTolerance, StableMaxDifferingFraction);

            if (stableRepeat)
            {
                action = FrameAction.Send;
                _lastSent = signature;
                _unstableStreak = 0;
            }
            else
            {
                _unstableStreak++;
                if (_unstableStreak >= _forceSendAfterTicks)
                {
                    action = FrameAction.Send;
                    _lastSent = signature;
                    _unstableStreak = 0;
                }
                else
                {
                    action = FrameAction.Skip;
                }
            }
        }

        _prevObserved = signature;
        return action;
    }

    /// <summary>
    /// Clears all state (including the force-send streak), e.g. when the user
    /// redefines the game-chat region so stale signatures from the old region
    /// can't suppress the first real send.
    /// </summary>
    public void Reset()
    {
        _lastSent = null;
        _prevObserved = null;
        _unstableStreak = 0;
    }
}
