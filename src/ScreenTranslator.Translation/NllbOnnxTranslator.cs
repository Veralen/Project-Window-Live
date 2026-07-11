using Microsoft.ML.OnnxRuntime;
using ScreenTranslator.Core.Translation;
using Tokenizers.DotNet;

namespace ScreenTranslator.Translation;

/// <summary>
/// <b>Benchmark-only</b> zho_Hans → eng_Latn translator on
/// facebook/NLLB-200-distilled-600M (int8 ONNX, Xenova export). This is the
/// reference model named in the product design doc; it exists so a shipping
/// decision can be made against opus-mt. <b>It is not wired into the app</b> — only
/// TranslatorCli's <c>--engine nllb</c> path constructs it.
///
/// <para>Reuses the shared <see cref="Seq2SeqOnnxEngine"/> (same beam search /
/// no-repeat-n-gram / repetition-penalty decoding as opus-mt) and the same
/// <see cref="SentenceSegmenter"/>. The differences from opus-mt are the model
/// geometry, and NLLB's language-token protocol: the source sequence is prefixed
/// with the <c>zho_Hans</c> token and the decoder is forced to emit the
/// <c>eng_Latn</c> token first.</para>
/// </summary>
public sealed class NllbOnnxTranslator : ITranslator
{
    private const string DownloadHint =
        "Run scripts/download-model-nllb.ps1 to download the NLLB-200-distilled-600M " +
        "int8 ONNX model (Hugging Face repo Xenova/nllb-200-distilled-600M) into this directory.";

    // NLLB special / language token ids (from the tokenizer's added_tokens).
    private const int SrcLangId = 256200;  // zho_Hans
    private const int TgtLangId = 256047;  // eng_Latn
    private const int MinLangId = 256001;  // language codes occupy the tail of the vocab
    private const int EosId = 2;           // </s>
    private const int PadId = 1;           // <pad>
    private const int DecoderStartId = 2;  // NLLB starts the decoder with </s>

    private const int MaxSentenceTokens = 100;

    private readonly string _modelDirectory;
    private readonly int _numBeams;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private Tokenizer? _tokenizer;
    private Seq2SeqOnnxEngine? _engine;

    private int _numLayers = 12;
    private int _numHeads = 16;
    private int _headDim = 64;
    private int _maxNewTokens = 200;

    public bool IsReady { get; private set; }

    public NllbOnnxTranslator(string modelDirectory, int numBeams = 4)
    {
        _modelDirectory = modelDirectory ?? throw new ArgumentNullException(nameof(modelDirectory));
        if (numBeams < 1) throw new ArgumentOutOfRangeException(nameof(numBeams), "Beam width must be >= 1.");
        _numBeams = numBeams;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsReady) return;
        if (!Directory.Exists(_modelDirectory))
            throw new FileNotFoundException(
                $"NLLB model directory not found: '{_modelDirectory}'. {DownloadHint}");

        string encoderPath = RequireFile("encoder_model_quantized.onnx");
        string decoderPath = RequireFile("decoder_model_merged_quantized.onnx");
        string tokenizerPath = RequireFile("tokenizer.json");
        LoadConfig();

        await Task.Run(() =>
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            options.InterOpNumThreads = 1;

            _encoder = new InferenceSession(encoderPath, options);
            _decoder = new InferenceSession(decoderPath, options);
            _tokenizer = new Tokenizer(vocabPath: tokenizerPath);
            _engine = new Seq2SeqOnnxEngine(_encoder, _decoder, _numLayers, _numHeads, _headDim);
        }, ct).ConfigureAwait(false);

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
            ForcedBosTokenId = TgtLangId,
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
    /// NLLB source sequence for zho→en: <c>[zho_Hans] … &lt;/s&gt;</c>.
    /// </summary>
    private static uint[] BuildSource(uint[] encoded)
    {
        if (encoded.Length == 0) return encoded;

        var list = new List<uint>(encoded.Length + 1);
        int start = 0;
        // Drop any leading language-code token the export prepended (e.g. eng_Latn).
        if (encoded[0] >= MinLangId) start = 1;

        list.Add(SrcLangId);
        for (int i = start; i < encoded.Length; i++) list.Add(encoded[i]);

        // Ensure the sequence ends with </s>.
        if (list.Count == 0 || list[^1] != (uint)EosId) list.Add((uint)EosId);
        return list.ToArray();
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
