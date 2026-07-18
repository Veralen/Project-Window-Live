using System.Text.Json;
using System.Text.Json.Serialization;
using WindowLive.Core.Geometry;

namespace WindowLive.Core.Config;

/// <summary>
/// User configuration, persisted as JSON at %APPDATA%\WindowLive\config.json.
/// Missing file or unreadable content silently yields defaults (and the default
/// file is written back so users can discover the settings). See
/// docs/window-live-design.md "Config" for the field reference.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Global hotkey for desktop (one-shot snip) mode, "Modifier+Modifier+Key".</summary>
    public string DesktopHotkey { get; set; } = "Ctrl+Shift+T";

    /// <summary>Global hotkey that (re)defines the game-chat region and starts polling.</summary>
    public string GameModeHotkey { get; set; } = "Ctrl+Shift+G";

    /// <summary>Game mode capture/poll cadence, in milliseconds.</summary>
    public int PollIntervalMs { get; set; } = 300;

    /// <summary>Multiplier on input character count for the dynamic max_tokens calculation.</summary>
    public double MaxTokensRatio { get; set; } = 0.75;

    /// <summary>Floor for the dynamic max_tokens calculation.</summary>
    public int MaxTokensMin { get; set; } = 30;

    /// <summary>Ceiling for the dynamic max_tokens calculation — keeps the model from writing disclaimers.</summary>
    public int MaxTokensMax { get; set; } = 120;

    /// <summary>max_tokens used for image input, where char count isn't known before inference.</summary>
    public int MaxTokensImageFallback { get; set; } = 80;

    /// <summary>Sampling temperature sent on every translation call.</summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>Hugging Face repo id llama-server pulls the GGUF model from.</summary>
    public string ModelRepo { get; set; } = "mradermacher/Huihui-Qwen3.5-0.8B-abliterated-GGUF";

    /// <summary>GGUF file name within <see cref="ModelRepo"/>.</summary>
    public string ModelFile { get; set; } = "Huihui-Qwen3.5-0.8B-abliterated.Q4_K_S.gguf";

    /// <summary>
    /// Vision projector GGUF within <see cref="ModelRepo"/>, passed to llama-server
    /// via --mmproj. Required for image input; without it the server is text-only.
    /// </summary>
    public string MmprojFile { get; set; } = "Huihui-Qwen3.5-0.8B-abliterated.mmproj-Q8_0.gguf";

    /// <summary>Local TCP port llama-server listens on.</summary>
    public int ServerPort { get; set; } = 8420;

    /// <summary>
    /// Translation backend: "local" (embedded llama-server — the default, fully
    /// on-device) or "custom" (user-supplied OpenAI-compatible endpoint;
    /// explicit opt-in to off-device calls).
    /// </summary>
    public string Provider { get; set; } = "local";

    /// <summary>
    /// Custom provider base URL (e.g. "https://api.openai.com" or another
    /// OpenAI-compatible server); "/v1/chat/completions" is appended. Ignored
    /// when <see cref="Provider"/> is "local".
    /// </summary>
    public string CustomEndpointUrl { get; set; } = "";

    /// <summary>
    /// Bearer token for the custom endpoint. Stored in plain text in
    /// config.json for v1 — users should treat this file as sensitive.
    /// </summary>
    public string CustomApiKey { get; set; } = "";

    /// <summary>Model name sent in the custom endpoint's "model" field.</summary>
    public string CustomModelName { get; set; } = "";

    /// <summary>Whole-request timeout for custom endpoint calls (remote latency ≫ local).</summary>
    public int CustomRequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// How captured pixels become text: "vision" (send the image to the
    /// selected provider's multimodal model — the original pipeline, default)
    /// or "tesseract" (local Tesseract OCR, then text-only translation).
    /// </summary>
    public string OcrEngine { get; set; } = "vision";

    /// <summary>Source language code from LanguageCatalog, or "auto" for on-device detection.</summary>
    public string SourceLanguage { get; set; } = "auto";

    /// <summary>Target language code from LanguageCatalog.</summary>
    public string TargetLanguage { get; set; } = "en";

    /// <summary>
    /// User override of the Local provider's translation prompt template
    /// ({text}/{source}/{target} placeholders). Null = use the built-in tested
    /// default (PromptTemplate.DefaultLocalTemplate); "Reset to default" stores
    /// null so shipped default improvements keep propagating.
    /// </summary>
    public string? LocalPromptTemplate { get; set; }

    /// <summary>
    /// User override of the Custom provider's translation prompt template.
    /// Null = use PromptTemplate.DefaultCustomTemplate.
    /// </summary>
    public string? CustomPromptTemplate { get; set; }

    /// <summary>
    /// Saved game-chat capture region — physical pixels, virtual-screen coordinates
    /// (Core's geometry convention). Zero-sized until game mode setup has run once.
    /// </summary>
    public PixelRect GameChatRegion { get; set; } = new PixelRect(0, 0, 0, 0);

    /// <summary>
    /// Saved translation-panel rect — physical pixels, virtual-screen coordinates.
    /// Zero-sized until the user has moved or resized the panel once; while unset
    /// (or no longer on any monitor) the panel auto-places below the chat region.
    /// </summary>
    public PixelRect GamePanelRect { get; set; } = new PixelRect(0, 0, 0, 0);

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowLive", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
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
}
