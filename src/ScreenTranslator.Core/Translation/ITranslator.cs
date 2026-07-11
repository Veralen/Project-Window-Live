namespace ScreenTranslator.Core.Translation;

/// <summary>
/// A local machine-translation engine. One instance translates one fixed
/// language direction (configured at construction time).
/// </summary>
public interface ITranslator : IDisposable
{
    /// <summary>
    /// Loads the model. Call once before TranslateAsync. Throws with a
    /// user-actionable message if the model files are missing or invalid.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>True once InitializeAsync has completed successfully.</summary>
    bool IsReady { get; }

    /// <summary>Translates one text block (a full paragraph, never a bare line fragment).</summary>
    Task<string> TranslateAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// Fallback translator used when no model is installed: passes text through
/// with a marker so the UI pipeline stays fully exercisable without a model.
/// </summary>
public sealed class EchoTranslator : ITranslator
{
    public bool IsReady { get; private set; }
    public Task InitializeAsync(CancellationToken ct = default) { IsReady = true; return Task.CompletedTask; }
    public Task<string> TranslateAsync(string text, CancellationToken ct = default) => Task.FromResult("[no model] " + text);
    public void Dispose() { }
}
