using Microsoft.ML.OnnxRuntime;
using ScreenTranslator.Core.Translation;
using Tokenizers.DotNet;

namespace ScreenTranslator.Translation;

/// <summary>
/// Multilingual translator on facebook/NLLB-200-distilled-600M (ONNX, Xenova
/// export) — the app's configurable heavier engine (<c>Engine = "nllb"</c>),
/// covering the ~200 FLORES-200 language codes. The language direction comes from
/// configuration: pass FLORES-200 codes (e.g. <c>zho_Hans</c>, <c>jpn_Jpan</c>,
/// <c>kor_Hang</c>, <c>fra_Latn</c> → <c>eng_Latn</c>) and the matching token ids
/// are resolved from the tokenizer's <c>added_tokens</c> at initialization.
///
/// <para>Reuses the shared <see cref="Seq2SeqOnnxEngine"/> (same beam search /
/// no-repeat-n-gram / repetition-penalty decoding as opus-mt) and the same
/// <see cref="SentenceSegmenter"/>. The differences from opus-mt are the model
/// geometry, and NLLB's language-token protocol: the source sequence is prefixed
/// with the source-language token and the decoder is forced to emit the
/// target-language token first.</para>
/// </summary>
public sealed class NllbOnnxTranslator : ITranslator
{
    private const string DownloadHint =
        "Run scripts/download-model-nllb.ps1 to download the NLLB-200-distilled-600M " +
        "int8 ONNX model (Hugging Face repo Xenova/nllb-200-distilled-600M) into this directory.";

    // NLLB special token ids (fixed by the model; language ids are resolved from
    // the tokenizer's added_tokens at init).
    private const int MinLangId = 256001;  // language codes occupy the tail of the vocab
    private const int EosId = 2;           // </s>
    private const int PadId = 1;           // <pad>
    private const int DecoderStartId = 2;  // NLLB starts the decoder with </s>

    private const int MaxSentenceTokens = 100;

    private readonly string _modelDirectory;
    private readonly int _numBeams;
    private readonly string _executionProvider;
    private readonly int _gpuDeviceId;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // FLORES-200 codes from config; token ids resolved during InitializeAsync.
    private int _srcLangId = -1;
    private int _tgtLangId = -1;

    /// <summary>FLORES-200 source language code this instance translates from.</summary>
    public string SourceLanguage { get; }

    /// <summary>FLORES-200 target language code this instance translates to.</summary>
    public string TargetLanguage { get; }

    /// <summary>The execution provider actually in use after init ("cpu" or "cuda").</summary>
    public string ActiveProvider { get; private set; } = OnnxSessionFactory.Cpu;

    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private Tokenizer? _tokenizer;
    private Seq2SeqOnnxEngine? _engine;

    private int _numLayers = 12;
    private int _numHeads = 16;
    private int _headDim = 64;
    private int _maxNewTokens = 200;

    public bool IsReady { get; private set; }

    /// <param name="executionProvider">
    /// <c>"cpu"</c> (default) or <c>"cuda"</c> (NVIDIA). On CUDA the engine prefers the
    /// fp32 weights (int8 dynamic quantization is CPU-targeted); on CPU it prefers
    /// int8. Whichever is preferred, it falls back to whatever files exist. Unknown
    /// values fall back to CPU, and a CUDA init failure falls back to CPU automatically.
    /// </param>
    /// <param name="gpuDeviceId">CUDA device index (default 0). Ignored on CPU.</param>
    /// <param name="log">Optional sink for provider / file-selection / fallback log lines.</param>
    /// <param name="sourceLanguage">FLORES-200 code of the source language (e.g. "zho_Hans", "jpn_Jpan").</param>
    /// <param name="targetLanguage">FLORES-200 code of the target language (e.g. "eng_Latn").</param>
    public NllbOnnxTranslator(string modelDirectory, int numBeams = 4,
        string executionProvider = "cpu", int gpuDeviceId = 0, Action<string>? log = null,
        string sourceLanguage = "zho_Hans", string targetLanguage = "eng_Latn")
    {
        _modelDirectory = modelDirectory ?? throw new ArgumentNullException(nameof(modelDirectory));
        if (numBeams < 1) throw new ArgumentOutOfRangeException(nameof(numBeams), "Beam width must be >= 1.");
        if (string.IsNullOrWhiteSpace(sourceLanguage)) throw new ArgumentException("Source language code required.", nameof(sourceLanguage));
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("Target language code required.", nameof(targetLanguage));
        _numBeams = numBeams;
        _executionProvider = executionProvider ?? OnnxSessionFactory.Cpu;
        _gpuDeviceId = gpuDeviceId;
        _log = log;
        SourceLanguage = sourceLanguage.Trim();
        TargetLanguage = targetLanguage.Trim();
        ActiveProvider = OnnxSessionFactory.NormalizeProvider(_executionProvider);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsReady) return;
        if (!Directory.Exists(_modelDirectory))
            throw new FileNotFoundException(
                $"NLLB model directory not found: '{_modelDirectory}'. {DownloadHint}");

