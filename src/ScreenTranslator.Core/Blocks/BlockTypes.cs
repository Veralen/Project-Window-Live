using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Ocr;

namespace ScreenTranslator.Core.Blocks;

/// <summary>
/// A paragraph-level group of OCR lines. <paramref name="Text"/> is the lines
/// joined appropriately for the language (space-joined for Latin scripts,
/// directly concatenated for CJK). Bounds is the union of member line bounds,
/// physical pixels, virtual-screen coordinates.
/// </summary>
public sealed record TextBlockGroup(string Text, PixelRect Bounds, IReadOnlyList<OcrLine> Lines);

/// <summary>Groups raw OCR lines into paragraph-level blocks for translation.</summary>
public interface ITextBlockGrouper
{
    IReadOnlyList<TextBlockGroup> Group(OcrRegionResult ocr);
}
