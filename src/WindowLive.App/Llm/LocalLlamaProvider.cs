using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WindowLive.Core.Config;
using WindowLive.Core.Language;
using WindowLive.Core.Llm;

namespace WindowLive.App.Llm;

/// <summary>
/// The "Local" provider: adapts the untouched <see cref="LlamaClient"/> (and
/// its empirically tested prompt/endpoint contract) to
/// <see cref="ITranslationProvider"/>. With no user prompt override this
/// delegates 1:1 — identical HTTP requests to the pre-abstraction app. When
/// the user HAS edited the local template in Settings, each line goes through
/// <see cref="LlamaClient.StreamRawCompletionAsync"/> with the rendered
/// template instead (same per-line split/stream semantics as
/// <see cref="LlamaClient.StreamTranscriptTranslationAsync"/>).
/// </summary>
internal sealed class LocalLlamaProvider : ITranslationProvider
{
    private readonly LlamaClient _llm;
    private readonly AppConfig _config;

    public LocalLlamaProvider(LlamaClient llm, AppConfig config)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string DisplayName
    {
        get
        {
            string name = _config.ModelFile;
            return name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        }
    }

    public bool SupportsVision => true;

    public Task<string> TranscribeImageAsync(byte[] pngBytes, CancellationToken ct = default) =>
        _llm.TranscribeImageAsync(pngBytes, ct);

    public async IAsyncEnumerable<string> StreamTranscriptTranslationAsync(
        string transcript, LanguagePair languages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        string? template = _config.LocalPromptTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            // Default path — byte-identical requests to the tested contract.
            // languages is deliberately ignored: the tested prompt is
            // English-target-only (CLAUDE.md "Key decisions").
            await foreach (string fragment in _llm.StreamTranscriptTranslationAsync(transcript, ct).ConfigureAwait(false))
                yield return fragment;
            yield break;
        }

        var lines = transcript.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        string source = LanguageCatalog.DisplayNameFor(languages.SourceCode);
        string target = LanguageCatalog.DisplayNameFor(languages.TargetCode);

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                yield return "\n";

            int maxTokens = TranslationPrompt.MaxTokensForText(
                lines[i].Length, _config.MaxTokensRatio, _config.MaxTokensMin, _config.MaxTokensMax);
            string prompt = PromptTemplate.Render(template, lines[i], source, target);

            await foreach (string fragment in _llm.StreamRawCompletionAsync(prompt, maxTokens, ct).ConfigureAwait(false))
                yield return fragment;
        }
    }
}
