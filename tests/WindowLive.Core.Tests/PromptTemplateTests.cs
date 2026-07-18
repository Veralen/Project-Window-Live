using WindowLive.Core.Llm;
using Xunit;

namespace WindowLive.Core.Tests;

/// <summary>
/// Coverage for <see cref="PromptTemplate"/>. The byte-equality test is the
/// regression lock for the provider abstraction: the Local provider's default
/// template MUST render exactly what <see cref="TranslationPrompt.BuildText"/>
/// produced pre-abstraction (the empirically tested prompt contract).
/// </summary>
public class PromptTemplateTests
{
    [Theory]
    [InlineData("hola amigos")]
    [InlineData("putain de merde ce jeu")]
    [InlineData("你他妈的闭嘴")]
    [InlineData("")]
    [InlineData("line with {source} placeholder text")]
    public void DefaultLocalTemplate_RendersByteIdenticalToTranslationPromptBuildText(string input)
    {
        string rendered = PromptTemplate.Render(PromptTemplate.DefaultLocalTemplate, input, "auto", "en");

        Assert.Equal(TranslationPrompt.BuildText(input), rendered);
    }

    [Fact]
    public void Render_SubstitutesAllThreePlaceholders()
    {
        string result = PromptTemplate.Render(
            "From {source} to {target}: {text}", "hola", "Spanish", "English");

        Assert.Equal("From Spanish to English: hola", result);
    }

    [Fact]
    public void Render_TextContainingPlaceholderTokens_IsNotDoubleSubstituted()
    {
        // {source}/{target} are replaced before {text}, so user text carrying a
        // literal "{source}" must survive untouched.
        string result = PromptTemplate.Render(
            "{source}->{target}: {text}", "say {source} out loud", "Japanese", "English");

        Assert.Equal("Japanese->English: say {source} out loud", result);
    }

    [Fact]
    public void Render_UnknownPlaceholders_PassThrough()
    {
        string result = PromptTemplate.Render("keep {unknown} and {text}", "x", "a", "b");

        Assert.Equal("keep {unknown} and x", result);
    }

    [Fact]
    public void Render_RepeatedPlaceholders_AllReplaced()
    {
        string result = PromptTemplate.Render("{text} / {text} ({target}, {target})", "hi", "src", "en");

        Assert.Equal("hi / hi (en, en)", result);
    }

    [Fact]
    public void DefaultCustomTemplate_ContainsAllThreePlaceholders()
    {
        Assert.Contains(PromptTemplate.TextPlaceholder, PromptTemplate.DefaultCustomTemplate);
        Assert.Contains(PromptTemplate.SourcePlaceholder, PromptTemplate.DefaultCustomTemplate);
        Assert.Contains(PromptTemplate.TargetPlaceholder, PromptTemplate.DefaultCustomTemplate);
    }
}
