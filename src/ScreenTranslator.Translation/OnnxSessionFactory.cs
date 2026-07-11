using Microsoft.ML.OnnxRuntime;

namespace ScreenTranslator.Translation;

/// <summary>
/// The single place where ONNX Runtime <see cref="SessionOptions"/> are constructed
/// and encoder/decoder <see cref="InferenceSession"/> pairs are created. Both engines
/// (<see cref="LocalOnnxTranslator"/>, <see cref="NllbOnnxTranslator"/>) go through
/// here so the CPU-vs-DirectML decision, the DirectML-required session flags, and the
/// graceful GPU→CPU fallback live in one auditable location.
///
/// <para><b>CPU default is sacred:</b> when the provider is CPU the options are built
/// exactly as the engines built them before the DirectML work (ORT_ENABLE_ALL,
/// IntraOp = ProcessorCount/2, InterOp = 1) so shipping behavior is unchanged.</para>
///
/// <para><b>DirectML requirements</b> (per the ORT DirectML EP docs): memory-pattern
/// optimization must be disabled and the execution mode must be sequential, otherwise
/// session creation fails. Those are applied only on the DirectML path.</para>
/// </summary>
internal static class OnnxSessionFactory
{
    /// <summary>Canonical provider strings.</summary>
    public const string Cpu = "cpu";
    public const string DirectMl = "directml";

    /// <summary>
    /// Normalizes a user-supplied provider string to <see cref="Cpu"/> or
    /// <see cref="DirectMl"/>. Accepts "cpu", "directml" and the "dml" alias
    /// (case-insensitive); anything else falls back to CPU with a log line.
    /// </summary>
    public static string NormalizeProvider(string? provider, Action<string>? log = null)
    {
        string p = (provider ?? string.Empty).Trim().ToLowerInvariant();
        switch (p)
        {
            case Cpu:
                return Cpu;
            case DirectMl:
            case "dml":
            case "gpu":
                return DirectMl;
            default:
                log?.Invoke($"[onnx] Unknown execution provider '{provider}'; using cpu.");
                return Cpu;
        }
    }

    /// <summary>
    /// Creates the encoder and decoder sessions for a seq2seq model. When
    /// <paramref name="executionProvider"/> resolves to DirectML this tries the GPU
    /// first and, if DirectML initialization throws (no DX12 device, driver missing,
    /// unsupported model), logs the reason and transparently falls back to CPU
    /// sessions. The returned provider string is the one actually used.
    ///
    /// <para><paramref name="weightsAreQuantized"/> must be true when the model files
    /// are int8 dynamically-quantized. int8 + DirectML is unsupported and, on at least
    /// some drivers, causes a <b>native access violation that no managed try/catch can
    /// intercept</b> (it kills the process). So when DirectML is requested with int8
    /// weights we do NOT attempt it — we log and use CPU. Give DirectML fp32 weights.</para>
    /// </summary>
    public static (InferenceSession encoder, InferenceSession decoder, string provider) CreateEncoderDecoder(
        string encoderPath, string decoderPath, string executionProvider, int gpuDeviceId,
        bool weightsAreQuantized = false, Action<string>? log = null)
    {
        string requested = NormalizeProvider(executionProvider, log);

        if (requested == DirectMl && weightsAreQuantized)
        {
            log?.Invoke(
                "[onnx] DirectML requested but only int8-quantized weights are present. " +
                "int8 + DirectML is unsupported (can hard-crash the process); using CPU instead. " +
                "Download fp32 weights to run on the GPU.");
            requested = Cpu;
        }

        if (requested == DirectMl)
        {
            SessionOptions? dmlOptions = null;
            InferenceSession? encoder = null;
            try
            {
                dmlOptions = BuildDirectMlOptions(gpuDeviceId);
                encoder = new InferenceSession(encoderPath, dmlOptions);
                var decoder = new InferenceSession(decoderPath, dmlOptions);
                log?.Invoke($"[onnx] Using DirectML execution provider (GPU device {gpuDeviceId}).");
                return (encoder, decoder, DirectMl);
            }
            catch (Exception ex)
            {
                encoder?.Dispose();
                log?.Invoke(
                    $"[onnx] DirectML init failed ({ex.GetType().Name}: {ex.Message.Trim()}); " +
                    "falling back to CPU execution provider.");
                // fall through to the CPU path below
            }
            finally
            {
                dmlOptions?.Dispose();
            }
        }

        using SessionOptions cpuOptions = BuildCpuOptions();
        var cpuEncoder = new InferenceSession(encoderPath, cpuOptions);
        var cpuDecoder = new InferenceSession(decoderPath, cpuOptions);
        return (cpuEncoder, cpuDecoder, Cpu);
    }

    /// <summary>
    /// CPU session options — identical to the pre-DirectML engine code so CPU runs are
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
    /// DirectML session options. The DirectML EP mandates memory-pattern optimization
    /// OFF and sequential execution mode; it also owns its own scheduling, so the CPU
    /// thread-pool bounds are left at defaults. Graph optimization stays at
    /// ORT_ENABLE_ALL (supported with DML).
    /// </summary>
    private static SessionOptions BuildDirectMlOptions(int gpuDeviceId)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        // Required by the DirectML execution provider.
        options.EnableMemoryPattern = false;
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        options.AppendExecutionProvider_DML(Math.Max(0, gpuDeviceId));
        return options;
    }
}
