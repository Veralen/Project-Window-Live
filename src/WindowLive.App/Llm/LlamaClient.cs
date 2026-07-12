using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowLive.App.Logging;
using WindowLive.Core.Config;
using WindowLive.Core.Llm;

namespace WindowLive.App.Llm;

/// <summary>
/// Client for the local llama-server, split across the two endpoints live testing
/// established as the correct ones for each call (docs/window-live-design.md
/// "Translation call contract"): text translation goes over llama-server's own
/// raw POST /completion endpoint (bypassing the chat template entirely), and
/// image input is a two-step transcribe-then-translate pipeline where only the
/// transcription step uses POST /v1/chat/completions. Every call is stateless:
/// no conversation history, fresh context each time. Prompt text/stop-sequences/
/// max-tokens formula all come from <see cref="TranslationPrompt"/> (the binding
/// contract) — this class only builds the HTTP request around them and parses
/// the responses.
///
/// WHY /completion AND NOT /v1/chat/completions FOR TEXT: Qwen3.5 is a thinking
/// model. The /v1/chat/completions endpoint wraps every prompt in a chat
/// template that opens a reasoning block before any content is produced; our
/// "\n" stop sequence then kills the response while it's still inside that
/// reasoning block, yielding empty output. llama-server's /completion endpoint
/// sends the prompt text through verbatim with no chat template, which is
/// exactly what our few-shot completion-style prompt from
/// <see cref="TranslationPrompt.BuildText"/> is built for — verified against the
/// live model at ~250 tok/s with correct stop-sequence behavior.
///
/// WHY THE IMAGE PATH IS TWO STEPS: one-shot image-to-translation does not work
/// at this model size (0.8B) — the model either transcribes instead of
/// translating, or degenerates. What does work: (1) a transcription call over
/// /v1/chat/completions with "chat_template_kwargs": {"enable_thinking": false}
/// to suppress the reasoning block for that one call, producing a faithful
/// transcript of the on-screen text (see
/// <see cref="TranslationPrompt.TranscriptionInstruction"/> for the wording
/// quirk), then (2) each transcribed line is translated independently through
/// the same /completion few-shot path used for text input.
///
/// Endpoint schemas verified against the official llama.cpp server docs
/// (https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md,
/// fetched 2026-07-12):
/// - POST /completion is llama-server's native (non-OpenAI) endpoint. Request
///   fields used here: "prompt" (string), "n_predict" (max tokens to generate),
///   "temperature", "stream", "stop" (array of stop strings). Streaming
///   responses are Server-Sent Events, each "data: {json}" line carrying a
///   "content" field with the newly generated text fragment; the final chunk
///   additionally carries "stop": true.
/// - POST /v1/chat/completions is the OpenAI-compatible endpoint used only for
///   the transcription call. Multimodal input uses an OpenAI-style content
///   array on the user message: {"type":"text","text":"..."} and
///   {"type":"image_url","image_url":{"url":"data:image/png;base64,..."}}; the
///   docs confirm image_url.url accepts a base64 data URI directly.
///   "chat_template_kwargs" is passed through to the model's chat template,
///   which is how "enable_thinking": false is threaded down to a Qwen3.5-style
///   template.
///
/// Not responsible for UI-thread marshalling — callers consume the
/// IAsyncEnumerable on whatever thread/context they choose (design doc
/// "Threading": SSE token appends are marshalled to the UI thread by the caller).
/// </summary>
public sealed class LlamaClient
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AppConfig _config;
    private readonly TimeSpan _requestTimeout;

    /// <summary>
    /// Does not take ownership of <paramref name="http"/> — the caller creates and
    /// disposes the shared <see cref="HttpClient"/>. <paramref name="requestTimeout"/>
    /// bounds the whole request (headers + full stream); HttpClient.Timeout is not
    /// used because it does not compose well with streamed responses.
    /// </summary>
    public LlamaClient(HttpClient http, AppConfig config, TimeSpan? requestTimeout = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <summary>
    /// Streams the English translation of <paramref name="input"/> as it is produced,
    /// via llama-server's raw /completion endpoint (see class doc for why the chat
    /// endpoint doesn't work for this thinking model). No system prompt — a single
    /// completion-style prompt built by <see cref="TranslationPrompt.BuildText"/>.
    /// Completes without yielding anything if the model returns an empty translation;
    /// that is a valid outcome, not an error.
    /// </summary>
    public IAsyncEnumerable<string> StreamTextTranslationAsync(string input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        int maxTokens = TranslationPrompt.MaxTokensForText(
            input.Length, _config.MaxTokensRatio, _config.MaxTokensMin, _config.MaxTokensMax);

        return StreamCompletionAsync(TranslationPrompt.BuildText(input), maxTokens, ct);
    }

    /// <summary>
    /// Step 1 of the image pipeline: POSTs the screenshot crop to
    /// /v1/chat/completions with "chat_template_kwargs": {"enable_thinking": false}
    /// and <see cref="TranslationPrompt.TranscriptionInstruction"/>, non-streaming,
    /// and returns the raw transcript text (untranslated on-screen chat lines —
    /// see the wording note on <see cref="TranslationPrompt.TranscriptionInstruction"/>).
    /// <paramref name="pngBytes"/> is encoded as a base64 data URI and sent inline —
    /// never written to disk (design doc "Image input"). No stop sequences are sent;
    /// output length is bounded by <see cref="AppConfig.MaxTokensImageFallback"/>
    /// since the transcript length can't be estimated before inference.
    /// </summary>
    public async Task<string> TranscribeImageAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);

        string dataUri = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
        var content = new List<ChatContentPart>
        {
            new() { Type = "image_url", ImageUrl = new ImageUrlPart { Url = dataUri } },
            new() { Type = "text", Text = TranslationPrompt.TranscriptionInstruction },
        };

        var request = new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = content }],
            MaxTokens = _config.MaxTokensImageFallback,
            Temperature = _config.Temperature,
            Stream = false,
            Stop = null,
            ChatTemplateKwargs = new Dictionary<string, object> { ["enable_thinking"] = false },
        };

        return await PostChatCompletionAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams the English translation of a screenshot crop via the two-step
    /// pipeline (class doc "WHY THE IMAGE PATH IS TWO STEPS"): transcribe the
    /// image first (<see cref="TranscribeImageAsync"/>), then delegate to
    /// <see cref="StreamTranscriptTranslationAsync"/> for the per-line
    /// translation streaming. Completes without yielding anything if the
    /// transcript is empty — a valid outcome (design doc "Error handling": no
    /// text / blank result shows nothing), not an error.
    /// </summary>
    public async IAsyncEnumerable<string> StreamImageTranslationAsync(
        byte[] pngBytes, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);

        string transcript = await TranscribeImageAsync(pngBytes, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(transcript))
            yield break;

        await foreach (string fragment in StreamTranscriptTranslationAsync(transcript, ct).ConfigureAwait(false))
            yield return fragment;
    }

    /// <summary>
    /// Streams the English translation of an already-transcribed multi-line
    /// transcript (game mode's path once it has its own transcript-change
    /// dedup — see <see cref="WindowLive.App.GameMode.GameModeController"/>):
    /// splits into non-empty trimmed lines and streams each line's translation
    /// through the same /completion path <see cref="StreamTextTranslationAsync"/>
    /// uses, yielding a bare "\n" between lines so callers can tell them apart in
    /// the streamed output. Completes without yielding anything if every line is
    /// blank — a valid outcome, not an error. Extracted verbatim from what used
    /// to be the second half of <see cref="StreamImageTranslationAsync"/> — no
    /// behavior change for that caller (or for
    /// <see cref="WindowLive.App.Overlay.SnipController"/>, which only calls
    /// <see cref="StreamImageTranslationAsync"/>).
    /// </summary>
    public async IAsyncEnumerable<string> StreamTranscriptTranslationAsync(
        string transcript, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var lines = new List<string>();
        foreach (string rawLine in transcript.Split('\n'))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length > 0)
                lines.Add(trimmed);
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                yield return "\n";

            int maxTokens = TranslationPrompt.MaxTokensForText(
                lines[i].Length, _config.MaxTokensRatio, _config.MaxTokensMin, _config.MaxTokensMax);

            await foreach (string fragment in StreamCompletionAsync(TranslationPrompt.BuildText(lines[i]), maxTokens, ct)
                .ConfigureAwait(false))
            {
                yield return fragment;
            }
        }
    }

    /// <summary>Convenience wrapper that aggregates <see cref="StreamTextTranslationAsync"/> into one string.</summary>
    public async Task<string> TranslateTextAsync(string input, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (string fragment in StreamTextTranslationAsync(input, ct).ConfigureAwait(false))
            sb.Append(fragment);
        return sb.ToString();
    }

    /// <summary>Convenience wrapper that aggregates <see cref="StreamImageTranslationAsync"/> into one string.</summary>
    public async Task<string> TranslateImageAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (string fragment in StreamImageTranslationAsync(pngBytes, ct).ConfigureAwait(false))
            sb.Append(fragment);
        return sb.ToString();
    }

    /// <summary>
    /// Posts one prompt to /completion and yields text fragments as SSE chunks
    /// arrive. Shared by the text path and by each per-line translation call in
    /// the image path.
    /// NOTE ON LOGGING: request/response payloads carry the user's chat text, which
    /// is sensitive — only status codes, exception messages, and (for HTTP error
    /// responses) a bounded snippet of the server's own error body are logged here.
    /// Translation content itself is never written to AppLog.
    /// </summary>
    private async IAsyncEnumerable<string> StreamCompletionAsync(
        string prompt, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new CompletionRequest
        {
            Prompt = prompt,
            NPredict = maxTokens,
            Temperature = _config.Temperature,
            Stream = true,
            Stop = TranslationPrompt.StopSequences,
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        string url = $"http://127.0.0.1:{_config.ServerPort}/completion";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Write($"[LlamaClient] request to {url} timed out after {_requestTimeout.TotalSeconds:F0}s");
            throw new TimeoutException($"llama-server at {url} did not respond within {_requestTimeout.TotalSeconds:F0}s.");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Write($"[LlamaClient] connection failure calling {url}: {ex.Message}");
            throw;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string snippet = body.Length > 500 ? body[..500] : body;
                AppLog.Write($"[LlamaClient] llama-server returned {(int)response.StatusCode} {response.ReasonPhrase}");
                throw new HttpRequestException(
                    $"llama-server returned {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (true)
            {
                string? line;
                try
                {
                    // Null return (rather than reader.EndOfStream, which blocks
                    // synchronously) is how ReadLineAsync signals end of stream.
                    line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    AppLog.Write($"[LlamaClient] stream from {url} timed out after {_requestTimeout.TotalSeconds:F0}s");
                    throw new TimeoutException(
                        $"llama-server at {url} did not finish streaming within {_requestTimeout.TotalSeconds:F0}s.");
                }

                if (line is null)
                    yield break; // connection closed without an explicit final chunk

                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                string payload = line["data:".Length..].Trim();
                if (payload.Length == 0)
                    continue;

                (string? fragment, bool isStop) = TryExtractCompletionContent(payload);
                if (!string.IsNullOrEmpty(fragment))
                    yield return fragment;
                if (isStop)
                    yield break;
            }
        }
    }

    /// <summary>
    /// Posts a non-streaming request to /v1/chat/completions and returns the
    /// completed message content. Used only by <see cref="TranscribeImageAsync"/>.
    /// </summary>
    private async Task<string> PostChatCompletionAsync(ChatCompletionRequest request, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        string url = $"http://127.0.0.1:{_config.ServerPort}/v1/chat/completions";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Write($"[LlamaClient] request to {url} timed out after {_requestTimeout.TotalSeconds:F0}s");
            throw new TimeoutException($"llama-server at {url} did not respond within {_requestTimeout.TotalSeconds:F0}s.");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Write($"[LlamaClient] connection failure calling {url}: {ex.Message}");
            throw;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string snippet = body.Length > 500 ? body[..500] : body;
                AppLog.Write($"[LlamaClient] llama-server returned {(int)response.StatusCode} {response.ReasonPhrase}");
                throw new HttpRequestException(
                    $"llama-server returned {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
            }

            string responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions);
                return completion?.Choices is { Count: > 0 } choices ? choices[0].Message?.Content ?? "" : "";
            }
            catch (JsonException ex)
            {
                AppLog.Write($"[LlamaClient] failed to parse /v1/chat/completions response: {ex.Message}");
                return "";
            }
        }
    }

    /// <summary>
    /// Parses one /completion SSE data payload, pulling out "content" and "stop".
    /// Isolated in its own (non-iterator) method because C# forbids yield return
    /// inside a try/catch with a catch clause — malformed or partial JSON chunks
    /// are skipped rather than aborting the whole stream.
    /// </summary>
    private static (string? Content, bool Stop) TryExtractCompletionContent(string jsonPayload)
    {
        try
        {
            var chunk = JsonSerializer.Deserialize<CompletionChunk>(jsonPayload, JsonOptions);
            return (chunk?.Content, chunk?.Stop ?? false);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    private sealed class CompletionRequest
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = "";

        [JsonPropertyName("n_predict")]
        public int NPredict { get; set; }

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("stop")]
        public string[] Stop { get; set; } = [];
    }

    private sealed class CompletionChunk
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("stop")]
        public bool Stop { get; set; }
    }

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "local";

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("stop")]
        public string[]? Stop { get; set; }

        [JsonPropertyName("chat_template_kwargs")]
        public Dictionary<string, object>? ChatTemplateKwargs { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        /// <summary>Either a plain string (text-only) or a List&lt;ChatContentPart&gt; (multimodal).</summary>
        [JsonPropertyName("content")]
        public object Content { get; set; } = "";
    }

    private sealed class ChatContentPart
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("image_url")]
        public ImageUrlPart? ImageUrl { get; set; }
    }

    private sealed class ImageUrlPart
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatCompletionResponseChoice>? Choices { get; set; }
    }

    private sealed class ChatCompletionResponseChoice
    {
        [JsonPropertyName("message")]
        public ChatCompletionResponseMessage? Message { get; set; }
    }

    private sealed class ChatCompletionResponseMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
