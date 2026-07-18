// FakeOpenAI: a tiny deterministic OpenAI-compatible test double for manually
// exercising WindowLive.App.Llm.OpenAiCompatProvider / BackendHealthCheck
// without a real remote endpoint. Not part of WindowLive.slnx — build/run
// standalone: `dotnet run --project tools/FakeOpenAI -- --mode normal`.
//
// Modes (--mode, default "normal"):
//   normal    - GET /v1/models -> 200; POST /v1/chat/completions streams
//               "Hello ", "from ", "fake." (or returns "FAKE TRANSCRIPT LINE"
//               non-streaming).
//   401       - GET /v1/models -> 401 (BackendHealthCheck "unauthorized" path).
//   stall     - POST /v1/chat/completions accepts the request, then sleeps
//               120s before responding (timeout-handling smoke test).
//   malformed - streaming POST emits one garbage "data:" line before the
//               valid deltas, to exercise malformed-chunk tolerance.

using System.Net;
using System.Text;
using System.Text.Json;

string mode = "normal";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--mode")
        mode = args[i + 1];
}

const string prefix = "http://127.0.0.1:8431/";
using var listener = new HttpListener();
listener.Prefixes.Add(prefix);
listener.Start();
Console.WriteLine($"FakeOpenAI listening on {prefix} (mode={mode})");

while (true)
{
    HttpListenerContext ctx = await listener.GetContextAsync();
    _ = HandleAsync(ctx);
}

async Task HandleAsync(HttpListenerContext ctx)
{
    try
    {
        HttpListenerRequest req = ctx.Request;
        Console.WriteLine($"{req.HttpMethod} {req.Url?.AbsolutePath}");

        if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/v1/models")
        {
            await HandleModelsAsync(ctx);
        }
        else if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/v1/chat/completions")
        {
            await HandleChatCompletionsAsync(ctx);
        }
        else
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"request handling failed: {ex.Message}");
        try { ctx.Response.Close(); } catch { /* best-effort */ }
    }
}

async Task HandleModelsAsync(HttpListenerContext ctx)
{
    if (mode == "401")
    {
        ctx.Response.StatusCode = 401;
        ctx.Response.Close();
        return;
    }

    byte[] body = Encoding.UTF8.GetBytes("""{"object":"list","data":[{"id":"fake-model","object":"model"}]}""");
    ctx.Response.ContentType = "application/json";
    ctx.Response.StatusCode = 200;
    await ctx.Response.OutputStream.WriteAsync(body);
    ctx.Response.Close();
}

async Task HandleChatCompletionsAsync(HttpListenerContext ctx)
{
    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
    string body = await reader.ReadToEndAsync();

    string? authHeader = ctx.Request.Headers["Authorization"];
    string model = "(unknown)";
    bool stream = false;
    try
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("model", out JsonElement modelEl))
            model = modelEl.GetString() ?? model;
        if (doc.RootElement.TryGetProperty("stream", out JsonElement streamEl))
            stream = streamEl.GetBoolean();
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"  request body was not valid JSON: {ex.Message}");
    }
    Console.WriteLine($"  model={model} stream={stream} authorization={authHeader ?? "(none)"}");

    if (mode == "stall")
    {
        Console.WriteLine("  stalling for 120s before responding...");
        await Task.Delay(TimeSpan.FromSeconds(120));
    }

    if (!stream)
    {
        byte[] body200 = Encoding.UTF8.GetBytes(
            """{"choices":[{"message":{"role":"assistant","content":"FAKE TRANSCRIPT LINE"}}]}""");
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = 200;
        await ctx.Response.OutputStream.WriteAsync(body200);
        ctx.Response.Close();
        return;
    }

    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.SendChunked = true;
    Stream output = ctx.Response.OutputStream;

    async Task WriteChunkAsync(string deltaJson)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"data: {deltaJson}\n\n");
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }

    if (mode == "malformed")
        await WriteChunkAsync("{not valid json");

    await WriteChunkAsync("""{"choices":[{"delta":{"role":"assistant"}}]}"""); // role-only opening chunk
    foreach (string word in new[] { "Hello ", "from ", "fake." })
        await WriteChunkAsync("{\"choices\":[{\"delta\":{\"content\":\"" + word + "\"}}]}");

    byte[] doneBytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
    await output.WriteAsync(doneBytes);
    await output.FlushAsync();
    ctx.Response.Close();
}
