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
/// KV-cache-merged decoder). Greedy decoding, single-flight (calls serialized
/// internally). Get the model with <c>scripts/download-model.ps1</c>.
/// </summary>
public sealed class LocalOnnxTranslator : ITranslator
{
    private const string DownloadHint =
        "Run scripts/download-model.ps1 to download the opus-mt-zh-en ONNX model " +
        "(Hugging Face repo Xenova/opus-mt-zh-en) into this directory.";

    private readonly string _modelDirectory;
    private readonly bool _preferQuantized;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private Tokenizer? _tokenizer;

    // Decoding parameters (loaded from config.json / generation_config.json).
    private int _decoderStartTokenId = 65000;
    private int _eosTokenId = 0;
    private int _padTokenId = 65000;
    private int _numLayers = 6;
    private int _numHeads = 8;
    private int _headDim = 64;
    private int _maxNewTokens = 256;

    // Runaway-repetition guard: stop if one token repeats this many times in a row.
    private const int MaxConsecutiveRepeats = 8;

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
    public LocalOnnxTranslator(string modelDirectory, bool preferQuantized = false)
    {
        _modelDirectory = modelDirectory ?? throw new ArgumentNullException(nameof(modelDirectory));
        _preferQuantized = preferQuantized;
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
        }, ct).ConfigureAwait(false);

        IsReady = true;
    }

    public async Task<string> TranslateAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        if (!IsReady || _encoder is null || _decoder is null || _tokenizer is null)
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
        var enc = _encoder!;
        var dec = _decoder!;
        var tok = _tokenizer!;

        // 1) Tokenize source. Tokenizers.DotNet appends the </s> (eos) the encoder expects.
        uint[] srcIds = tok.Encode(text);
        int srcLen = srcIds.Length;
        if (srcLen == 0) return string.Empty;

        var inputIds = new DenseTensor<long>(new[] { 1, srcLen });
        var attnMask = new DenseTensor<long>(new[] { 1, srcLen });
        for (int i = 0; i < srcLen; i++)
        {
            inputIds[0, i] = srcIds[i];
            attnMask[0, i] = 1;
        }

        // 2) Encoder pass -> encoder_hidden_states (reused every decode step).
        DenseTensor<float> encoderHidden;
        using (var encResults = enc.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attnMask),
        }))
        {
            encoderHidden = CopyFloat(First(encResults, "last_hidden_state"));
        }

        // 3) Greedy decode with the KV-cache-merged decoder.
        var encoderMask = new DenseTensor<long>(new[] { 1, srcLen });
        for (int i = 0; i < srcLen; i++) encoderMask[0, i] = 1;

        // Empty (zero-length) self/cross-attention caches for the first (no-cache) pass.
        var emptyDecoderKv = new DenseTensor<float>(new[] { 1, _numHeads, 0, _headDim });
        DenseTensor<float>[] pastDecoder = new DenseTensor<float>[_numLayers * 2];
        DenseTensor<float>[] cachedEncoderKv = new DenseTensor<float>[_numLayers * 2]; // filled after step 0

        var generated = new List<uint>(_maxNewTokens);
        int currentToken = _decoderStartTokenId;
        bool firstStep = true;
        int lastToken = -1;
        int repeatRun = 0;

        for (int step = 0; step < _maxNewTokens; step++)
        {
            ct.ThrowIfCancellationRequested();

            var stepInputIds = new DenseTensor<long>(new[] { 1, 1 });
            stepInputIds[0, 0] = currentToken;

            var inputs = new List<NamedOnnxValue>(4 + _numLayers * 4)
            {
                NamedOnnxValue.CreateFromTensor("input_ids", stepInputIds),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", encoderMask),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHidden),
                NamedOnnxValue.CreateFromTensor("use_cache_branch",
                    new DenseTensor<bool>(new[] { !firstStep }, new[] { 1 })),
            };
            for (int l = 0; l < _numLayers; l++)
            {
                var decKey = firstStep ? emptyDecoderKv : pastDecoder[l * 2];
                var decVal = firstStep ? emptyDecoderKv : pastDecoder[l * 2 + 1];
                var encKey = firstStep ? emptyDecoderKv : cachedEncoderKv[l * 2];
                var encVal = firstStep ? emptyDecoderKv : cachedEncoderKv[l * 2 + 1];
                inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.decoder.key", decKey));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.decoder.value", decVal));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.encoder.key", encKey));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.encoder.value", encVal));
            }

            using var results = dec.Run(inputs);

            int nextToken = ArgMaxLastStep(First(results, "logits"));

            // Update KV caches for the next step (copied out before results dispose).
            for (int l = 0; l < _numLayers; l++)
            {
                pastDecoder[l * 2] = CopyFloat(First(results, $"present.{l}.decoder.key"));
                pastDecoder[l * 2 + 1] = CopyFloat(First(results, $"present.{l}.decoder.value"));
                if (firstStep)
                {
                    cachedEncoderKv[l * 2] = CopyFloat(First(results, $"present.{l}.encoder.key"));
                    cachedEncoderKv[l * 2 + 1] = CopyFloat(First(results, $"present.{l}.encoder.value"));
                }
            }

            firstStep = false;

            if (nextToken == _eosTokenId) break;

            generated.Add((uint)nextToken);
            currentToken = nextToken;

            // Runaway-repetition guard.
            if (nextToken == lastToken)
            {
                if (++repeatRun >= MaxConsecutiveRepeats) break;
            }
            else
            {
                repeatRun = 0;
                lastToken = nextToken;
            }
        }

        if (generated.Count == 0) return string.Empty;
        return tok.Decode(generated.ToArray()).Trim();
    }

    // ----- logits / tensor helpers -------------------------------------------------

    /// <summary>Argmax over the last position's logits, masking the pad token.</summary>
    private int ArgMaxLastStep(DisposableNamedOnnxValue logitsValue)
    {
        var logits = logitsValue.AsTensor<float>();
        var dims = logits.Dimensions; // [1, seq, vocab]
        int seq = dims[1];
        int vocab = dims[2];
        int last = seq - 1;

        int best = 0;
        float bestVal = float.NegativeInfinity;
        for (int v = 0; v < vocab; v++)
        {
            if (v == _padTokenId) continue; // bad_words_ids: never emit pad
            float val = logits[0, last, v];
            if (val > bestVal)
            {
                bestVal = val;
                best = v;
            }
        }
        return best;
    }

    private static DenseTensor<float> CopyFloat(DisposableNamedOnnxValue value)
    {
        var t = value.AsTensor<float>();
        return new DenseTensor<float>(t.ToArray(), t.Dimensions.ToArray());
    }

    private static DisposableNamedOnnxValue First(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, string name)
    {
        foreach (var r in results)
            if (r.Name == name) return r;
        throw new KeyNotFoundException($"ONNX output '{name}' not found.");
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
        _encoder = null;
        _decoder = null;
        _tokenizer = null;
        IsReady = false;
    }
}
