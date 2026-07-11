using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using CoreOcrLine = ScreenTranslator.Core.Ocr.OcrLine;
using CoreOcrWord = ScreenTranslator.Core.Ocr.OcrWord;

namespace ScreenTranslator.App.Ocr;

/// <summary>
/// <see cref="IOcrService"/> backed by Windows.Media.Ocr (WinRT). Resolves the
/// recognizer language by BCP-47 prefix match against installed packs, then
/// user-profile fallback. Returns line bounds already offset into virtual-screen
/// physical-pixel coordinates (docs/architecture.md §Coordinate spaces).
/// </summary>
internal sealed class WindowsOcrService : IOcrService
{
    private readonly Core.Config.AppConfig _config;
    private readonly Action<string, string> _notify; // (title, message)
    private string? _notifiedLanguage; // last configured language we warned about

    // Reads OcrLanguage from live config on every call so a Settings language
    // change takes effect on the next snip without rebuilding the service.
    private string ConfiguredLanguage => _config.OcrLanguage;

    public WindowsOcrService(Core.Config.AppConfig config, Action<string, string> notify)
    {
        _config = config;
        _notify = notify;
    }

    public async Task<OcrRegionResult> RecognizeAsync(CapturedRegion region, CancellationToken ct = default)
    {
        string configuredLanguage = ConfiguredLanguage;
        OcrEngine? engine = ResolveEngine();
        if (engine is null)
        {
            _notify("OCR unavailable",
                $"No OCR language pack matched '{configuredLanguage}' and no user-profile engine is available. " +
                "Install a language pack under Settings > Time & Language > Language.");
            return new OcrRegionResult(Array.Empty<CoreOcrLine>(), configuredLanguage);
        }

        string engineTag = engine.RecognizerLanguage.LanguageTag;

        // A user-profile fallback engine in a language that doesn't match the
        // configured one silently misreads the source text (e.g. en-US OCR over
        // Chinese produces Latin garbage that then gets "translated"). Surface it
        // once per configured language instead of failing quietly.
        if (_notifiedLanguage != configuredLanguage && !Bcp47PrefixMatch(configuredLanguage, engineTag))
        {
            _notifiedLanguage = configuredLanguage;
            _notify("OCR language pack missing",
                $"No '{configuredLanguage}' OCR pack is installed — using '{engineTag}' instead, " +
                "so the snipped text will likely be misread. Run scripts/install-ocr-language.ps1 " +
                "as administrator, then restart the app.");
        }

        // Downscale for OCR if the crop exceeds MaxImageDimension; scale rects back after.
        int maxDim = (int)OcrEngine.MaxImageDimension;
        double scale = 1.0;
        byte[] pixels = region.PixelsBgra32;
        int w = region.PixelWidth, h = region.PixelHeight;
        if (Math.Max(w, h) > maxDim && maxDim > 0)
        {
            scale = (double)maxDim / Math.Max(w, h);
            int nw = Math.Max(1, (int)Math.Round(w * scale));
            int nh = Math.Max(1, (int)Math.Round(h * scale));
            pixels = DownscaleBgra(region.PixelsBgra32, w, h, nw, nh);
            w = nw; h = nh;
        }

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);

        OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);

        double invScale = scale <= 0 ? 1.0 : 1.0 / scale;
        double originX = region.ScreenRegion.X;
        double originY = region.ScreenRegion.Y;

        var lines = new List<CoreOcrLine>();
        foreach (var line in result.Lines)
        {
            var words = new List<CoreOcrWord>();
            PixelRect lineBounds = default;
            bool first = true;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                var wb = new PixelRect(
                    r.X * invScale + originX,
                    r.Y * invScale + originY,
                    r.Width * invScale,
                    r.Height * invScale);
                words.Add(new CoreOcrWord(word.Text, wb));
                lineBounds = first ? wb : lineBounds.Union(wb);
                first = false;
            }
            if (words.Count == 0) continue;
            lines.Add(new CoreOcrLine(line.Text, lineBounds, words));
        }

        return new OcrRegionResult(lines, engineTag);
    }

    /// <summary>
    /// Tag of the installed OCR pack that prefix-matches <paramref name="configured"/>,
    /// or null when none does (the service would fall back to the user-profile engine).
    /// Used by the Settings debug panel.
    /// </summary>
    internal static string? FindInstalledMatch(string configured)
    {
        try
        {
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                if (Bcp47PrefixMatch(configured, lang.LanguageTag))
                    return lang.LanguageTag;
        }
        catch { /* WinRT unavailable — treat as no match */ }
        return null;
    }

    /// <summary>Tag of the user-profile fallback OCR engine, or null when none exists.</summary>
    internal static string? UserProfileEngineTag()
    {
        try { return OcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage.LanguageTag; }
        catch { return null; }
    }

    private OcrEngine? ResolveEngine()
    {
        // 1) Prefix match the configured tag against installed recognizer languages.
        foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
        {
            if (Bcp47PrefixMatch(ConfiguredLanguage, lang.LanguageTag))
            {
                var e = OcrEngine.TryCreateFromLanguage(lang);
                if (e is not null) return e;
            }
        }
        // 2) User-profile fallback.
        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    /// <summary>
    /// True if the two BCP-47 tags share a subtag prefix (case-insensitive),
    /// e.g. "zh-Hans" matches "zh-Hans-CN"; "en" matches "en-US".
    /// </summary>
    private static bool Bcp47PrefixMatch(string configured, string available)
    {
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(available))
            return false;
        var a = configured.Split('-');
        var b = available.Split('-');
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private static byte[] DownscaleBgra(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        int srcStride = sw * 4;
        int dstStride = dw * 4;
        for (int y = 0; y < dh; y++)
        {
            int sy = Math.Min(sh - 1, (int)((y + 0.5) * sh / dh));
            int srcRow = sy * srcStride;
            int dstRow = y * dstStride;
            for (int x = 0; x < dw; x++)
            {
                int sx = Math.Min(sw - 1, (int)((x + 0.5) * sw / dw));
                int si = srcRow + sx * 4;
                int di = dstRow + x * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
        return dst;
    }
}
