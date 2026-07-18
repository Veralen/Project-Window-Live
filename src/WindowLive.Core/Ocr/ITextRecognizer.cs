namespace WindowLive.Core.Ocr;

/// <summary>
/// Turns a captured region (upscaled PNG bytes, in memory — screenshots never
/// touch disk) into a multi-line text transcript. Implementations: Tesseract
/// OCR, or "vision" (the selected <see cref="Llm.ITranslationProvider"/>'s
/// image transcription). The returned transcript is the exact currency the
/// rest of the pipeline consumes — game mode's transcript-level dedup and
/// <see cref="Llm.ITranslationProvider.StreamTranscriptTranslationAsync"/>
/// both operate on it unchanged.
/// </summary>
public interface ITextRecognizer
{
    Task<string> RecognizeAsync(byte[] pngBytes, CancellationToken ct = default);
}