        // The GPU wants fp32 weights; CPU keeps the int8 files (current behavior).
        bool preferFp32 = OnnxSessionFactory.NormalizeProvider(_executionProvider) != OnnxSessionFactory.Cpu;
        string encoderPath = ResolveWeightFile(
            "encoder_model.onnx", "encoder_model_quantized.onnx", preferFp32);
        string decoderPath = ResolveWeightFile(
            "decoder_model_merged.onnx", "decoder_model_merged_quantized.onnx", preferFp32);
        string tokenizerPath = RequireFile("tokenizer.json");
        LoadConfig();

        await Task.Run(() =>
        {
            (_srcLangId, _tgtLangId) = ResolveLanguageIds(tokenizerPath, SourceLanguage, TargetLanguage);
            bool quantized = encoderPath.Contains("quantized", StringComparison.OrdinalIgnoreCase);
            (_encoder, _decoder, string provider) = OnnxSessionFactory.CreateEncoderDecoder(
                encoderPath, decoderPath, _executionProvider, _gpuDeviceId, quantized, _log);
            ActiveProvider = provider;
            _tokenizer = new Tokenizer(vocabPath: tokenizerPath);
            _engine = new Seq2SeqOnnxEngine(_encoder, _decoder, _numLayers, _numHeads, _headDim);
        }, ct).ConfigureAwait(false);

        _log?.Invoke(
            $"[nllb] Loaded {Path.GetFileName(encoderPath)} + {Path.GetFileName(decoderPath)} " +
            $"on {ActiveProvider} execution provider ({SourceLanguage}→{TargetLanguage}).");

