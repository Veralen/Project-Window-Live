using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTranslator.Core.Config;

/// <summary>
/// User configuration, persisted as JSON at %APPDATA%\ScreenTranslator\config.json.
/// Missing file or unreadable content silently yields defaults (and the default
/// file is written back so users can discover the settings).
/// </summary>
public sealed class AppConfig
{
    /// <summary>BCP-47 tag used to pick the Windows OCR engine (prefix-matched against installed packs).</summary>
    public string OcrLanguage { get; set; } = "zh-Hans";

    /// <summary>
    /// FLORES-200 source language code (e.g. "zho_Hans", "jpn_Jpan", "kor_Hang",
    /// "fra_Latn"). Drives the NLLB engine's translation direction; the opus engine
    /// is a fixed zh→en pair and ignores it. Keep <see cref="OcrLanguage"/> in sync
    /// (the OCR pack for the same language must be installed).
    /// </summary>
    public string SourceLanguage { get; set; } = "zho_Hans";

    /// <summary>FLORES-200 target language code. Drives the NLLB engine; opus ignores it.</summary>
    public string TargetLanguage { get; set; } = "eng_Latn";

    /// <summary>Global hotkey, "Modifier+Modifier+Key" (modifiers: Ctrl, Alt, Shift, Win).</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+L";

    /// <summary>Directory containing the translation model files. Relative paths resolve against %LOCALAPPDATA%\ScreenTranslator.</summary>
    public string ModelDirectory { get; set; } = "models";

    /// <summary>
    /// Translation engine: <c>"opus"</c> (default; opus-mt-zh-en, small and fast) or
    /// <c>"nllb"</c> (NLLB-200-distilled-600M — multilingual via
    /// <see cref="SourceLanguage"/>/<see cref="TargetLanguage"/>, higher quality, far
    /// heavier). NLLB reads from a sibling <c>models-nllb</c> dir and degrades to
    /// opus, then echo, when model files are missing. Unknown values fall back to
    /// <c>"opus"</c>.
    /// </summary>
    public string Engine { get; set; } = "opus";

    /// <summary>
    /// ONNX Runtime execution provider: <c>"cpu"</c> (default; the shipping behavior)
    /// or <c>"cuda"</c> to run translation on an NVIDIA GPU (needs the CUDA 12 /
    /// cuDNN 9 runtime DLLs beside the exe or on PATH). If CUDA fails to initialize
    /// the engine automatically falls back to CPU. Legacy <c>"directml"</c>/<c>"dml"</c>
    /// values map to cuda (the DirectML EP was removed as broken — see
    /// docs/architecture.md). Unknown values fall back to <c>"cpu"</c>.
    /// </summary>
    public string ExecutionProvider { get; set; } = "cpu";

    /// <summary>CUDA device index (default 0). Ignored when ExecutionProvider is "cpu".</summary>
    public int GpuDeviceId { get; set; } = 0;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenTranslator", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig LoadOrDefault(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
            var config = new AppConfig();
            config.Save(path);
            return config;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// Normalizes <see cref="Engine"/> to a known value ("opus"/"nllb"), logging and
    /// defaulting to "opus" on anything unrecognized. Never throws.
    /// </summary>
    public string ResolveEngine(Action<string>? log = null)
    {
        string e = (Engine ?? string.Empty).Trim().ToLowerInvariant();
        if (e == "opus" || e == "nllb") return e;
        log?.Invoke($"[config] Unknown Engine '{Engine}'; using 'opus'.");
        return "opus";
    }

    /// <summary>
    /// Normalizes <see cref="ExecutionProvider"/> to a known value ("cpu"/"cuda"),
    /// logging and defaulting to "cpu" on anything unrecognized. Legacy
    /// "directml"/"dml"/"gpu" values map to "cuda". Never throws.
    /// </summary>
    public string ResolveExecutionProvider(Action<string>? log = null)
    {
        string p = (ExecutionProvider ?? string.Empty).Trim().ToLowerInvariant();
        if (p == "cpu" || p == "cuda") return p;
        if (p == "gpu") return "cuda";
        if (p == "directml" || p == "dml")
        {
            log?.Invoke("[config] ExecutionProvider 'directml' is no longer supported; using 'cuda'.");
            return "cuda";
        }
        log?.Invoke($"[config] Unknown ExecutionProvider '{ExecutionProvider}'; using 'cpu'.");
        return "cpu";
    }

    /// <summary>
    /// Resolves the NLLB benchmark model directory (sibling <c>models-nllb</c> next to
    /// the opus model dir), mirroring TranslatorCli's layout.
    /// </summary>
    public string ResolveNllbModelDirectory()
    {
        string opusDir = ResolveModelDirectory();
        string? parent = Path.GetDirectoryName(
            opusDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.Combine(parent ?? opusDir, "models-nllb");
    }

    public string ResolveModelDirectory()
    {
        if (Path.IsPathRooted(ModelDirectory))
            return ModelDirectory;

        // A shipped build carries its model in a "models" folder beside the exe;
        // prefer that so the distributed folder is drop-in with no install step.
        string besideExe = Path.Combine(AppContext.BaseDirectory, ModelDirectory);
        if (Directory.Exists(besideExe))
            return besideExe;

        // Dev / downloaded default: %LOCALAPPDATA%\ScreenTranslator\models.
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenTranslator", ModelDirectory);
    }
}
