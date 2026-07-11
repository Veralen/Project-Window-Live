// Test harness for the local ONNX translation engine (Work Package 2).
//
//   dotnet run --project tools/TranslatorCli -- "你好，世界"
//   dotnet run --project tools/TranslatorCli -- --model-dir C:\path\to\models "设置" "文件"
//   dotnet run --project tools/TranslatorCli -- --quantized "你好"       # force int8 weights
//   dotnet run --project tools/TranslatorCli -- --engine nllb "你好"     # NLLB multilingual engine
//   dotnet run --project tools/TranslatorCli -- --engine nllb --src jpn_Jpan "こんにちは"
//   dotnet run --project tools/TranslatorCli -- --beams 6 "你好"         # override beam width
//
// Flags: --model-dir <path>, --engine opus|nllb (default opus; nllb is the
// multilingual engine and reads from the sibling models-nllb dir), --src/--tgt
// <FLORES-200 code> (nllb only; defaults zho_Hans/eng_Latn), --beams <n> (override
// beam width; opus default is min(config,4)), --quantized/--int8 (force int8, opus
// only), --fp32 (force fp32, the opus default), --ep cpu|cuda (execution
// provider, default cpu; CUDA needs the CUDA 12/cuDNN 9 DLLs beside the binary or
// on PATH), --gpu (shorthand for --ep cuda), --device <n> (CUDA device index).
// Remaining args are translated in sequence. Prints each translation with
// per-call timing. Exits non-zero when the model is missing.
using System.Diagnostics;
using ScreenTranslator.Core.Config;
using ScreenTranslator.Core.Translation;
using ScreenTranslator.Translation;

string? modelDir = null;
bool preferQuantized = false; // default matches the engine (fp32)
string engine = "opus";       // "opus" (default, app engine) or "nllb" (benchmark only)
int? beamWidth = null;        // null -> engine default (min(config, 4))
string provider = "cpu";      // "cpu" (default) or "cuda"
int deviceId = 0;             // CUDA device index
string srcLang = "zho_Hans";  // FLORES-200 source code (nllb only)
string tgtLang = "eng_Latn";  // FLORES-200 target code (nllb only)
var texts = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model-dir" when i + 1 < args.Length:
            modelDir = args[++i];
            break;
        case "--engine" when i + 1 < args.Length:
            engine = args[++i].ToLowerInvariant();
            break;
        case "--beams" when i + 1 < args.Length:
            beamWidth = int.Parse(args[++i]);
            break;
        case "--quantized":
        case "--int8":
            preferQuantized = true;
            break;
        case "--fp32":
            preferQuantized = false;
            break;
        case "--ep" when i + 1 < args.Length:
            provider = args[++i].ToLowerInvariant();
            break;
        case "--gpu":
            provider = "cuda";
            break;
        case "--device" when i + 1 < args.Length:
            deviceId = int.Parse(args[++i]);
            break;
        case "--src" when i + 1 < args.Length:
            srcLang = args[++i];
            break;
        case "--tgt" when i + 1 < args.Length:
            tgtLang = args[++i];
            break;
        default:
            texts.Add(args[i]);
            break;
    }
}

if (engine != "opus" && engine != "nllb")
{
    Console.Error.WriteLine($"ERROR: unknown --engine '{engine}'. Use 'opus' (default) or 'nllb'.");
    return 2;
}

// opus-mt lives in the app's model dir; NLLB lives beside it in a sibling
// 'models-nllb' directory.
if (modelDir is null)
{
    string opusDir = new AppConfig().ResolveModelDirectory();
    modelDir = engine == "nllb"
        ? Path.Combine(Path.GetDirectoryName(opusDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!, "models-nllb")
        : opusDir;
}

if (texts.Count == 0)
{
    texts.AddRange(new[]
    {
        "你好，世界",
        "今天天气很好，我们去公园散步吧。",
        "设置",
        "文件下载完成后，请重新启动应用程序以完成安装。",
    });
    Console.WriteLine("(no input given — running the built-in sample set)\n");
}

Console.WriteLine($"Engine: {(engine == "nllb" ? $"NLLB-200-distilled-600M ({srcLang}→{tgtLang})" : "opus-mt-zh-en")}");
Console.WriteLine($"Model directory: {modelDir}");
if (engine == "opus")
    Console.WriteLine($"Weights: {(preferQuantized ? "quantized (int8)" : "full-precision (fp32)")}");
Console.WriteLine($"Beams: {(beamWidth?.ToString() ?? "engine default")}");
Console.WriteLine($"Requested provider: {provider}{(provider != "cpu" ? $" (device {deviceId})" : "")}");

// Provider / file-selection / fallback lines are surfaced to stderr as they happen.
void Log(string line) => Console.Error.WriteLine(line);

using ITranslator translator = engine == "nllb"
    ? (ITranslator)new NllbOnnxTranslator(modelDir, numBeams: beamWidth ?? 4,
        executionProvider: provider, gpuDeviceId: deviceId, log: Log,
        sourceLanguage: srcLang, targetLanguage: tgtLang)
    : new LocalOnnxTranslator(modelDir, preferQuantized: preferQuantized, beamWidth: beamWidth,
        executionProvider: provider, gpuDeviceId: deviceId, log: Log);

var sw = Stopwatch.StartNew();
try
{
    await translator.InitializeAsync();
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
{
    // Missing model files or an unknown FLORES-200 language code — both carry
    // actionable messages; no stack trace needed.
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return 2;
}
sw.Stop();

string activeProvider = translator switch
{
    LocalOnnxTranslator o => o.ActiveProvider,
    NllbOnnxTranslator n => n.ActiveProvider,
    _ => provider,
};
Console.WriteLine($"Active provider: {activeProvider}" +
    (activeProvider != provider ? $"  (requested '{provider}', fell back)" : ""));
Console.WriteLine($"Init: {sw.ElapsedMilliseconds} ms\n");

foreach (var text in texts)
{
    var t = Stopwatch.StartNew();
    string result = await translator.TranslateAsync(text);
    t.Stop();
    Console.WriteLine($"ZH: {text}");
    Console.WriteLine($"EN: {result}");
    Console.WriteLine($"    ({t.ElapsedMilliseconds} ms)\n");
}

return 0;
