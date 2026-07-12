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
/// docs/window-live-design.md, "Live loop" and "Debounce"). A polled frame is
/// sent only when its hash (a) differs from the hash of the last frame that
/// was actually sent, and (b) equals the immediately preceding observed hash
/// — i.e. one stable repeat after a change. This means a burst of differing
/// hashes (mid-scroll, animating text) keeps skipping until the picture holds
/// still for two consecutive polls, avoiding translation of partial frames.
/// Not thread-safe; intended for use from a single polling loop.
/// </summary>
public sealed class FrameGate
{
    private bool _hasSent;
    private ulong _lastSentHash;
    private bool _hasPrevObserved;
    private ulong _prevObservedHash;

    /// <summary>
    /// Feeds the next observed frame hash into the state machine and returns
    /// whether it should be sent for translation.
    /// </summary>
    public FrameAction Observe(ulong hash)
    {
        FrameAction action;

        if (_hasSent && hash == _lastSentHash)
        {
            // Identical to what we last sent — nothing new to translate.
            action = FrameAction.Skip;
        }
        else if (_hasPrevObserved && hash == _prevObservedHash)
        {
            // Differs from last sent (or nothing sent yet) and just repeated
            // once — the frame has stabilized after a change.
            action = FrameAction.Send;
            _lastSentHash = hash;
            _hasSent = true;
        }
        else
        {
            // Either the very first observation, or mid-change (this hash
            // differs from the previous poll) — wait for it to settle.
            action = FrameAction.Skip;
        }

        _prevObservedHash = hash;
        _hasPrevObserved = true;
        return action;
    }

    /// <summary>
    /// Clears all state, e.g. when the user redefines the game-chat region so
    /// stale hashes from the old region can't suppress the first real send.
    /// </summary>
    public void Reset()
    {
        _hasSent = false;
        _lastSentHash = 0;
        _hasPrevObserved = false;
        _prevObservedHash = 0;
    }
}
