using WindowLive.Core.Language;
using Xunit;

namespace WindowLive.Core.Tests;

/// <summary>
/// Deterministic in-process smoke tests for <see cref="TextLanguageDetector"/>.
/// These exercise the real SearchPioneer.Lingua detector (restricted to
/// <see cref="LanguageCatalog"/>'s languages) against short game-chat-shaped
/// phrases, not a mock — the point is to catch a broken wiring (wrong Lingua
/// enum resolution, wrong confidence floor) rather than to validate Lingua's
/// own detection accuracy.
/// </summary>
public class TextLanguageDetectorTests
{
    [Fact]
    public void DetectCode_Spanish_ReturnsEs()
    {
        var detector = new TextLanguageDetector();
        Assert.Equal("es", detector.DetectCode("hola amigos, como estas todos hoy"));
    }

    [Fact]
    public void DetectCode_Japanese_ReturnsJa()
    {
        var detector = new TextLanguageDetector();
        Assert.Equal("ja", detector.DetectCode("こんにちは、元気ですか"));
    }

    [Fact]
    public void DetectCode_French_ReturnsFr()
    {
        var detector = new TextLanguageDetector();
        Assert.Equal("fr", detector.DetectCode("putain de merde ce jeu est vraiment nul aujourd'hui"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DetectCode_EmptyOrWhitespace_ReturnsNull(string? text)
    {
        var detector = new TextLanguageDetector();
        Assert.Null(detector.DetectCode(text));
    }

    [Fact]
    public void DetectCode_Gibberish_DoesNotThrow()
    {
        var detector = new TextLanguageDetector();
        var exception = Record.Exception(() => detector.DetectCode("xkcd 1234!!"));
        Assert.Null(exception);
    }

    [Fact]
    public async Task WarmUpAsync_ThenDetectCode_StillWorks()
    {
        var detector = new TextLanguageDetector();
        await detector.WarmUpAsync();
        Assert.Equal("es", detector.DetectCode("hola amigos, como estas todos hoy"));
    }
}
