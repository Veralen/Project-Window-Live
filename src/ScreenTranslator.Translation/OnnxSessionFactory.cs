using Microsoft.ML.OnnxRuntime;

namespace ScreenTranslator.Translation;

/// <summary>
/// The single place where ONNX Runtime <see cref="SessionOptions"/> are constructed
/// and encoder/decoder <see cref="InferenceSession"/> pairs are created. Both engines
/// (<see cref="LocalOnnxTranslator"/>, <see cref="NllbOnnxTranslator"/>) go through
/// here so the CPU-vs-CUDA decision and the graceful GPU→CPU fallback live in one
/// auditable location.
///
/// <para><b>CPU default is sacred:</b> when the provider is CPU the options are built
/// exactly as the engines built them before any GPU work (ORT_ENABLE_ALL,
/// IntraOp = ProcessorCount/2, InterOp = 1) so shipping behavior is unchanged.</para>
///
/// <para><b>GPU history:</b> the first GPU path was DirectML; it was removed after
/// benchmarks showed the DML EP produces garbage for these KV-cache merged-decoder
/// exports on every GPU tested (docs/architecture.md §execution provider). CUDA
/// (NVIDIA-only) replaced it. The CUDA/cuDNN runtime DLLs are not bundled — when
/// they're missing, session creation throws and we fall back to CPU.</para>
/// </summary>
internal static class OnnxSessionFactory
{
    /// <summary>Canonical provider strings.</summary>
    public const string Cpu = "cpu";
    public const string Cuda = "cuda";

    /// <summary>
    /// Normalizes a user-supplied provider string to <see cref="Cpu"/> or
    /// <see cref="Cuda"/>. Accepts "cpu", "cuda" and the "gpu" alias; the legacy
    /// "directml"/"dml" values map to CUDA with a log line (the DML EP was removed
    /// as broken — an old config asking for GPU still gets the working GPU path,
    /// or the CPU fallback). Anything else falls back to CPU with a log line.
    /// </summary>
    public static string NormalizeProvider(string? provider, Action<string>? log = null)
    {
        string p = (provider ?? string.Empty).Trim().ToLowerInvariant();
        switch (p)
        {
            case Cpu:
                return Cpu;
            case Cuda:
            case "gpu":
                return Cuda;
            case "directml":
            case "dml":
                log?.Invoke("[onnx] DirectML support was removed (broken for these models — " +
                            "see docs/architecture.md); treating provider as 'cuda'.");
                return Cuda;
            default:
                log?.Invoke($"[onnx] Unknown execution provider '{provider}'; using cpu.");
                return Cpu;
        }
    }

    /// <summary>
    /// Creates the encoder and decoder sessions for a seq2seq model. When
    /// <paramref name="executionProvider"/> resolves to CUDA this tries the GPU
    /// first and, if CUDA initialization throws (no NVIDIA device, CUDA/cuDNN DLLs
    /// missing), logs the reason and transparently falls back to CPU sessions. The
    /// returned provider string is the one actually used.
    ///
    /// <para><paramref name="weightsAreQuantized"/> must be true when the model files
    /// are int8 dynamically-quantized. int8 dynamic quantization is CPU-targeted —
    /// on CUDA those ops just fall back to the CPU EP inside the session, paying
    /// GPU transfer costs for nothing — so when a GPU is requested with int8
    /// weights we log and use CPU. Give the GPU fp32 (or fp16) weights.</para>
    /// </summary>
    public static (InferenceSession encoder, InferenceSession decoder, string provider) CreateEncoderDecoder(
        string encoderPath, string decoderPath, string executionProvider, int gpuDeviceId,
        bool weightsAreQuantized = false, Action<string>? log = null)
    {
        string requested = NormalizeProvider(executionProvider, log);

        if (requested == Cuda && weightsAreQuantized)
        {
            log?.Invoke(
                "[onnx] CUDA requested but only int8-quantized weights are present. " +
                "int8 dynamic quantization is CPU-targeted and gains nothing on the GPU; " +
                "using CPU instead. Download fp32 weights to run on the GPU.");
            requested = Cpu;
        }

        if (requested == Cuda)
        {
            SessionOptions? cudaOptions = null;
            InferenceSession? encoder = null;
            try
            {
                cudaOptions = BuildCudaOptions(gpuDeviceId);
                encoder = new InferenceSession(encoderPath, cudaOptions);
                var decoder = new InferenceSession(decoderPath, cudaOptions);
                log?.Invoke($"[onnx] Using CUDA execution provider (GPU device {gpuDeviceId}).");
                return (encoder, decoder, Cuda);
            }
            catch (Exception ex)
            {
                encoder?.Dispose();
                log?.Invoke(
                    $"[onnx] CUDA init failed ({ex.GetType().Name}: {ex.Message.Trim()}); " +
                    "falling back to CPU execution provider. CUDA needs an NVIDIA GPU and the " +
                    "CUDA 12 / cuDNN 9 runtime DLLs beside the exe or on PATH.");
                // fall through to the CPU path below
            }
            finally
            {
                cudaOptions?.Dispose();
            }
        }

        using SessionOptions cpuOptions = BuildCpuOptions();
        var cpuEncoder = new InferenceSession(encoderPath, cpuOptions);
        var cpuDecoder = new InferenceSession(decoderPath, cpuOptions);
        return (cpuEncoder, cpuDecoder, Cpu);
    }

    /// <summary>
    /// CPU session options — identical to the pre-GPU engine code so CPU runs are
    /// byte-for-byte unaffected: full graph optimization, bounded thread pools for
    /// deterministic snip-time latency.
    /// </summary>
    private static SessionOptions BuildCpuOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        options.InterOpNumThreads = 1;
        return options;
    }

    /// <summary>
    /// CUDA session options. Graph optimization stays at ORT_ENABLE_ALL; the CPU
    /// thread-pool bounds are left at defaults (the GPU owns the heavy work, and a
    /// handful of shape/copy ops still run on the CPU EP).
    /// </summary>
    private static SessionOptions BuildCudaOptions(int gpuDeviceId)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        options.AppendExecutionProvider_CUDA(Math.Max(0, gpuDeviceId));
        return options;
    }
}
