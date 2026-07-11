using System.IO;
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
    /// Returns the local ONNX translator when its model files are present,
    /// otherwise an <see cref="EchoTranslator"/> so the pipeline stays usable
    /// (labels rendered with a "[no model]" marker). Run scripts/download-model.ps1
    /// to install the model.
    /// </summary>
    public ITranslator CreateTranslator()
    {
        string modelDir = _config.ResolveModelDirectory();
        if (ModelPresent(modelDir))
            return new LocalOnnxTranslator(modelDir);
        return new EchoTranslator();
    }

    private static bool ModelPresent(string modelDir) =>
        File.Exists(Path.Combine(modelDir, "encoder_model.onnx")) ||
        File.Exists(Path.Combine(modelDir, "encoder_model_quantized.onnx"));
}
