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
