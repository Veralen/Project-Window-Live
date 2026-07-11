// Test harness for the local ONNX translation engine (Work Package 2).
//
//   dotnet run --project tools/TranslatorCli -- "你好，世界"
//   dotnet run --project tools/TranslatorCli -- --model-dir C:\path\to\models "设置" "文件"
//   dotnet run --project tools/TranslatorCli -- --quantized "你好"   # force int8 weights
//
// Flags: --model-dir <path>, --quantized/--int8 (force int8), --fp32 (force fp32,
// the default). Remaining args are translated in sequence. Prints each translation
// with per-call timing. Exits non-zero with an actionable message when the model is missing.
using System.Diagnostics;
using ScreenTranslator.Core.Config;
using ScreenTranslator.Translation;

string? modelDir = null;
bool preferQuantized = false; // default matches the engine (fp32)
var texts = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model-dir" when i + 1 < args.Length:
            modelDir = args[++i];
            break;
        case "--quantized":
        case "--int8":
            preferQuantized = true;
            break;
        case "--fp32":
            preferQuantized = false;
            break;
        default:
            texts.Add(args[i]);
            break;
    }
}

modelDir ??= new AppConfig().ResolveModelDirectory();

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

Console.WriteLine($"Model directory: {modelDir}");
Console.WriteLine($"Weights: {(preferQuantized ? "quantized (int8)" : "full-precision (fp32)")}\n");

using var translator = new LocalOnnxTranslator(modelDir, preferQuantized: preferQuantized);

var sw = Stopwatch.StartNew();
try
{
    await translator.InitializeAsync();
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return 2;
}
sw.Stop();
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
