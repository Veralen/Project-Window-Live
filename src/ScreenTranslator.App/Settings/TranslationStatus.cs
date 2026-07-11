namespace ScreenTranslator.App.Settings;

/// <summary>
/// Point-in-time snapshot of the translation/OCR runtime state, produced by the
/// app shell for the Settings debug panel. Providers are the canonical strings
/// from OnnxSessionFactory ("cpu"/"directml"); <see cref="ActiveProvider"/> is
/// "n/a" when no ONNX engine is loaded (echo passthrough).
/// </summary>
/// <param name="EngineLabel">Human-readable name of the loaded engine/model.</param>
/// <param name="RequestedProvider">Execution provider from config ("cpu"/"directml").</param>
/// <param name="ActiveProvider">Provider actually in use after init (may differ after a GPU→CPU fallback).</param>
/// <param name="IsReady">True once the model finished loading.</param>
/// <param name="IsEcho">True when no model was found and the echo passthrough is active.</param>
/// <param name="ModelDirectory">Resolved model directory of the active engine, null for echo.</param>
/// <param name="OcrConfiguredLanguage">BCP-47 tag from config.</param>
/// <param name="OcrMatchedLanguage">Installed OCR pack matching the configured tag, or null when missing.</param>
/// <param name="OcrFallbackLanguage">User-profile OCR engine tag used when no pack matches, or null.</param>
/// <param name="InitError">Message of a failed engine InitializeAsync, null while loading or on success.</param>
internal sealed record TranslationStatus(
    string EngineLabel,
    string RequestedProvider,
    string ActiveProvider,
    bool IsReady,
    bool IsEcho,
    string? ModelDirectory,
    string OcrConfiguredLanguage,
    string? OcrMatchedLanguage,
    string? OcrFallbackLanguage,
    string? InitError = null);
