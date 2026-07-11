using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ScreenTranslator.Translation;

/// <summary>
/// Decoding parameters for one <see cref="Seq2SeqOnnxEngine.Generate"/> call.
/// </summary>
internal sealed class GenerationOptions
{
    public int DecoderStartTokenId { get; init; }
    /// <summary>First token forced after the start token (NLLB target-language id). Null for opus-mt.</summary>
    public int? ForcedBosTokenId { get; init; }
    public int EosTokenId { get; init; }
    public int PadTokenId { get; init; }
    public int MaxNewTokens { get; init; } = 256;
    public int NumBeams { get; init; } = 4;
    public double LengthPenalty { get; init; } = 1.0;
    public double RepetitionPenalty { get; init; } = 1.15;
    public int NoRepeatNgramSize { get; init; } = 3;
}

/// <summary>
/// Shared ONNX encoder/decoder driver for Xenova-exported Marian-family seq2seq
/// models (opus-mt, NLLB). Encapsulates the KV-cache-merged decoder loop and a
/// quality-standard <b>beam search</b> with no-repeat-n-gram blocking, CTRL-style
/// repetition penalty, and length-normalized selection — the combination that
/// eliminates the greedy decoder's runaway repetition loops.
///
/// <para>Stateless across calls (single-flight is enforced by the owning
/// translator). The encoder cross-attention KV is computed once per input and
/// reused across every beam and step.</para>
/// </summary>
internal sealed class Seq2SeqOnnxEngine
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly int _numLayers;
    private readonly int _numHeads;
    private readonly int _headDim;

    public Seq2SeqOnnxEngine(InferenceSession encoder, InferenceSession decoder,
                             int numLayers, int numHeads, int headDim)
    {
        _encoder = encoder;
        _decoder = decoder;
        _numLayers = numLayers;
        _numHeads = numHeads;
        _headDim = headDim;
    }

    /// <summary>
    /// Beam-search generate. Returns the generated target token ids (excluding the
    /// decoder start token, any forced BOS token, and the terminating EOS).
    /// </summary>
    public List<int> Generate(uint[] srcIds, GenerationOptions opt, CancellationToken ct)
    {
        int srcLen = srcIds.Length;
        if (srcLen == 0) return new List<int>();

        // ----- Encoder pass (once) -------------------------------------------------
        var inputIds = new DenseTensor<long>(new[] { 1, srcLen });
        var attnMask = new DenseTensor<long>(new[] { 1, srcLen });
        for (int i = 0; i < srcLen; i++)
        {
            inputIds[0, i] = srcIds[i];
            attnMask[0, i] = 1;
        }

        DenseTensor<float> encoderHidden;
        using (var encResults = _encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attnMask),
        }))
        {
            encoderHidden = CopyFloat(First(encResults, "last_hidden_state"));
        }

        var encoderMask = new DenseTensor<long>(new[] { 1, srcLen });
        for (int i = 0; i < srcLen; i++) encoderMask[0, i] = 1;

        // Encoder cross-attention KV — identical for every beam/step; computed on the
        // first decoder step and reused thereafter.
        var sharedEncoderKv = new DenseTensor<float>[_numLayers * 2];

        // ----- Seed beam ----------------------------------------------------------
        // Step 0: feed the decoder start token (populates sharedEncoderKv).
        var (logits0, kv0) = DecoderStep(opt.DecoderStartTokenId, null, encoderHidden, encoderMask, sharedEncoderKv);

        float[] seedLogits;
        DenseTensor<float>[] seedKv;
        if (opt.ForcedBosTokenId is int forced)
        {
            // NLLB forces the target-language token as the first decoded token; run one
            // more forced step so the seed's logits describe the first *real* token.
            var (logits1, kv1) = DecoderStep(forced, kv0, encoderHidden, encoderMask, sharedEncoderKv);
            seedLogits = logits1;
            seedKv = kv1;
        }
        else
        {
            seedLogits = logits0;
            seedKv = kv0;
        }

        var active = new List<Beam> { new Beam(new List<int>(), 0.0, seedKv, seedLogits) };
        var finished = new List<(List<int> tokens, double norm)>();

        int topN = Math.Max(2 * opt.NumBeams, opt.NumBeams + 1);

        for (int step = 0; step < opt.MaxNewTokens; step++)
        {
            ct.ThrowIfCancellationRequested();

            // Gather candidate (parent, token, cumulative-score) triples.
            var candidates = new List<(int parent, int token, double score)>(active.Count * topN);
            for (int bi = 0; bi < active.Count; bi++)
            {
                Beam beam = active[bi];
                var top = TopKLogProbs(beam.NextLogits, beam.Tokens, opt, topN);
                foreach (var (token, logProb) in top)
                    candidates.Add((bi, token, beam.Score + logProb));
            }

            candidates.Sort((a, b) => b.score.CompareTo(a.score));

            var nextActive = new List<Beam>(opt.NumBeams);
            foreach (var cand in candidates)
            {
                if (nextActive.Count >= opt.NumBeams) break;
                Beam parent = active[cand.parent];

                if (cand.token == opt.EosTokenId)
                {
                    int len = parent.Tokens.Count + 1;
                    double norm = cand.score / Math.Pow(Math.Max(1, len), opt.LengthPenalty);
                    finished.Add((parent.Tokens, norm));
                    continue;
                }

                // Materialize the surviving beam: run the decoder once to obtain its
                // next-token distribution and self-attention KV.
                var (logits, kv) = DecoderStep(cand.token, parent.Kv, encoderHidden, encoderMask, sharedEncoderKv);
                var tokens = new List<int>(parent.Tokens.Count + 1);
                tokens.AddRange(parent.Tokens);
                tokens.Add(cand.token);
                nextActive.Add(new Beam(tokens, cand.score, kv, logits));
            }

            active = nextActive;
            if (active.Count == 0) break;
            // Early stopping: once we have enough completed hypotheses, and the best
            // still-active beam can't beat the best finished one, stop.
            if (finished.Count >= opt.NumBeams)
            {
                double bestFinished = double.NegativeInfinity;
                foreach (var f in finished) bestFinished = Math.Max(bestFinished, f.norm);
                double bestActive = double.NegativeInfinity;
                foreach (var b in active)
                {
                    double norm = b.Score / Math.Pow(Math.Max(1, b.Tokens.Count), opt.LengthPenalty);
                    bestActive = Math.Max(bestActive, norm);
                }
                if (bestFinished >= bestActive) break;
            }
        }

        // Fold any still-active beams into the candidate pool (length-normalized).
        foreach (var b in active)
        {
            double norm = b.Score / Math.Pow(Math.Max(1, b.Tokens.Count), opt.LengthPenalty);
            finished.Add((b.Tokens, norm));
        }

        if (finished.Count == 0) return new List<int>();

        List<int> best = finished[0].tokens;
        double bestNorm = finished[0].norm;
        for (int i = 1; i < finished.Count; i++)
        {
            if (finished[i].norm > bestNorm)
            {
                bestNorm = finished[i].norm;
                best = finished[i].tokens;
            }
        }
        return best;
    }

    private sealed class Beam
    {
        public List<int> Tokens { get; }
        public double Score { get; }
        public DenseTensor<float>[] Kv { get; }
        public float[] NextLogits { get; }

        public Beam(List<int> tokens, double score, DenseTensor<float>[] kv, float[] nextLogits)
        {
            Tokens = tokens;
            Score = score;
            Kv = kv;
            NextLogits = nextLogits;
        }
    }

    // ----- one decoder step ------------------------------------------------------

    private (float[] logits, DenseTensor<float>[] decoderKv) DecoderStep(
        long inputToken,
        DenseTensor<float>[]? pastDecoderKv,
        DenseTensor<float> encoderHidden,
        DenseTensor<long> encoderMask,
        DenseTensor<float>[] sharedEncoderKv)
    {
        bool firstStep = pastDecoderKv is null;
        var empty = new DenseTensor<float>(new[] { 1, _numHeads, 0, _headDim });

        var stepInputIds = new DenseTensor<long>(new[] { 1, 1 });
        stepInputIds[0, 0] = inputToken;

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
            var decKey = firstStep ? empty : pastDecoderKv![l * 2];
            var decVal = firstStep ? empty : pastDecoderKv![l * 2 + 1];
            var encKey = firstStep ? empty : sharedEncoderKv[l * 2];
            var encVal = firstStep ? empty : sharedEncoderKv[l * 2 + 1];
            inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.decoder.key", decKey));
            inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.decoder.value", decVal));
            inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.encoder.key", encKey));
            inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.encoder.value", encVal));
        }

        using var results = _decoder.Run(inputs);

        float[] logits = LastStepLogits(First(results, "logits"));
        var decoderKv = new DenseTensor<float>[_numLayers * 2];
        for (int l = 0; l < _numLayers; l++)
        {
            decoderKv[l * 2] = CopyFloat(First(results, $"present.{l}.decoder.key"));
            decoderKv[l * 2 + 1] = CopyFloat(First(results, $"present.{l}.decoder.value"));
            if (firstStep)
            {
                sharedEncoderKv[l * 2] = CopyFloat(First(results, $"present.{l}.encoder.key"));
                sharedEncoderKv[l * 2 + 1] = CopyFloat(First(results, $"present.{l}.encoder.value"));
            }
        }
        return (logits, decoderKv);
    }

    // ----- logits post-processing + top-k ----------------------------------------

    /// <summary>
    /// Applies pad masking, CTRL-style repetition penalty and no-repeat-n-gram
    /// blocking to a fresh copy of the step logits, then returns the top
    /// <paramref name="topN"/> tokens as (token, logProb) pairs (properly
    /// log-softmax normalized so beam scores are comparable).
    /// </summary>
    private List<(int token, double logProb)> TopKLogProbs(
        float[] logits, List<int> generated, GenerationOptions opt, int topN)
    {
        int vocab = logits.Length;
        // Work on a copy so the (shared) beam logits array is never mutated.
        var work = (float[])logits.Clone();

        // Never emit the pad token.
        if (opt.PadTokenId >= 0 && opt.PadTokenId < vocab)
            work[opt.PadTokenId] = float.NegativeInfinity;

        // CTRL-style repetition penalty over already-generated tokens.
        if (opt.RepetitionPenalty is > 1.0 && generated.Count > 0)
        {
            float pen = (float)opt.RepetitionPenalty;
            // Distinct tokens only — penalizing once is the standard behavior.
            foreach (int t in new HashSet<int>(generated))
            {
                if (t < 0 || t >= vocab) continue;
                float v = work[t];
                if (float.IsNegativeInfinity(v)) continue;
                work[t] = v > 0 ? v / pen : v * pen;
            }
        }

        // No-repeat-n-gram blocking: forbid completing an (n)-gram already present.
        int ng = opt.NoRepeatNgramSize;
        if (ng > 1 && generated.Count >= ng - 1)
        {
            int prefLen = ng - 1;
            // The (n-1) tokens just generated.
            for (int p = 0; p + prefLen < generated.Count; p++)
            {
                bool match = true;
                for (int k = 0; k < prefLen; k++)
                {
                    if (generated[p + k] != generated[generated.Count - prefLen + k]) { match = false; break; }
                }
                if (match)
                {
                    int banned = generated[p + prefLen];
                    if (banned >= 0 && banned < vocab) work[banned] = float.NegativeInfinity;
                }
            }
        }

        // Log-softmax normalization (for cross-beam-comparable scores).
        double max = double.NegativeInfinity;
        for (int v = 0; v < vocab; v++) if (work[v] > max) max = work[v];
        double sumExp = 0.0;
        for (int v = 0; v < vocab; v++)
        {
            float w = work[v];
            if (!float.IsNegativeInfinity(w)) sumExp += Math.Exp(w - max);
        }
        double logZ = max + Math.Log(sumExp);

        // Partial top-k selection over the (penalized) logits.
        var top = new List<(int token, float logit)>(topN + 1);
        float threshold = float.NegativeInfinity;
        for (int v = 0; v < vocab; v++)
        {
            float w = work[v];
            if (float.IsNegativeInfinity(w)) continue;
            if (top.Count < topN)
            {
                top.Add((v, w));
                if (top.Count == topN)
                {
                    top.Sort((a, b) => a.logit.CompareTo(b.logit));
                    threshold = top[0].logit;
                }
            }
            else if (w > threshold)
            {
                top[0] = (v, w);
                // Re-bubble the new minimum to the front (small topN, cheap).
                top.Sort((a, b) => a.logit.CompareTo(b.logit));
                threshold = top[0].logit;
            }
        }

        var result = new List<(int, double)>(top.Count);
        foreach (var (token, logit) in top)
            result.Add((token, logit - logZ));
        // Highest first.
        result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return result;
    }

    private static float[] LastStepLogits(DisposableNamedOnnxValue logitsValue)
    {
        var logits = logitsValue.AsTensor<float>();
        var dims = logits.Dimensions; // [1, seq, vocab]
        int seq = dims[1];
        int vocab = dims[2];
        int last = seq - 1;
        var row = new float[vocab];
        for (int v = 0; v < vocab; v++) row[v] = logits[0, last, v];
        return row;
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
}
