using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TesseractOCR;
using TesseractOCR.Enums;
using WindowLive.App.Logging;
using WindowLive.Core.Config;
using WindowLive.Core.Language;
using WindowLive.Core.Llm;
using WindowLive.Core.Ocr;

namespace WindowLive.App.Ocr;

/// <summary>
/// The "Tesseract" OCR engine (see <see cref="AppConfig.OcrEngine"/>): local,
/// language-specific text recognition — the alternative to <see cref="VisionRecognizer"/>'s
/// send-the-image-to-the-LLM approach. Cheaper and (for languages with good
/// tessdata_fast models) more reliable at reading dense chat text than the
/// 0.8B vision model, at the cost of needing a language selected up front.
///
/// V1 AUTO RULE: Tesseract fundamentally needs a trained-data language before
/// it can OCR at all — unlike the vision path, there is no "read this and
/// figure out the language as you go". So when <see cref="AppConfig.SourceLanguage"/>
/// is "auto" (or an otherwise-unrecognized code), this recognizer OCRs as
/// English ("eng") rather than blocking or guessing blind. Automatic source
/// *language* detection (<see cref="TextLanguageDetector"/>) is a text-domain
/// concern that runs on the resulting transcript/translation flow, not a
/// pre-OCR image-domain one — teaching this recognizer to detect language from
/// pixels first is out of scope for v1 and would need either a language-agnostic
/// OCR pass or trying every tessdata language against the image, neither of
/// which is worth the cost here.
///
/// In-memory only per the hard "no disk writes for screenshots" rule: the PNG
/// bytes are loaded straight into a Leptonica <see cref="TesseractOCR.Pix.Image"/>
/// via <see cref="TesseractOCR.Pix.Image.LoadFromMemory(byte[])"/> — no temp file.
/// Tessdata itself is the only new on-disk artifact this recognizer introduces
/// (via <see cref="TessdataStore"/>), which is a data file, not a screenshot.
///
/// Tesseract's native <see cref="Engine"/>/<see cref="TesseractOCR.Page"/> types
/// are not thread-safe (they wrap a single native handle with an internal
/// "one result iterator open at a time" invariant), so every access to the
/// cached engine — including recreating it for a new language — is serialized
/// through <see cref="_engineGate"/>. Snip mode and game mode's poll loop can
/// both call RecognizeAsync around the same time; without this gate they would
/// corrupt each other's native calls.
/// </summary>
internal sealed class TesseractRecognizer : ITextRecognizer, IDisposable
{
    private readonly AppConfig _config;
    private readonly TessdataStore _store;
    private readonly SemaphoreSlim _engineGate = new(1, 1);

    private Engine? _engine;
    private string? _engineLanguage;

    public TesseractRecognizer(AppConfig config, TessdataStore store)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<string> RecognizeAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        if (pngBytes is null || pngBytes.Length == 0)
            return "";

        string tessdataName = ResolveTessdataName(_config.SourceLanguage);

        await _engineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Engine engine = await GetOrCreateEngineAsync(tessdataName, ct).ConfigureAwait(false);
            return await Task.Run(() => RunOcr(engine, pngBytes), ct).ConfigureAwait(false);
        }
        finally
        {
            _engineGate.Release();
        }
    }

    /// <summary>
    /// "auto" or a code with no catalog entry falls back to English — see the
    /// v1 auto rule in this class's doc comment. Otherwise resolves the
    /// catalog's tessdata file stem (e.g. "ja" -> "jpn").
    /// </summary>
    private static string ResolveTessdataName(string sourceLanguage)
    {
        if (string.Equals(sourceLanguage, LanguagePair.Auto, StringComparison.OrdinalIgnoreCase))
            return "eng";

        return LanguageCatalog.ByCode(sourceLanguage)?.TessdataName ?? "eng";
    }

    /// <summary>Must be called while holding <see cref="_engineGate"/>.</summary>
    private async Task<Engine> GetOrCreateEngineAsync(string tessdataName, CancellationToken ct)
    {
        if (_engine is not null && string.Equals(_engineLanguage, tessdataName, StringComparison.OrdinalIgnoreCase))
            return _engine;

        string tessdataDir = await _store.EnsureLanguageAsync(tessdataName, progress: null, ct).ConfigureAwait(false);
        // Engine's dataPath is the directory that directly contains the
        // *.traineddata files — i.e. the tessdata folder itself. (The upstream
        // doc comment's "parent of tessdata" wording is misleading; verified
        // live 2026-07-19: passing the parent makes Tesseract probe
        // "{parent}\eng.traineddata" and fail.)
        _engine?.Dispose();
        _engine = new Engine(tessdataDir, tessdataName, EngineMode.Default);
        _engineLanguage = tessdataName;
        AppLog.Write($"[TesseractRecognizer] engine (re)created for language \"{tessdataName}\"");
        return _engine;
    }

    /// <summary>Runs entirely off the calling thread's caller (invoked via Task.Run) —
    /// Tesseract recognition is CPU-bound native work and must not block the
    /// caller's thread (UI thread for snip mode, poll loop thread for game mode).</summary>
    private static string RunOcr(Engine engine, byte[] pngBytes)
    {
        Stopwatch sw = Stopwatch.StartNew();
        using TesseractOCR.Pix.Image image = TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);
        using TesseractOCR.Page page = engine.Process(image);
        string text = (page.Text ?? "").Trim();
        sw.Stop();

        // Never log the recognized text itself — chat text is user/game content
        // and potentially sensitive (same rule LlamaClient follows for translations).
        AppLog.Write($"[TesseractRecognizer] OCR took {sw.ElapsedMilliseconds}ms, {text.Length} chars recognized");
        return text;
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
        _engineGate.Dispose();
    }
}
