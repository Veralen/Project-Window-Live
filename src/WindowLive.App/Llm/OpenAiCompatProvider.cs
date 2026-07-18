using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowLive.App.Logging;
using WindowLive.Core.Config;
using WindowLive.Core.Language;
using WindowLive.Core.Llm;

namespace WindowLive.App.Llm;

/// <summary>
/// The "Custom" provider (Phase 3, docs/window-live-design.md "Providers"):
/// talks to a user-supplied OpenAI-compatible endpoint entirely over
/// /v1/chat/completions — unlike <see cref="LlamaClient"/>, an arbitrary
/// remote server cannot be assumed to expose llama-server's raw /completion
/// endpoint, so both transcript translation and image transcription go
/// through the chat endpoint here.
///
/// Settings (<see cref="AppConfig.CustomEndpointUrl"/>, CustomApiKey,
/// CustomModelName, CustomRequestTimeoutSeconds, CustomPromptTemplate) are
/// re-read from <see cref="AppConfig"/> on every call rather than cached at
/// construction — the user can edit Settings between calls and the very next
/// translation should pick up the change without the provider being rebuilt.
///
/// WHY ONE STREAMING CALL FOR THE WHOLE TRANSCRIPT (unlike the Local
/// provider's per-line loop in <see cref="LocalLlamaProvider"/>): remote
/// instruction-tuned models handle a multi-line prompt directly and
/// reliably. The Local provider's per-line split exists to keep each call
/// inside the 0.8B model's tested few-shot/stop-sequence contract — that
/// constraint does not apply to a general-purpose remote model, and a
/// per-line round trip would just multiply network latency.
///
/// NOTE ON LOGGING: request/response payloads carry the user's chat text,
/// which is sensitive — only status codes, lengths, and exception messages
/// (plus a bounded snippet of the server's own HTTP error body) are logged
/// here. Translation/transcription content itself is never written to
/// AppLog, matching <see cref="LlamaClient"/>'s discipline.
/// </summary>
internal sealed class OpenAiCompatProvider : ITranslationProvider
{
    /// <summary>
    /// Instruction for the image-transcription call. Deliberately NOT
    /// <see cref="TranslationPrompt.TranscriptionInstruction"/> — that
    /// wording is a quirk tuned for the 0.8B local model and has no bearing
    /// on a general-purpose remote model.
    /// </summary>
    private const string TranscriptionInstruction =
        "Transcribe all text in this image exactly as written, preserving the original language. " +
        "Output one line per on-screen message, and nothing else.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AppConfig _config;