        IsReady = true;
    }

    public async Task<string> TranslateAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (!IsReady || _tokenizer is null || _engine is null)
            throw new InvalidOperationException("Translator not initialized. Call InitializeAsync first.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RunTranslation(text, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string RunTranslation(string text, CancellationToken ct)
    {
        var tok = _tokenizer!;
        var engine = _engine!;

        var options = new GenerationOptions
        {
            DecoderStartTokenId = DecoderStartId,
            ForcedBosTokenId = _tgtLangId,
            EosTokenId = EosId,
            PadTokenId = PadId,
            MaxNewTokens = _maxNewTokens,
            NumBeams = _numBeams,
            LengthPenalty = 1.0,
        };

        var parts = new List<string>();
        foreach (string sentence in SentenceSegmenter.Split(text))
        {
            ct.ThrowIfCancellationRequested();
            foreach (string chunk in ChunkBySourceTokens(sentence, tok))
            {
                uint[] srcIds = BuildSource(tok.Encode(chunk));
                if (srcIds.Length == 0) continue;

                List<int> outIds = engine.Generate(srcIds, options, ct);
                if (outIds.Count == 0) continue;

                string english = tok.Decode(outIds.Select(x => (uint)x).ToArray()).Trim();
                if (english.Length > 0) parts.Add(english);
            }
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Converts the tokenizer's default output (which prefixes the baked-in
    /// <c>eng_Latn</c> source language and appends <c>&lt;/s&gt;</c>) into the correct
    /// NLLB source sequence for the configured direction: <c>[src_lang] … &lt;/s&gt;</c>.
    /// </summary>
    private uint[] BuildSource(uint[] encoded)
    {
        if (encoded.Length == 0) return encoded;

        var list = new List<uint>(encoded.Length + 1);
        int start = 0;
        // Drop any leading language-code token the export prepended (e.g. eng_Latn).
        if (encoded[0] >= MinLangId) start = 1;

        list.Add((uint)_srcLangId);
        for (int i = start; i < encoded.Length; i++) list.Add(encoded[i]);

        // Ensure the sequence ends with </s>.
        if (list.Count == 0 || list[^1] != (uint)EosId) list.Add((uint)EosId);
        return list.ToArray();
    }

    /// <summary>
    /// Resolves the token ids of the two FLORES-200 language codes from the
    /// tokenizer's <c>added_tokens</c> (language codes live there, at the tail of
    /// the vocab). Throws with an actionable message when a code isn't known —
    /// surfacing a config typo beats silently translating the wrong direction.
    /// </summary>
    private static (int src, int tgt) ResolveLanguageIds(string tokenizerPath, string source, string target)
    {
        int? src = null, tgt = null;
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(tokenizerPath)))
        {
            if (doc.RootElement.TryGetProperty("added_tokens", out var added) &&
                added.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var tok in added.EnumerateArray())
                {
                    if (!tok.TryGetProperty("content", out var content) ||
                        !tok.TryGetProperty("id", out var id)) continue;
                    string? code = content.GetString();
                    if (code == source) src = id.GetInt32();
                    if (code == target) tgt = id.GetInt32();
                    if (src is not null && tgt is not null) break;
                }
            }
        }

        if (src is null || tgt is null)
        {
            string missing = string.Join(" and ", new[]
            {
                src is null ? $"'{source}'" : null,
                tgt is null ? $"'{target}'" : null,
            }.Where(s => s is not null));
            throw new InvalidOperationException(
                $"NLLB language code {missing} not found in the tokenizer. Use FLORES-200 codes " +
                "(e.g. zho_Hans, jpn_Jpan, kor_Hang, fra_Latn, deu_Latn, eng_Latn) in " +
                "SourceLanguage/TargetLanguage.");
        }
        return (src.Value, tgt.Value);
    }

    private IEnumerable<string> ChunkBySourceTokens(string sentence, Tokenizer tok)
    {
        if (tok.Encode(sentence).Length <= MaxSentenceTokens)
        {
            yield return sentence;
            yield break;
        }

        var buffer = new System.Text.StringBuilder();
        int bufferTokens = 0;
        foreach (string clause in SentenceSegmenter.SplitClauses(sentence))
        {
            int clauseTokens = tok.Encode(clause).Length;
            if (buffer.Length > 0 && bufferTokens + clauseTokens > MaxSentenceTokens)
            {
                yield return buffer.ToString();
                buffer.Clear();
                bufferTokens = 0;
            }
            buffer.Append(clause);
            bufferTokens += clauseTokens;
        }
        if (buffer.Length > 0) yield return buffer.ToString();
    }

    private void LoadConfig()
    {
        try
        {
            string cfg = Path.Combine(_modelDirectory, "config.json");
            if (!File.Exists(cfg)) return;
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfg))?.AsObject();
            if (root is null) return;
            _numLayers = TryInt(root, "decoder_layers") ?? _numLayers;
            _numHeads = TryInt(root, "decoder_attention_heads") ?? _numHeads;
            int? dModel = TryInt(root, "d_model");
            if (dModel is int dm && _numHeads > 0) _headDim = dm / _numHeads;
            int? maxLen = TryInt(root, "max_length");
            if (maxLen is int ml && ml > 0) _maxNewTokens = Math.Min(256, ml);
        }
        catch { /* fall back to NLLB-600M defaults */ }
    }

    private static int? TryInt(System.Text.Json.Nodes.JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) return null;
        try { return node.GetValue<int>(); } catch { return null; }
    }

    private string RequireFile(string name)
    {
        string path = Path.Combine(_modelDirectory, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required NLLB model file '{name}' missing from '{_modelDirectory}'. {DownloadHint}", path);
        return path;
    }

    /// <summary>
    /// Picks the fp32 vs int8 weight file. <paramref name="preferFp32"/> (GPU)
    /// tries <paramref name="fp32Name"/> first; otherwise (CPU) <paramref name="int8Name"/>
    /// first. Falls back to whichever exists, and throws with the download hint if
    /// neither is present.
    /// </summary>
    private string ResolveWeightFile(string fp32Name, string int8Name, bool preferFp32)
    {
        string first = preferFp32 ? fp32Name : int8Name;
        string second = preferFp32 ? int8Name : fp32Name;
        string firstPath = Path.Combine(_modelDirectory, first);
        if (File.Exists(firstPath)) return firstPath;
        string secondPath = Path.Combine(_modelDirectory, second);
        if (File.Exists(secondPath))
        {
            if (preferFp32)
                _log?.Invoke($"[nllb] fp32 '{first}' not found; using '{second}'. GPU runs want the fp32 variant (download-model-nllb.ps1 -Variant fp32).");
            return secondPath;
        }
        throw new FileNotFoundException(
            $"NLLB weights not found in '{_modelDirectory}' (looked for '{fp32Name}' / '{int8Name}'). {DownloadHint}");
    }

    public void Dispose()
    {
        _encoder?.Dispose();
        _decoder?.Dispose();
        _gate.Dispose();
        _engine = null;
        _encoder = null;
        _decoder = null;
        _tokenizer = null;
        IsReady = false;
    }
}
