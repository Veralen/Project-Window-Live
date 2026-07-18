namespace WindowLive.Core.Llm;

/// <summary>
/// User-editable translation prompt templates (Settings → PROMPT). A template
/// is a plain string with literal placeholders <c>{text}</c>, <c>{source}</c>,
/// and <c>{target}</c>. <see cref="Config.AppConfig"/> stores null when the
/// user hasn't edited a template, meaning "use the built-in default" — so
/// shipped default improvements keep propagating, and "Reset to default" is
/// just setting the field back to null.
/// </summary>
public static class PromptTemplate
{
    public const string TextPlaceholder = "{text}";
    public const string SourcePlaceholder = "{source}";
    public const string TargetPlaceholder = "{target}";

    /// <summary>
    /// Default template for the Local provider. MUST render byte-for-byte
    /// identically to <see cref="TranslationPrompt.BuildText"/> (the empirically
    /// tested prompt contract — locked by a unit test). Do not edit without
    /// re-testing against the live model.
    /// </summary>
    public const string DefaultLocalTemplate =
        TranslationPrompt.FewShotBlock + TranslationPrompt.InstructionPrefix + "{text}\nEnglish:";

    /// <summary>
    /// Default template for the Custom endpoint provider — sent as a single
    /// user chat message to an instruction-tuned remote model, which (unlike
    /// the 0.8B local model) handles a plain instruction reliably and needs the
    /// explicit source/target languages.
    /// </summary>
    public const string DefaultCustomTemplate =
        "Translate the following text from {source} to {target}. " +
        "Output only the translation, nothing else.\n\n{text}";

    /// <summary>
    /// Renders a template by literal (ordinal) placeholder replacement.
    /// {source}/{target} are substituted before {text} so user-supplied text
    /// containing a literal "{source}" cannot inject a second substitution.
    /// Unknown placeholders pass through untouched.
    /// </summary>
    public static string Render(string template, string text, string sourceLanguage, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template
            .Replace(SourcePlaceholder, sourceLanguage, StringComparison.Ordinal)
            .Replace(TargetPlaceholder, targetLanguage, StringComparison.Ordinal)
            .Replace(TextPlaceholder, text, StringComparison.Ordinal);
    }
}
