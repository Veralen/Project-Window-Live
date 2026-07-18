namespace WindowLive.Core.Llm;

/// <summary>
/// Source/target language pair for a translation call, as ISO-ish codes from
/// <see cref="Language.LanguageCatalog"/>. <see cref="SourceCode"/> may be
/// "auto" when the user selected auto-detect and no detection result is
/// available (yet); providers that need a concrete source name (prompt
/// templates with {source}) should render something neutral in that case.
/// </summary>
public readonly record struct LanguagePair(string SourceCode, string TargetCode)
{
    public const string Auto = "auto";
}

/// <summary>
/// A translation backend: the Local embedded llama-server or a user-supplied
/// OpenAI-compatible Custom endpoint. Mirrors the two-step
/// transcribe-then-translate shape the app is built around (see
/// docs/window-live-design.md "Translation call contract"): callers obtain a
/// transcript (via vision transcription here, or OCR elsewhere) and then stream
/// its translation. Implementations are stateless per call and must not
/// marshal to the UI thread — callers own that.
/// </summary>
public interface ITranslationProvider
{
    /// <summary>Model name shown in the translation popup footer (mono, dim).</summary>
    string DisplayName { get; }

    /// <summary>Whether <see cref="TranscribeImageAsync"/> is expected to work (multimodal model).</summary>
    bool SupportsVision { get; }

    /// <summary>
    /// Streams the translation of an already-obtained multi-line transcript.
    /// Completes without yielding anything when every line is blank — a valid
    /// outcome, not an error (design doc "Error handling").
    /// </summary>
    IAsyncEnumerable<string> StreamTranscriptTranslationAsync(
        string transcript, LanguagePair languages, CancellationToken ct = default);

    /// <summary>
    /// Vision path step 1: transcribes the on-screen text in a PNG (in-memory,
    /// never written to disk) and returns the raw untranslated transcript.
    /// </summary>
    Task<string> TranscribeImageAsync(byte[] pngBytes, CancellationToken ct = default);
}
