using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ScreenTranslator.Core.Translation;
using Tokenizers.DotNet;

namespace ScreenTranslator.Translation;

/// <summary>
/// Fully local (no-cloud) Chinese-&gt;English translation engine built on ONNX
/// Runtime. Runs the Helsinki-NLP opus-mt-zh-en Marian model exported to ONNX
/// (Hugging Face repo <c>Xenova/opus-mt-zh-en</c>: separate encoder +
/// KV-cache-merged decoder). Single-flight (calls serialized internally). Get the
/// model with <c>scripts/download-model.ps1</c>.
///
/// <para>Quality: the input block is <b>segmented into sentences</b>
/// (<see cref="SentenceSegmenter"/>) and each sentence is translated separately —
/// Marian models are trained on sentence pairs and degrade badly on multi-sentence
/// paragraphs. Decoding is <b>beam search</b> with no-repeat-n-gram blocking and a
/// repetition penalty (<see cref="Seq2SeqOnnxEngine"/>), which removes the runaway
/// repetition loops the previous greedy decoder produced on dense prose.</para>
/// </summary>
public sealed class LocalOnnxTranslator : ITranslator
{
    private const string DownloadHint =
        "Run scripts/download-model.ps1 to download the opus-mt-zh-en ONNX model " +
        "(Hugging Face repo Xenova/opus-mt-zh-en) into this directory.";

    private readonly string _modelDirectory;
    private readonly bool _preferQuantized;
    private readonly int? _beamWidthOverride;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private Tokenizer? _tokenizer;
    private Seq2SeqOnnxEngine? _engine;

    // Decoding parameters (loaded from config.json / generation_config.json).
    private int _decoderStartTokenId = 65000;
    private int _eosTokenId = 0;
    private int _padTokenId = 65000;
    private int _numLayers = 6;
    private int _numHeads = 8;
    private int _headDim = 64;
    private int _maxNewTokens = 256;
    private int _numBeams = 4;
    private double _lengthPenalty = 1.0;

    // Beam width is capped for latency: opus-mt's config asks for 6, but on this CPU
    // 4 is the quality/latency sweet spot (see the engine's benchmark report).
    private const int DefaultBeamCap = 4;

    // A single "sentence" longer than this many source tokens is split further at
    // comma-class punctuation before translation (keeps Marian in its comfort zone).
    private const int MaxSentenceTokens = 100;

    public bool IsReady { get; private set; }

    /// <param name="modelDirectory">
    /// Directory containing the ONNX export (encoder + merged decoder) plus the
    /// tokenizer/config files. For the default app config this is
    /// <c>%LOCALAPPDATA%\ScreenTranslator\models</c>
    /// (<see cref="ScreenTranslator.Core.Config.AppConfig.ResolveModelDirectory"/>).
    /// </param>
    /// <param name="preferQuantized">
    /// When false (default) the full-precision encoder/decoder are used if present,
    /// falling back to the int8-quantized files otherwise. On a typical consumer
    /// CPU (no AVX-512 VNNI) fp32 measured both faster AND higher quality than int8
    /// dynamic quantization here, so it is the default. Pass true to force the
    /// smaller int8 model (~113 MB vs ~446 MB) when download size / RAM matters more
    /// than latency; it still translates a short block in well under a second.
    /// Whichever variant is preferred, the other is used as a fallback if the
    /// preferred files are absent.
    /// </param>
    /// <param name="beamWidth">
    /// Optional override for the beam-search width. When null (default) the engine
    /// uses <c>min(generation_config.num_beams, 4)</c>. Pass 1 for greedy decoding,
    /// or a larger value to trade latency for quality.
    /// </param>
    public LocalOnnxTranslator(string modelDirectory, bool preferQuantized = false, int? beamWidth = null)
    {
        _modelDirectory = modelDirectory ?? throw new ArgumentNullException(nameof(modelDirectory));
        _preferQuantized = preferQuantized;
        if (beamWidth is int bw && bw < 1)
            throw new ArgumentOutOfRangeException(nameof(beamWidth), "Beam width must be >= 1.");
        _beamWidthOverride = beamWidth;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsReady) return;

        if (!Directory.Exists(_modelDirectory))
            throw new FileNotFoundException(
                $"Translation model directory not found: '{_modelDirectory}'. {DownloadHint}");

        string encoderPath = ResolveModelFile("encoder_model_quantized.onnx", "encoder_model.onnx");
        string decoderPath = ResolveModelFile("decoder_model_merged_quantized.onnx", "decoder_model_merged.onnx");
        string tokenizerPath = RequireFile("tokenizer.json");
        LoadDecodingConfig();

