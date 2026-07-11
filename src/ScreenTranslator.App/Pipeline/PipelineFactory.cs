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
    /// Returns the configured ONNX translator when its model files are present,
    /// otherwise an <see cref="EchoTranslator"/> so the pipeline stays usable
    /// (labels rendered with a "[no model]" marker). Honors
    /// <see cref="AppConfig.Engine"/> ("opus" default / "nllb") and passes the
    /// execution-provider settings (<see cref="AppConfig.ExecutionProvider"/>,
    /// <see cref="AppConfig.GpuDeviceId"/>) through to the engine. Defaults are opus +
    /// cpu, exactly as before. Run scripts/download-model.ps1 to install the model.
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
                    gpuDeviceId: deviceId, log: AppLog.Write);
            AppLog.Write($"[pipeline] NLLB model not found in '{nllbDir}'; using EchoTranslator.");
            return new EchoTranslator();
        }

        string modelDir = _config.ResolveModelDirectory();
        if (OpusModelPresent(modelDir))
            return new LocalOnnxTranslator(modelDir, executionProvider: provider,
                gpuDeviceId: deviceId, log: AppLog.Write);
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
