namespace WindowLive.Core.Language;

/// <summary>
/// One supported language: the app-wide code (what AppConfig stores and the
/// popup badge abbreviates), its human-readable name (settings dropdowns and
/// {source}/{target} prompt placeholders), the Lingua detector enum name
/// (resolved reflectively by the detector so Core's catalog stays data-only),
/// and the tessdata_fast traineddata file stem for Tesseract OCR.
/// </summary>
public sealed record LanguageInfo(string Code, string DisplayName, string LinguaName, string TessdataName)
{
    /// <summary>Short uppercase badge form, e.g. "ja" → "JA", "zh-Hans" → "ZH".</summary>
    public string BadgeLabel => Code.Split('-')[0].ToUpperInvariant();
}

/// <summary>
/// The single mapping table behind the language dropdowns, the language
/// detector's restricted set, the popup's "JA→EN" badge, and tessdata
/// downloads. "auto" (auto-detect) is deliberately NOT an entry — settings
/// prepends it to the source dropdown, and <see cref="Llm.LanguagePair.Auto"/>
/// marks it in config/calls.
/// </summary>
public static class LanguageCatalog
{
    /// <summary>
    /// Both Chinese entries map to Lingua's single "Chinese" language; detection
    /// results for Chinese resolve to zh-Hans (the first catalog match), while
    /// zh-Hant remains selectable manually (Tesseract chi_tra).
    /// </summary>
    public static readonly IReadOnlyList<LanguageInfo> All =
    [
        new("en", "English", "English", "eng"),
        new("es", "Spanish", "Spanish", "spa"),
        new("fr", "French", "French", "fra"),
        new("de", "German", "German", "deu"),
        new("pt", "Portuguese", "Portuguese", "por"),
        new("it", "Italian", "Italian", "ita"),
        new("ru", "Russian", "Russian", "rus"),
        new("ja", "Japanese", "Japanese", "jpn"),
        new("ko", "Korean", "Korean", "kor"),
        new("zh-Hans", "Chinese (Simplified)", "Chinese", "chi_sim"),
        new("zh-Hant", "Chinese (Traditional)", "Chinese", "chi_tra"),
        new("ar", "Arabic", "Arabic", "ara"),
        new("tr", "Turkish", "Turkish", "tur"),
        new("pl", "Polish", "Polish", "pol"),
        new("nl", "Dutch", "Dutch", "nld"),
    ];

    /// <summary>Finds an entry by app code (ordinal-insensitive), or null.</summary>
    public static LanguageInfo? ByCode(string code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the first entry for a Lingua enum name (detection result), or null.</summary>
    public static LanguageInfo? ByLinguaName(string linguaName) =>
        All.FirstOrDefault(l => string.Equals(l.LinguaName, linguaName, StringComparison.Ordinal));

    /// <summary>
    /// Display name for a code, for prompt {source}/{target} substitution.
    /// "auto" (or an unknown code) renders as a neutral phrase so a template
    /// like "from {source}" still reads sensibly when detection hasn't landed.
    /// </summary>
    public static string DisplayNameFor(string code) =>
        string.Equals(code, Llm.LanguagePair.Auto, StringComparison.OrdinalIgnoreCase)
            ? "the source language"
            : ByCode(code)?.DisplayName ?? code;

    /// <summary>Badge text for the popup footer, e.g. ("ja","en") → "JA→EN"; auto/unknown source → "?→EN".</summary>
    public static string BadgeFor(string sourceCode, string targetCode)
    {
        string src = string.Equals(sourceCode, Llm.LanguagePair.Auto, StringComparison.OrdinalIgnoreCase)
            ? "?"
            : ByCode(sourceCode)?.BadgeLabel ?? sourceCode.ToUpperInvariant();
        string tgt = ByCode(targetCode)?.BadgeLabel ?? targetCode.ToUpperInvariant();
        return $"{src}→{tgt}";
    }
}
