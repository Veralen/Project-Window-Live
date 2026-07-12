using WindowLive.Core.Polling;
using Xunit;

namespace WindowLive.Core.Tests;

public class FrameHasherTests
{
    [Fact]
    public void Hash_SameBytes_ProducesSameHash()
    {
        byte[] a = [1, 2, 3, 4, 5, 250, 0, 128];
        byte[] b = [1, 2, 3, 4, 5, 250, 0, 128];

        Assert.Equal(FrameHasher.Hash(a), FrameHasher.Hash(b));
    }

    [Fact]
    public void Hash_SingleByteChanged_ProducesDifferentHash()
    {
        byte[] a = [1, 2, 3, 4, 5];
        byte[] b = [1, 2, 3, 4, 6];

        Assert.NotEqual(FrameHasher.Hash(a), FrameHasher.Hash(b));
    }

    [Fact]
    public void Hash_EmptyInput_IsDefinedAndDeterministic()
    {
        ulong first = FrameHasher.Hash(ReadOnlySpan<byte>.Empty);
        ulong second = FrameHasher.Hash([]);

        Assert.Equal(first, second);
        // The empty-input hash is the FNV offset basis by definition.
        Assert.Equal(14695981039346656037UL, first);
    }

    [Fact]
    public void Hash_DifferentLengths_ProduceDifferentHashes()
    {
        byte[] a = [1, 2, 3];
        byte[] b = [1, 2, 3, 0];

        Assert.NotEqual(FrameHasher.Hash(a), FrameHasher.Hash(b));
    }
}
