using WindowLive.Core.Polling;
using Xunit;

namespace WindowLive.Core.Tests;

/// <summary>
/// Walks the FrameGate debounce state machine through a full polling scenario
/// (see docs/window-live-design.md, "Debounce" and FrameGate.cs's own remarks
/// on the dual-tolerance + force-send design): a frame is sent once it (a)
/// differs from the last frame actually sent and (b) has stabilized, OR the
/// force-send backstop fires after enough consecutive unstable, differing
/// observations.
///
/// Signatures are built from small solid-gray buffers: for a gray pixel
/// (R=G=B=v) the BT.601 luma reduces to exactly v (77+150+29 == 256), so
/// choosing luminance values far apart (0, 90, 180, ...) makes every pairwise
/// comparison "different" under both FrameGate thresholds
/// (ChangeLevelTolerance=8, StableLevelTolerance=10), mirroring how the old
/// exact-hash tests used distinct hash literals.
/// </summary>
public class FrameGateTests
{
    private static FrameSignature Sig(byte luminance)
    {
        const int width = 32, height = 32;
        var buf = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            buf[i * 4] = luminance;
            buf[i * 4 + 1] = luminance;
            buf[i * 4 + 2] = luminance;
            buf[i * 4 + 3] = 255;
        }
        return FrameSignature.Compute(buf, width, height);
    }

    [Fact]
    public void Observe_FirstFrameEver_Skips()
    {
        var gate = new FrameGate();

        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0)));
    }

    [Fact]
    public void Observe_StableRepeatAfterFirstFrame_SendsOnce()
    {
        var gate = new FrameGate();

        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0))); // first sighting
        Assert.Equal(FrameAction.Send, gate.Observe(Sig(0))); // stable repeat -> send
    }

    [Fact]
    public void Observe_UnchangedAfterSend_KeepsSkipping()
    {
        var gate = new FrameGate();
        gate.Observe(Sig(0)); // skip
        gate.Observe(Sig(0)); // send

        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0)));
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0)));
    }

    [Fact]
    public void Observe_ScrollBurstOfDifferingSignatures_SkipsUntilStable()
    {
        var gate = new FrameGate();
        gate.Observe(Sig(0)); // skip (first sighting)
        gate.Observe(Sig(0)); // send

        // Chat starts scrolling: every poll sees a different signature.
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(90))); // changed, not yet stable
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(180))); // changed again, still not stable
    }

    [Fact]
    public void Observe_StabilizesAfterBurst_SendsOnceSignatureRepeats()
    {
        var gate = new FrameGate();
        gate.Observe(Sig(0)); // skip
        gate.Observe(Sig(0)); // send A

        gate.Observe(Sig(90)); // skip - changing
        gate.Observe(Sig(180)); // skip - still changing (first sighting of C)

        Assert.Equal(FrameAction.Send, gate.Observe(Sig(180))); // C repeats -> stable -> send
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(180))); // unchanged from newly sent -> skip
    }

    [Fact]
    public void Observe_FullScrollThenStabilize_SendsExactlyOnceForNewFrame()
    {
        var gate = new FrameGate();
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0))); // first sighting
        Assert.Equal(FrameAction.Send, gate.Observe(Sig(0))); // stable -> send A

        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0))); // unchanged from sent -> skip
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(90))); // scroll starts
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(180))); // scroll continues
        Assert.Equal(FrameAction.Send, gate.Observe(Sig(180))); // stable on C, differs from last sent (A) -> send
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(180))); // unchanged from newly sent -> skip
    }

    [Fact]
    public void Observe_ReturnsToLastSentSignature_NeverResendsWithoutNewChange()
    {
        var gate = new FrameGate();
        gate.Observe(Sig(0)); // skip
        gate.Observe(Sig(0)); // send A

        gate.Observe(Sig(90)); // skip - changed away from A
        // Back to A: identical-to-last-sent always skips, even once stable again.
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0)));
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0)));
    }

    [Fact]
    public void Observe_LeavesAndReturnsToDifferentSentSignature_SendsAgainOnceStable()
    {
        var gate = new FrameGate();
        gate.Observe(Sig(0)); // skip
        gate.Observe(Sig(0)); // send A
        gate.Observe(Sig(0)); // skip - unchanged

        gate.Observe(Sig(90)); // skip - changed, first sighting of B
        Assert.Equal(FrameAction.Send, gate.Observe(Sig(90))); // B stable, differs from last sent (A) -> send

        gate.Observe(Sig(0)); // skip - changed away from B, first sighting of A again
        Assert.Equal(FrameAction.Send, gate.Observe(Sig(0))); // A stable, differs from last sent (B) -> send
    }

    [Fact]
    public void Reset_ClearsState_SoNextStableFrameSendsAgain()
    {
        var gate = new FrameGate();
        gate.Observe(Sig(0)); // skip
        gate.Observe(Sig(0)); // send A
        gate.Observe(Sig(0)); // skip - unchanged from sent

        gate.Reset();

        // After reset, even the same signature needs to re-establish stability
        // before it is sent again (region was redefined).
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0)));
        Assert.Equal(FrameAction.Send, gate.Observe(Sig(0)));
    }

    [Fact]
    public void Observe_ForceSendBackstop_FiresAfterConfiguredTicksOfConstantDifferentSignatures()
    {
        var gate = new FrameGate(forceSendAfterTicks: 3);

        // Three mutually-different signatures in a row never stabilize
        // (never equal to the immediately preceding observation), so the
        // force-send backstop -- not the stability path -- must fire.
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0)));   // streak 1
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(90)));  // streak 2
        Assert.Equal(FrameAction.Send, gate.Observe(Sig(180))); // streak 3 -> forced send

        // The forced-send frame becomes the new last-sent.
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(180)));
    }

    [Fact]
    public void Observe_ForceSendStreak_ReaccumulatesAfterAForcedSend()
    {
        var gate = new FrameGate(forceSendAfterTicks: 3);
        gate.Observe(Sig(0));
        gate.Observe(Sig(90));
        gate.Observe(Sig(180)); // 1st forced send, lastSent = 180

        // A fresh run of mutually-different signatures, none stabilizing.
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(200)));  // streak 1
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(10)));   // streak 2
        Assert.Equal(FrameAction.Send, gate.Observe(Sig(250)));  // streak 3 -> 2nd forced send
    }

    [Fact]
    public void Observe_StabilizingBeforeForceSendThreshold_SendsViaNormalPath()
    {
        var gate = new FrameGate(forceSendAfterTicks: 3);

        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0)));  // streak 1
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(90))); // streak 2

        // Repeat the last signature before the streak reaches 3: stabilizes
        // via the normal path, not the force-send backstop.
        Assert.Equal(FrameAction.Send, gate.Observe(Sig(90)));
    }

    [Fact]
    public void Observe_StreakResets_WhenFrameReturnsToLastSent()
    {
        var gate = new FrameGate(forceSendAfterTicks: 3);
        gate.Observe(Sig(0)); // skip, first sighting
        gate.Observe(Sig(0)); // send -> lastSent = 0

        // Build the unstable streak to 2 of 3 with mutually-different signatures.
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(90)));  // streak 1
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(180))); // streak 2

        // Frame returns to what was last sent: always skips and resets the streak.
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0)));

        // A new differing signature must NOT immediately force-send -- the
        // streak restarted at 1, not continuing from the earlier 2.
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(90)));
    }

    [Fact]
    public void Reset_MidStreak_PreventsPrematureForceSend()
    {
        var gate = new FrameGate(forceSendAfterTicks: 3);
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(0)));  // streak 1
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(90))); // streak 2

        gate.Reset();

        // Had the streak survived, this would be tick 3 -> forced send.
        // After Reset() it must be treated as the first tick again.
        Assert.Equal(FrameAction.Skip, gate.Observe(Sig(180)));
    }

    [Fact]
    public void Observe_NullSignature_ThrowsArgumentNullException()
    {
        var gate = new FrameGate();
        Assert.Throws<ArgumentNullException>(() => gate.Observe(null!));
    }

    [Fact]
    public void Constructor_ForceSendAfterTicksLessThanOne_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameGate(forceSendAfterTicks: 0));
    }
}
