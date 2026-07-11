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

    /// <summary>Source language code understood by the translation model (NLLB/FLORES-style or model-specific).</summary>
    public string SourceLanguage { get; set; } = "zho_Hans";

    /// <summary>Target language code understood by the translation model.</summary>
    public string TargetLanguage { get; set; } = "eng_Latn";

    /// <summary>Global hotkey, "Modifier+Modifier+Key" (modifiers: Ctrl, Alt, Shift, Win).</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+L";

    /// <summary>Directory containing the translation model files. Relative paths resolve against %LOCALAPPDATA%\ScreenTranslator.</summary>
    public string ModelDirectory { get; set; } = "models";

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

    public string ResolveModelDirectory() =>
        Path.IsPathRooted(ModelDirectory)
            ? ModelDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenTranslator", ModelDirectory);
}
