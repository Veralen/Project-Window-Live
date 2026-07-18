using System;
using System.Threading;
using System.Threading.Tasks;
using WindowLive.Core.Llm;
using WindowLive.Core.Ocr;

namespace WindowLive.App.Ocr;

/// <summary>
/// The "Vision" OCR engine: image-to-text via the currently selected
/// <see cref="ITranslationProvider"/>'s multimodal transcription — the app's
/// original pipeline. Resolves the provider through a delegate so a settings
/// change (Local ⇄ Custom) is picked up without rebuilding the recognizer.
/// </summary>
internal sealed class VisionRecognizer : ITextRecognizer
{
    private readonly Func<ITranslationProvider> _provider;

    public VisionRecognizer(Func<ITranslationProvider> provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public Task<string> RecognizeAsync(byte[] pngBytes, CancellationToken ct = default) =>
        _provider().TranscribeImageAsync(pngBytes, ct);
}
