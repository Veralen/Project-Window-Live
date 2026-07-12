using WindowLive.Core.Polling;
using Xunit;

namespace WindowLive.Core.Tests;

/// <summary>
/// Walks the FrameGate debounce state machine through a full polling scenario
/// (see docs/window-live-design.md, "Debounce"): a frame is sent only once it
/// differs from the last frame actually sent AND has just repeated (one
/// stable poll after a change). Hash values below are arbitrary and stand in
/// for "the bitmap looks like this."
/// </summary>
public class FrameGateTests
{
    private const ulong FrameA = 111;
    private const ulong FrameB = 222;
    private const ulong FrameC = 333;

    [Fact]
    public void Observe_FirstFrameEver_Skips()
    {
        var gate = new FrameGate();

        Assert.Equal(FrameAction.Skip, gate.Observe(FrameA));
    }

    [Fact]
    public void Observe_StableRepeatAfterFirstFrame_SendsOnce()
    {
        var gate = new FrameGate();

        Assert.Equal(FrameAction.Skip, gate.Observe(FrameA)); // first sighting
        Assert.Equal(FrameAction.Send, gate.Observe(FrameA)); // stable repeat -> send
    }

    [Fact]
    public void Observe_UnchangedAfterSend_KeepsSkipping()
    {
        var gate = new FrameGate();
        gate.Observe(FrameA); // skip
        gate.Observe(FrameA); // send

        Assert.Equal(FrameAction.Skip, gate.Observe(FrameA));
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameA));
    }

    [Fact]
    public void Observe_ScrollBurstOfDifferingHashes_SkipsUntilStable()
    {
        var gate = new FrameGate();
        gate.Observe(FrameA); // skip (first sighting)
        gate.Observe(FrameA); // send

        // Chat starts scrolling: every poll sees a different hash.
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameB)); // changed, not yet stable
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameC)); // changed again, still not stable
    }

    [Fact]
    public void Observe_StabilizesAfterBurst_SendsOnceHashRepeats()
    {
        var gate = new FrameGate();
        gate.Observe(FrameA); // skip
        gate.Observe(FrameA); // send A

        gate.Observe(FrameB); // skip - changing
        gate.Observe(FrameC); // skip - still changing (first sighting of C)

        Assert.Equal(FrameAction.Send, gate.Observe(FrameC)); // C repeats -> stable -> send
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameC)); // unchanged from newly sent -> skip
    }

    [Fact]
    public void Observe_FullScrollThenStabilize_SendsExactlyOnceForNewFrame()
    {
        var gate = new FrameGate();
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameA)); // first sighting
        Assert.Equal(FrameAction.Send, gate.Observe(FrameA)); // stable -> send A

        Assert.Equal(FrameAction.Skip, gate.Observe(FrameA)); // unchanged from sent -> skip
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameB)); // scroll starts
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameC)); // scroll continues
        Assert.Equal(FrameAction.Send, gate.Observe(FrameC)); // stable on C, differs from last sent (A) -> send
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameC)); // unchanged from newly sent -> skip
    }

    [Fact]
    public void Observe_ReturnsToLastSentHash_NeverResendsWithoutNewChange()
    {
        var gate = new FrameGate();
        gate.Observe(FrameA); // skip
        gate.Observe(FrameA); // send A

        gate.Observe(FrameB); // skip - changed away from A
        // Back to A: identical-to-last-sent always skips, even once stable again.
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameA));
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameA));
    }

    [Fact]
    public void Observe_LeavesAndReturnsToDifferentSentHash_SendsAgainOnceStable()
    {
        var gate = new FrameGate();
        gate.Observe(FrameA); // skip
        gate.Observe(FrameA); // send A
        gate.Observe(FrameA); // skip - unchanged

        gate.Observe(FrameB); // skip - changed, first sighting of B
        Assert.Equal(FrameAction.Send, gate.Observe(FrameB)); // B stable, differs from last sent (A) -> send

        gate.Observe(FrameA); // skip - changed away from B, first sighting of A again
        Assert.Equal(FrameAction.Send, gate.Observe(FrameA)); // A stable, differs from last sent (B) -> send
    }

    [Fact]
    public void Reset_ClearsState_SoNextStableFrameSendsAgain()
    {
        var gate = new FrameGate();
        gate.Observe(FrameA); // skip
        gate.Observe(FrameA); // send A
        gate.Observe(FrameA); // skip - unchanged from sent

        gate.Reset();

        // After reset, even the same hash needs to re-establish stability
        // before it is sent again (region was redefined).
        Assert.Equal(FrameAction.Skip, gate.Observe(FrameA));
        Assert.Equal(FrameAction.Send, gate.Observe(FrameA));
    }
}
