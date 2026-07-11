using ScreenTranslator.Core.Geometry;

namespace ScreenTranslator.Core.Ocr;

/// <summary>A single OCR'd word. Bounds are physical pixels, virtual-screen coordinates.</summary>
public sealed record OcrWord(string Text, PixelRect Bounds);

/// <summary>A single OCR'd line. Bounds are physical pixels, virtual-screen coordinates.</summary>
public sealed record OcrLine(string Text, PixelRect Bounds, IReadOnlyList<OcrWord> Words);

/// <summary>
/// OCR output for one captured region. <paramref name="LanguageTag"/> is the BCP-47
/// tag of the OCR engine actually used (e.g. "zh-Hans-CN") — grouping uses it to
/// decide how to join lines (CJK scripts join without spaces).
/// </summary>
public sealed record OcrRegionResult(IReadOnlyList<OcrLine> Lines, string LanguageTag);

/// <summary>
/// A captured screen region: raw 32-bit BGRA pixels (row-major, no stride padding).
/// <paramref name="ScreenRegion"/> is where those pixels came from on the virtual
/// screen, in physical pixels — PixelWidth/PixelHeight always match its size 1:1.
/// </summary>
public sealed record CapturedRegion(
    byte[] PixelsBgra32,
    int PixelWidth,
    int PixelHeight,
    PixelRect ScreenRegion);

/// <summary>
/// OCR service. Implementations must return bounds already offset into
/// virtual-screen physical-pixel coordinates (image coords + ScreenRegion origin).
/// </summary>
public interface IOcrService
{
    Task<OcrRegionResult> RecognizeAsync(CapturedRegion region, CancellationToken ct = default);
}
