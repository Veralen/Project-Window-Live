using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ScreenTranslator.Core.Geometry;
using ScreenTranslator.Core.Placement;

namespace ScreenTranslator.App.Rendering;

/// <summary>
/// <see cref="ITextMeasurer"/> backed by WPF <see cref="FormattedText"/>.
/// Placement math operates in physical pixels, so we measure at
/// pixelsPerDip = 1.0 (1 DIP == 1 physical px) and treat font sizes as physical px.
/// </summary>
internal sealed class WpfTextMeasurer : ITextMeasurer
{
    private static readonly Typeface Typeface = new(
        new FontFamily(LabelStyle.FontFamily),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public PixelSize Measure(string text, double fontSizePx, double maxWidthPx)
    {
        var ft = new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface,
            fontSizePx,
            Brushes.White,
            pixelsPerDip: 1.0);

        if (maxWidthPx > 0)
            ft.MaxTextWidth = maxWidthPx;

        return new PixelSize(ft.Width, ft.Height);
    }
}
