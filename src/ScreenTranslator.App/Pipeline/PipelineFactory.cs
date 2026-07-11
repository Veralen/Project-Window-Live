using System.IO;
using ScreenTranslator.App.Logging;
using ScreenTranslator.Core.Blocks;
using ScreenTranslator.Core.Config;
using ScreenTranslator.Core.Placement;
using ScreenTranslator.Core.Translation;
using ScreenTranslator.Translation;

namespace ScreenTranslator.App.Pipeline;

/// <summary>
/// Single composition point for the grouping / placement / translation
/// implementations used by the snip pipeline.
/// </summary>
internal sealed class PipelineFactory
{
    private readonly AppConfig _config;

    public PipelineFactory(AppConfig config) => _config = config;

    public ILabelPlacer CreatePlacer() => new AspectRatioLabelPlacer();

    public ITextBlockGrouper CreateGrouper() => new ProximityTextBlockGrouper();

    /// <summary>
    /// Returns the configured translator, degrading along nllb → opus → echo so the
    /// pipeline always stays usable (echo renders labels with a "[no model]" marker).
    /// Honors <see cref="AppConfig.Engine"/> ("opus" default / "nllb" multilingual —
    /// its direction comes from <see cref="AppConfig.SourceLanguage"/>/<see
    /// cref="AppConfig.TargetLanguage"/>, FLORES-200 codes) and passes the
    /// execution-provider settings (<see cref="AppConfig.ExecutionProvider"/>,
    /// <see cref="AppConfig.GpuDeviceId"/>) through to the engine. Defaults are opus +
    /// cpu, exactly as before. Run scripts/download-model.ps1 /
    /// download-model-nllb.ps1 to install the models.
    /// </summary>
    public ITranslator CreateTranslator()
    {
        string engine = _config.ResolveEngine(AppLog.Write);
        string provider = _config.ResolveExecutionProvider(AppLog.Write);
        int deviceId = _config.GpuDeviceId;

        if (engine == "nllb")
        {
            string nllbDir = _config.ResolveNllbModelDirectory();
            if (NllbModelPresent(nllbDir))
                return new NllbOnnxTranslator(nllbDir, executionProvider: provider,
                    gpuDeviceId: deviceId, log: AppLog.Write,
                    sourceLanguage: _config.SourceLanguage, targetLanguage: _config.TargetLanguage);
            // The small model is the designed fallback when the big one is absent.
            // Note it is a fixed zh→en pair — correct for the default config; with a
            // non-Chinese SourceLanguage it degrades to visibly-wrong output, which
            // still beats a dead pipeline (and the log/Settings panel say why).
            AppLog.Write($"[pipeline] NLLB model not found in '{nllbDir}'; falling back to opus-mt (zh→en).");
        }

        string modelDir = _config.ResolveModelDirectory();
        if (OpusModelPresent(modelDir))
            return new LocalOnnxTranslator(modelDir, executionProvider: provider,
                gpuDeviceId: deviceId, log: AppLog.Write);
        AppLog.Write($"[pipeline] opus-mt model not found in '{modelDir}'; using EchoTranslator.");
        return new EchoTranslator();
    }

    // Internal so the Settings debug panel can warn before the user commits to an
    // engine whose model files aren't installed.
    internal static bool OpusModelPresent(string modelDir) =>
        File.Exists(Path.Combine(modelDir, "encoder_model.onnx")) ||
        File.Exists(Path.Combine(modelDir, "encoder_model_quantized.onnx"));

    internal static bool NllbModelPresent(string modelDir) =>
        File.Exists(Path.Combine(modelDir, "encoder_model.onnx")) ||
        File.Exists(Path.Combine(modelDir, "encoder_model_quantized.onnx"));
}