    /// <summary>
    /// Does not take ownership of <paramref name="http"/> — the caller creates
    /// and disposes the shared <see cref="HttpClient"/>.
    /// </summary>
    public OpenAiCompatProvider(HttpClient http, AppConfig config)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>Custom model name if the user set one, else the endpoint's host, for the popup footer.</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_config.CustomModelName))
                return _config.CustomModelName;
            return Uri.TryCreate(_config.CustomEndpointUrl, UriKind.Absolute, out Uri? uri)
                ? uri.Host
                : _config.CustomEndpointUrl;
        }
    }

    public bool SupportsVision => true;

    /// <summary>
    /// Builds the "{scheme}://{host}[:port]/v1" base URL from a user-supplied
    /// endpoint, tolerating both a bare host ("https://api.openai.com") and an
    /// endpoint that already includes "/v1" ("http://127.0.0.1:8421/v1") —
    /// appending a second "/v1" would 404 against the latter. Internal (not
    /// private) so <see cref="BackendHealthCheck"/> hits the exact same base
    /// URL for its "/v1/models" probe as translation calls use.
    /// </summary>
    internal static string BuildV1BaseUrl(string endpointUrl)
    {
        string trimmed = (endpointUrl ?? "").TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/v1";
    }

    /// <summary>
    /// Streams the translation of the whole transcript as a single chat call
    /// (class doc "WHY ONE STREAMING CALL"). Completes without yielding
    /// anything for a blank transcript — a valid outcome, not an error — and
    /// skips the network call entirely in that case.
    /// </summary>
    public async IAsyncEnumerable<string> StreamTranscriptTranslationAsync(
        string transcript, LanguagePair languages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        if (string.IsNullOrWhiteSpace(transcript))
            yield break;

        string template = _config.CustomPromptTemplate ?? PromptTemplate.DefaultCustomTemplate;
        string prompt = PromptTemplate.Render(
            template,
            transcript,
            LanguageCatalog.DisplayNameFor(languages.SourceCode),
            LanguageCatalog.DisplayNameFor(languages.TargetCode));

        var request = new OpenAiChatRequest
        {
            Model = _config.CustomModelName,
            Messages = [new OpenAiChatMessage { Role = "user", Content = prompt }],
            Temperature = _config.Temperature,
            Stream = true,
            MaxTokens = null, // let the model stop naturally
        };

        string url = BuildV1BaseUrl(_config.CustomEndpointUrl) + "/chat/completions";
        TimeSpan requestTimeout = TimeSpan.FromSeconds(_config.CustomRequestTimeoutSeconds);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(requestTimeout);

        using var httpRequest = BuildRequest(HttpMethod.Post, url, JsonContent.Create(request, options: JsonOptions));

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Write($"[OpenAiCompatProvider] request to {url} timed out after {requestTimeout.TotalSeconds:F0}s");
            throw new TimeoutException($"Custom endpoint at {url} did not respond within {requestTimeout.TotalSeconds:F0}s.");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Write($"[OpenAiCompatProvider] connection failure calling {url}: {ex.Message}");
            throw;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string snippet = body.Length > 500 ? body[..500] : body;
                AppLog.Write($"[OpenAiCompatProvider] custom endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}");
                throw new HttpRequestException(
                    $"Custom endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
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
                    AppLog.Write($"[OpenAiCompatProvider] stream from {url} timed out after {requestTimeout.TotalSeconds:F0}s");
                    throw new TimeoutException(
                        $"Custom endpoint at {url} did not finish streaming within {requestTimeout.TotalSeconds:F0}s.");
                }

                if (line is null)
                    yield break; // connection closed without an explicit [DONE]

                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                string payload = line["data:".Length..].Trim();
                if (payload.Length == 0)
                    continue;
                if (payload == "[DONE]")
                    yield break;

                string? fragment = TryExtractDeltaContent(payload);
                if (!string.IsNullOrEmpty(fragment))
                    yield return fragment;
            }
        }
    }

    /// <summary>
    /// Non-streaming chat call with the screenshot as a data-URI image part
    /// plus a text transcription instruction. <paramref name="pngBytes"/> is
    /// base64-encoded and sent inline — never written to disk. Returns "" if
    /// the response has no choices or content.
    /// </summary>
    public async Task<string> TranscribeImageAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);

        string dataUri = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
        var content = new List<OpenAiContentPart>
        {
            new() { Type = "image_url", ImageUrl = new OpenAiImageUrl { Url = dataUri } },
            new() { Type = "text", Text = TranscriptionInstruction },
        };

        var request = new OpenAiChatRequest
        {
            Model = _config.CustomModelName,
            Messages = [new OpenAiChatMessage { Role = "user", Content = content }],
            Temperature = _config.Temperature,
            Stream = false,
            MaxTokens = null,
        };

        string url = BuildV1BaseUrl(_config.CustomEndpointUrl) + "/chat/completions";
        TimeSpan requestTimeout = TimeSpan.FromSeconds(_config.CustomRequestTimeoutSeconds);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(requestTimeout);

        using var httpRequest = BuildRequest(HttpMethod.Post, url, JsonContent.Create(request, options: JsonOptions));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Write($"[OpenAiCompatProvider] request to {url} timed out after {requestTimeout.TotalSeconds:F0}s");
            throw new TimeoutException($"Custom endpoint at {url} did not respond within {requestTimeout.TotalSeconds:F0}s.");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Write($"[OpenAiCompatProvider] connection failure calling {url}: {ex.Message}");
            throw;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string snippet = body.Length > 500 ? body[..500] : body;
                AppLog.Write($"[OpenAiCompatProvider] custom endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}");
                throw new HttpRequestException(
                    $"Custom endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
            }

            string responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                var completion = JsonSerializer.Deserialize<OpenAiChatResponse>(responseBody, JsonOptions);
                return completion?.Choices is { Count: > 0 } choices ? choices[0].Message?.Content ?? "" : "";
            }
            catch (JsonException ex)
            {
                AppLog.Write($"[OpenAiCompatProvider] failed to parse /v1/chat/completions response: {ex.Message}");
                return "";
            }
        }
    }

    /// <summary>
    /// Attaches the Authorization header only when an API key is configured —
    /// a local unauthenticated OpenAI-compatible server needs none.
    /// </summary>
    private HttpRequestMessage BuildRequest(HttpMethod method, string url, HttpContent content)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (!string.IsNullOrEmpty(_config.CustomApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.CustomApiKey);
        return request;
    }

    /// <summary>
    /// Parses one streaming SSE data payload, pulling out
    /// choices[0].delta.content. Isolated in its own (non-iterator) method
    /// for the same reason as LlamaClient.TryExtractCompletionContent: C#
    /// forbids yield return inside a try/catch with a catch clause, and
    /// malformed/partial JSON chunks or role-only/empty deltas should be
    /// skipped rather than aborting the whole stream.
    /// </summary>
    private static string? TryExtractDeltaContent(string jsonPayload)
    {
        try
        {
            var chunk = JsonSerializer.Deserialize<OpenAiChatChunk>(jsonPayload, JsonOptions);
            return chunk?.Choices is { Count: > 0 } choices ? choices[0].Delta?.Content : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
