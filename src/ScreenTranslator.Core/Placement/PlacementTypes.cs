using ScreenTranslator.Core.Blocks;
using ScreenTranslator.Core.Geometry;

namespace ScreenTranslator.Core.Placement;

/// <summary>A source block paired with its translation, ready for placement.</summary>
public sealed record TranslatedBlock(TextBlockGroup Source, string TranslatedText);

/// <summary>
/// A translated label with a decided on-screen position. LabelBounds is the full
/// chip rectangle (text + LabelStyle padding), physical pixels, virtual-screen
/// coordinates. FontSize is in physical pixels (App converts to DIPs per monitor).
/// </summary>
public sealed record PlacedLabel(TranslatedBlock Block, PixelRect LabelBounds, double FontSize);

/// <summary>
/// Measures rendered text so the placement engine can size labels without a UI
/// dependency. Implementations: WPF FormattedText in App; a deterministic fake in tests.
/// Returns the size of <paramref name="text"/> rendered at <paramref name="fontSizePx"/>,
/// wrapped at <paramref name="maxWidthPx"/> (text only, excluding chip padding).
/// </summary>
public interface ITextMeasurer
{
    PixelSize Measure(string text, double fontSizePx, double maxWidthPx);
}

/// <summary>
/// Decides where each translated label goes. See docs/architecture.md §Placement
/// for the full rules (aspect-ratio direction choice, flip, clamp, collision nudge).
/// </summary>
public interface ILabelPlacer
{
    /// <param name="blocks">Translated blocks from one capture.</param>
    /// <param name="monitorBounds">
    /// Work area of the monitor containing the capture, physical pixels,
    /// virtual-screen coordinates. Labels must stay inside it.
    /// </param>
    IReadOnlyList<PlacedLabel> Place(
        IReadOnlyList<TranslatedBlock> blocks,
        PixelRect monitorBounds,
        ITextMeasurer measurer);
}

/// <summary>Shared visual constants so placement math and App rendering agree exactly.</summary>
public static class LabelStyle
{
    /// <summary>Horizontal chip padding on each side, physical px.</summary>
    public const double PaddingX = 8;
    /// <summary>Vertical chip padding on each side, physical px.</summary>
    public const double PaddingY = 5;
    /// <summary>Gap between a source block and its label, physical px.</summary>
    public const double Gap = 6;
    /// <summary>Chip corner radius, physical px.</summary>
    public const double CornerRadius = 6;
    public const double MinFontSize = 11;
    public const double MaxFontSize = 40;
    /// <summary>Font family the App renders labels with; measurer fakes should assume it.</summary>
    public const string FontFamily = "Segoe UI";
}