        // Building sessions + tokenizer is CPU/IO heavy; keep it off the caller's thread.
        await Task.Run(() =>
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            // Deterministic, snip-friendly CPU latency: bound thread pools.
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            options.InterOpNumThreads = 1;

            _encoder = new InferenceSession(encoderPath, options);
            _decoder = new InferenceSession(decoderPath, options);
            _tokenizer = new Tokenizer(vocabPath: SanitizeTokenizer(tokenizerPath));
            _engine = new Seq2SeqOnnxEngine(_encoder, _decoder, _numLayers, _numHeads, _headDim);
        }, ct).ConfigureAwait(false);

        IsReady = true;
    }

    public async Task<string> TranslateAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
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
            DecoderStartTokenId = _decoderStartTokenId,
            EosTokenId = _eosTokenId,
            PadTokenId = _padTokenId,
            MaxNewTokens = _maxNewTokens,
            NumBeams = _numBeams,
            LengthPenalty = _lengthPenalty,
        };

        // Segment the block into sentences and translate each one on its own — Marian
        // MT degrades sharply on multi-sentence input. Outputs are joined with a space.
        var parts = new List<string>();
        foreach (string sentence in SentenceSegmenter.Split(text))
        {
            ct.ThrowIfCancellationRequested();
            foreach (string chunk in ChunkBySourceTokens(sentence, tok))
            {
                uint[] srcIds = tok.Encode(chunk);
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
    /// Yields translation chunks for one sentence. Normally the sentence itself; but
    /// if it still exceeds <see cref="MaxSentenceTokens"/> source tokens it is broken
    /// at comma-class punctuation and the clauses are regrouped to stay under the cap.
    /// </summary>
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

    // ----- init helpers ------------------------------------------------------------

    private string ResolveModelFile(string quantizedName, string fullName)
    {
        string first = _preferQuantized ? quantizedName : fullName;
        string second = _preferQuantized ? fullName : quantizedName;
        string firstPath = Path.Combine(_modelDirectory, first);
        if (File.Exists(firstPath)) return firstPath;
        string secondPath = Path.Combine(_modelDirectory, second);
        if (File.Exists(secondPath)) return secondPath;
        throw new FileNotFoundException(
            $"Model weights not found in '{_modelDirectory}' (looked for '{quantizedName}' / '{fullName}'). {DownloadHint}");
    }

    private string RequireFile(string name)
    {
        string path = Path.Combine(_modelDirectory, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required model file '{name}' missing from '{_modelDirectory}'. {DownloadHint}", path);
        return path;
    }

    private void LoadDecodingConfig()
    {
        // config.json holds architecture (layer/head counts); generation_config.json
        // (when present) holds the authoritative decoding token ids.
        var config = TryReadJson(Path.Combine(_modelDirectory, "config.json"));
        var gen = TryReadJson(Path.Combine(_modelDirectory, "generation_config.json"));

        _decoderStartTokenId = ReadInt(gen, "decoder_start_token_id")
                               ?? ReadInt(config, "decoder_start_token_id") ?? _decoderStartTokenId;
        _eosTokenId = ReadInt(gen, "eos_token_id") ?? ReadInt(config, "eos_token_id") ?? _eosTokenId;
        _padTokenId = ReadInt(gen, "pad_token_id") ?? ReadInt(config, "pad_token_id") ?? _padTokenId;

        _numLayers = ReadInt(config, "decoder_layers") ?? _numLayers;
        _numHeads = ReadInt(config, "decoder_attention_heads") ?? _numHeads;
        int? dModel = ReadInt(config, "d_model");
        if (dModel is int dm && _numHeads > 0) _headDim = dm / _numHeads;

        int? maxLen = ReadInt(gen, "max_length") ?? ReadInt(config, "max_length");
        if (maxLen is int ml && ml > 0) _maxNewTokens = Math.Min(_maxNewTokens, ml);

        // Beam width: config asks for 6; cap at DefaultBeamCap for latency unless the
        // caller explicitly overrode it. length_penalty defaults to 1.0 (opus-mt's
        // generation_config does not set it).
        int configBeams = ReadInt(gen, "num_beams") ?? ReadInt(config, "num_beams") ?? _numBeams;
        _numBeams = _beamWidthOverride ?? Math.Min(configBeams, DefaultBeamCap);
        _lengthPenalty = ReadDouble(gen, "length_penalty") ?? ReadDouble(config, "length_penalty") ?? _lengthPenalty;
    }

    private static JsonObject? TryReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() : null;
        }
        catch { return null; }
    }

    private static int? ReadInt(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null) return null;
        try { return node.GetValue<int>(); } catch { return null; }
    }

    private static double? ReadDouble(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null) return null;
        try { return node.GetValue<double>(); } catch { return null; }
    }

    /// <summary>
    /// The Xenova opus-mt tokenizer.json declares a <c>Precompiled</c> normalizer
    /// with a null charsmap, which the Rust tokenizers backend rejects on load.
    /// That normalizer is a no-op, so strip it and cache the fixed file. The cache
    /// is keyed on the source file's size+timestamp so it regenerates if the model
    /// is re-downloaded.
    /// </summary>
    private static string SanitizeTokenizer(string tokenizerPath)
    {
        var info = new FileInfo(tokenizerPath);
        string key = $"{info.Length}_{info.LastWriteTimeUtc.Ticks}";
        string cacheDir = Path.Combine(Path.GetTempPath(), "ScreenTranslator");
        Directory.CreateDirectory(cacheDir);
        string cachePath = Path.Combine(cacheDir, $"opus-mt-zh-en.tokenizer.{key}.json");

        if (File.Exists(cachePath)) return cachePath;

        var root = JsonNode.Parse(File.ReadAllText(tokenizerPath))!.AsObject();
        if (root["normalizer"] is JsonObject norm &&
            norm["type"]?.GetValue<string>() == "Precompiled" &&
            norm["precompiled_charsmap"] is null)
        {
            root["normalizer"] = null;
        }

        string tmp = cachePath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString());
        File.Move(tmp, cachePath, overwrite: true);
        return cachePath;
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
