using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using WindowLive.App.Logging;
using WindowLive.Core.Config;

namespace WindowLive.App.Llm;

/// <summary>
/// Settings-page "Test connection" check for both providers. Never throws —
/// every failure path (unreachable, timeout, malformed URL, non-2xx status)
/// resolves to a <c>(false, message)</c> result rather than an exception, so
/// callers can wire this straight to a UI status line without a try/catch of
/// their own.
/// </summary>
internal static class BackendHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Local provider: GETs the embedded llama-server's own /health endpoint
    /// on <see cref="AppConfig.ServerPort"/>. Custom provider: GETs
    /// "{base}/v1/models" (same base-URL normalization
    /// <see cref="OpenAiCompatProvider.BuildV1BaseUrl"/> uses for translation
    /// calls) with the configured bearer token attached, if any.
    /// </summary>
    public static async Task<(bool Ok, string Message)> CheckAsync(
        HttpClient http, AppConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(config);

        bool isCustom = string.Equals(config.Provider, "custom", StringComparison.OrdinalIgnoreCase);
        return isCustom
            ? await CheckCustomAsync(http, config, ct).ConfigureAwait(false)
            : await CheckLocalAsync(http, config, ct).ConfigureAwait(false);
    }

    private static async Task<(bool, string)> CheckLocalAsync(HttpClient http, AppConfig config, CancellationToken ct)
    {
        string url = $"http://127.0.0.1:{config.ServerPort}/health";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        try
        {
            using var response = await http.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, $"connected — localhost:{config.ServerPort}")
                : (false, $"server returned {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "no response within 5s");
        }
        catch (Exception ex)
        {
            // Broad catch is deliberate: this check must never throw (class doc).
            AppLog.Write($"[BackendHealthCheck] local health check failed: {ex.Message}");
            return (false, "not running");
        }
    }

    private static async Task<(bool, string)> CheckCustomAsync(HttpClient http, AppConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.CustomEndpointUrl))
            return (false, "no endpoint URL set");

        string host = Uri.TryCreate(config.CustomEndpointUrl, UriKind.Absolute, out Uri? parsed)
            ? parsed.Host
            : config.CustomEndpointUrl;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        try
        {
            string url = OpenAiCompatProvider.BuildV1BaseUrl(config.CustomEndpointUrl) + "/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(config.CustomApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.CustomApiKey);

            using var response = await http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return (true, $"connected — {host}");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, "unauthorized — check API key");
            return (false, $"server returned {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "no response within 5s");
        }
        catch (Exception ex)
        {
            // Broad catch is deliberate: this check must never throw (class doc) —
            // a malformed user-supplied URL can throw types other than
            // HttpRequestException (e.g. UriFormatException) before the request
            // even leaves the process.
            AppLog.Write($"[BackendHealthCheck] custom endpoint check failed: {ex.Message}");
            return (false, "unreachable");
        }
    }
}
