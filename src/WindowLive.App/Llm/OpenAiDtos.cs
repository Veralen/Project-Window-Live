using System.Text.Json.Serialization;

namespace WindowLive.App.Llm;

/// <summary>
/// DTOs for the OpenAI-compatible /v1/chat/completions wire format, used by
/// <see cref="OpenAiCompatProvider"/> for the user's Custom endpoint. These
/// deliberately DUPLICATE the shapes private to <see cref="LlamaClient"/>
/// (which uses /v1/chat/completions only for llama-server's own image
/// transcription call) rather than sharing them — the two call sites already
/// differ (no chat_template_kwargs here, and "model" is a user-configured
/// name rather than a fixed "local") and are expected to keep diverging
/// independently as each provider's needs change.
/// </summary>
internal sealed class OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<OpenAiChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    /// <summary>
    /// Omitted from the JSON request when null (see the provider's
    /// JsonSerializerOptions) — used to let the model stop naturally on the
    /// translation streaming path instead of imposing a token ceiling.
    /// </summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }
}

internal sealed class OpenAiChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>Either a plain string (text-only) or a List&lt;OpenAiContentPart&gt; (multimodal).</summary>
    [JsonPropertyName("content")]
    public object Content { get; set; } = "";
}

internal sealed class OpenAiContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    public OpenAiImageUrl? ImageUrl { get; set; }
}

internal sealed class OpenAiImageUrl
{
    /// <summary>A "data:image/png;base64,..." data URI — never a filesystem path (no disk writes).</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

/// <summary>One SSE chunk from a streaming /v1/chat/completions response.</summary>
internal sealed class OpenAiChatChunk
{
    [JsonPropertyName("choices")]
    public List<OpenAiChatChunkChoice>? Choices { get; set; }
}

internal sealed class OpenAiChatChunkChoice
{
    [JsonPropertyName("delta")]
    public OpenAiChatDelta? Delta { get; set; }
}

internal sealed class OpenAiChatDelta
{
    /// <summary>Null/absent on the role-only opening chunk and on some providers' final chunk.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

/// <summary>A non-streaming /v1/chat/completions response (used for image transcription).</summary>
internal sealed class OpenAiChatResponse
{
    [JsonPropertyName("choices")]
    public List<OpenAiChatResponseChoice>? Choices { get; set; }
}

internal sealed class OpenAiChatResponseChoice
{
    [JsonPropertyName("message")]
    public OpenAiChatResponseMessage? Message { get; set; }
}

internal sealed class OpenAiChatResponseMessage
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
