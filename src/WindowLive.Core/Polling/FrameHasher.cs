namespace WindowLive.Core.Polling;

/// <summary>
/// FNV-1a 64-bit hash over raw pixel bytes, used by <see cref="FrameGate"/> for
/// game-mode change detection (see docs/window-live-design.md, "Live loop" —
/// "hash the bitmap"). Deterministic and allocation-free: no LINQ, no boxing,
/// no intermediate buffers.
/// </summary>
public static class FrameHasher
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Computes the FNV-1a 64-bit hash of <paramref name="pixels"/>. An empty
    /// span is a defined input and returns the FNV offset basis unchanged.
    /// </summary>
    public static ulong Hash(ReadOnlySpan<byte> pixels)
    {
        ulong hash = FnvOffsetBasis;
        for (int i = 0; i < pixels.Length; i++)
        {
            hash ^= pixels[i];
            hash *= FnvPrime;
        }
        return hash;
    }
}
