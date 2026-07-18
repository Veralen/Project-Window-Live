using WindowLive.Core.Language;
using WindowLive.Core.Llm;
using Xunit;

namespace WindowLive.Core.Tests;

/// <summary>
/// Coverage for <see cref="LanguageCatalog"/> — the single mapping table behind
/// the settings dropdowns, the detector's restricted set, the popup badge, and
/// tessdata downloads.
/// </summary>
public class LanguageCatalogTests
{
    [Fact]
    public void AllEntries_HaveEveryMappingFilled()
    {
        foreach (LanguageInfo entry in LanguageCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Code));
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(entry.LinguaName));
            Assert.False(string.IsNullOrWhiteSpace(entry.TessdataName));
        }
    }

    [Fact]
    public void Codes_AreUnique()
    {
        var codes = LanguageCatalog.All.Select(l => l.Code.ToLowerInvariant()).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void AutoIsNotACatalogEntry()
    {
        Assert.Null(LanguageCatalog.ByCode(LanguagePair.Auto));
    }

    [Theory]
    [InlineData("ja", "Japanese")]
    [InlineData("JA", "Japanese")] // case-insensitive
    [InlineData("zh-Hans", "Chinese (Simplified)")]
    public void ByCode_FindsEntries(string code, string expectedDisplayName)
    {
        Assert.Equal(expectedDisplayName, LanguageCatalog.ByCode(code)?.DisplayName);
    }

    [Fact]
    public void ByLinguaName_Chinese_ResolvesToSimplified()
    {
        // Lingua has a single Chinese language; detection maps to zh-Hans
        // (first catalog match) while zh-Hant stays manually selectable.
        Assert.Equal("zh-Hans", LanguageCatalog.ByLinguaName("Chinese")?.Code);
    }

    [Theory]
    [InlineData("ja", "en", "JA→EN")]
    [InlineData("zh-Hans", "en", "ZH→EN")]
    [InlineData("auto", "en", "?→EN")]
    public void BadgeFor_FormatsAsDesignPackBadge(string source, string target, string expected)
    {
        Assert.Equal(expected, LanguageCatalog.BadgeFor(source, target));
    }

    [Fact]
    public void DisplayNameFor_AutoRendersNeutralPhrase()
    {
        Assert.Equal("the source language", LanguageCatalog.DisplayNameFor("auto"));
        Assert.Equal("Japanese", LanguageCatalog.DisplayNameFor("ja"));
    }
}
