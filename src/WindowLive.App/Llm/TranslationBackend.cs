using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WindowLive.App.Logging;
using WindowLive.App.Ocr;
using WindowLive.App.Server;
using WindowLive.Core.Config;
using WindowLive.Core.Language;
using WindowLive.Core.Llm;
using WindowLive.Core.Ocr;

namespace WindowLive.App.Llm;

/// <summary>
/// The single facade the snip and game-mode controllers hold: recognize
/// (image → transcript, via Vision or Tesseract) and translate (transcript →
/// streamed text, via the Local llama-server or a Custom OpenAI-compatible
/// endpoint). Stays a stable reference across settings changes —
/// <see cref="Rebuild"/> swaps the provider/recognizer in place when
/// Provider/OcrEngine/languages change, so controllers never re-wire.
///
/// Also owns per-snip language state: when source is "auto", the language of
/// the latest transcript is detected (Phase 2 wires the detector via
/// <see cref="SetLanguageDetector"/>) and used for the {source} prompt
/// placeholder and the popup's "JA→EN" badge.
/// </summary>
internal sealed class TranslationBackend : IDisposable
{
    private readonly object _gate = new();
    private readonly LlamaClient _localClient;
    private readonly HttpClient _http;
    private readonly AppConfig _config;

    private ITranslationProvider _provider;
    private ITextRecognizer _recognizer;
    private Func<string, string?>? _detectLanguage;

    /// <summary>
    /// Created on first use and reused across <see cref="Rebuild"/>s — the
    /// recognizer caches a Tesseract engine per language and re-reads
    /// AppConfig.SourceLanguage at call time, so swapping it on every settings
    /// change would only churn native engines for no benefit.
    /// </summary>
    private TesseractRecognizer? _tesseract;
    private TessdataStore? _tessdataStore;

    /// <summary>Backend readiness gate (local: driven by server startup; custom: marked ready immediately).</summary>
    public ServerReadiness Readiness { get; }

    public TranslationBackend(LlamaClient localClient, HttpClient http, AppConfig config, ServerReadiness readiness)
    {
        _localClient = localClient ?? throw new ArgumentNullException(nameof(localClient));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        (_provider, _recognizer) = Build();
    }

    /// <summary>True when the active provider is the embedded llama-server.</summary>
    public bool IsLocalProvider => !IsCustomConfigured;

    /// <summary>Model name for the popup footer.</summary>
    public string ModelDisplayName => CurrentProvider.DisplayName;

    /// <summary>
    /// Language code detected from the most recent transcript when source is
    /// "auto"; null before any detection (or when detection is unavailable/
    /// inconclusive). Display state for the popup badge — the translate call
    /// itself re-resolves at call time.
    /// </summary>
    public string? LastDetectedLanguageCode { get; private set; }

    private ITranslationProvider CurrentProvider { get { lock (_gate) return _provider; } }
    private ITextRecognizer CurrentRecognizer { get { lock (_gate) return _recognizer; } }

    private bool IsCustomConfigured =>
        string.Equals(_config.Provider, "custom", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Phase 2 hook: installs the on-device text language detector
    /// (transcript → catalog code or null). Callable any time; recognition
    /// works without it (badge shows "?" for auto source).
    /// </summary>
    public void SetLanguageDetector(Func<string, string?> detect) => _detectLanguage = detect;

    /// <summary>
    /// Re-reads config and swaps the active provider/recognizer. Called by App
    /// after any settings change that affects the backend. In-flight requests
    /// finish on the instances they started with.
    /// </summary>
    public void Rebuild()
    {
        var (provider, recognizer) = Build();
        lock (_gate)
        {
            _provider = provider;
            _recognizer = recognizer;
        }
        AppLog.Write($"[Backend] rebuilt: provider={_config.Provider}, ocr={_config.OcrEngine}, " +
                     $"langs={_config.SourceLanguage}->{_config.TargetLanguage}");
    }

    private (ITranslationProvider, ITextRecognizer) Build()
    {
        // Provider: the embedded llama-server ("local", default) or the
        // user-configured OpenAI-compatible endpoint ("custom").
        ITranslationProvider provider = IsCustomConfigured
            ? new OpenAiCompatProvider(_http, _config)
            : new LocalLlamaProvider(_localClient, _config);

        // Recognizer: "vision" (default) is the original image-transcription
        // pipeline via the active provider; "tesseract" OCRs locally and the
        // provider only ever sees text.
        ITextRecognizer recognizer =
            string.Equals(_config.OcrEngine, "tesseract", StringComparison.OrdinalIgnoreCase)
                ? GetOrCreateTesseract()
                : new VisionRecognizer(() => CurrentProvider);

        return (provider, recognizer);
    }

    private TesseractRecognizer GetOrCreateTesseract()
    {
        lock (_gate)
        {
            _tessdataStore ??= new TessdataStore(_http);
            return _tesseract ??= new TesseractRecognizer(_config, _tessdataStore);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tesseract?.Dispose();
            _tesseract = null;
        }
    }

    /// <summary>
    /// Image → transcript via the configured OCR engine, then (source=auto)
    /// detects the transcript's language for the badge / {source} placeholder.
    /// </summary>
    public async Task<string> RecognizeAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        string transcript = await CurrentRecognizer.RecognizeAsync(pngBytes, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(transcript) && IsAutoSource)
        {
            try
            {
                LastDetectedLanguageCode = _detectLanguage?.Invoke(transcript);
            }
            catch (Exception ex)
            {
                // Detection is best-effort display/prompt state — never let it
                // break the translation pipeline.
                AppLog.Write($"[Backend] language detection failed: {ex.Message}");
            }
        }

        return transcript;
    }

    /// <summary>Streams the transcript's translation via the active provider, with the resolved language pair.</summary>
    public IAsyncEnumerable<string> StreamTranscriptTranslationAsync(string transcript, CancellationToken ct = default) =>
        CurrentProvider.StreamTranscriptTranslationAsync(transcript, ResolveLanguages(), ct);

    /// <summary>Popup badge text, e.g. "JA→EN" ("?→EN" while auto source is undetected).</summary>
    public string CurrentBadge
    {
        get
        {
            LanguagePair langs = ResolveLanguages();
            return LanguageCatalog.BadgeFor(langs.SourceCode, langs.TargetCode);
        }
    }

    private bool IsAutoSource =>
        string.Equals(_config.SourceLanguage, LanguagePair.Auto, StringComparison.OrdinalIgnoreCase);

    private LanguagePair ResolveLanguages()
    {
        string source = _config.SourceLanguage;
        if (IsAutoSource && LastDetectedLanguageCode is not null)
            source = LastDetectedLanguageCode;
        return new LanguagePair(source, _config.TargetLanguage);
    }
}
