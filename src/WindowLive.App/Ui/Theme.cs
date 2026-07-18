using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WindowLive.App.Ui;

/// <summary>
/// Single source of design tokens for the "minimal utility" dark UI defined
/// in <c>Design Pack/design_handoff_project_window_1b</c> (README.md is the
/// authority; <c>1b-reference.html</c> is the visual reference). All
/// brushes/effects exposed here are frozen so a single shared instance can be
/// reused everywhere without per-use cloning.
///
/// Shape rule (applies app-wide unless a call site documents an exception):
/// corner radius 0, borders always 1px solid, no gradients/blur. The two
/// documented exceptions in this design are the tray <c>ContextMenu</c> chrome
/// (radius 4px, see <c>TrayMenuStyles.xaml</c>) and the app-icon tiles
/// (12px @96px / 6px @32px / 3px @16px, see <c>scripts/make-icon.ps1</c>).
/// </summary>
internal static class Theme
{
    // ---- Colors ---------------------------------------------------------
    public static readonly Color WindowBgColor = Color.FromRgb(0x12, 0x12, 0x12);
    public static readonly Color PopupBgColor = Color.FromRgb(0x16, 0x16, 0x16);
    public static readonly Color DeepBgColor = Color.FromRgb(0x0e, 0x0e, 0x0e);
    public static readonly Color TileBgColor = Color.FromRgb(0x1c, 0x1c, 0x1c);
    public static readonly Color HoverBgColor = Color.FromRgb(0x1f, 0x1f, 0x1f);
    public static readonly Color BorderColor = Color.FromRgb(0x2c, 0x2c, 0x2c);
    public static readonly Color DividerColor = Color.FromRgb(0x24, 0x24, 0x24);
    public static readonly Color IconBorderColor = Color.FromRgb(0x2e, 0x2e, 0x2e);
    public static readonly Color TextPrimaryColor = Color.FromRgb(0xf2, 0xf2, 0xf2);
    public static readonly Color TextSecondaryColor = Color.FromRgb(0xa8, 0xa8, 0xa8);
    public static readonly Color TextTrayColor = Color.FromRgb(0xc9, 0xc9, 0xc9);
    public static readonly Color TextMutedColor = Color.FromRgb(0x5c, 0x5c, 0x5c);
    public static readonly Color AccentColor = Color.FromRgb(0x47, 0xd6, 0xa2);

    /// <summary>Brighter mint used for hover on accent ("mint link") text buttons. Not an explicit README token; derived to read as a lightened Accent.</summary>
    public static readonly Color AccentBrightColor = Color.FromRgb(0x6e, 0xe7, 0xbb);

    public static readonly Color ErrorColor = Color.FromRgb(0xd6, 0x68, 0x5c);

    // ---- Brushes (frozen) -------------------------------------------------
    public static readonly Brush WindowBg = Freeze(new SolidColorBrush(WindowBgColor));
    public static readonly Brush PopupBg = Freeze(new SolidColorBrush(PopupBgColor));
    public static readonly Brush DeepBg = Freeze(new SolidColorBrush(DeepBgColor));
    public static readonly Brush TileBg = Freeze(new SolidColorBrush(TileBgColor));
    public static readonly Brush HoverBg = Freeze(new SolidColorBrush(HoverBgColor));
    public static readonly Brush Border = Freeze(new SolidColorBrush(BorderColor));
    public static readonly Brush Divider = Freeze(new SolidColorBrush(DividerColor));
    public static readonly Brush IconBorder = Freeze(new SolidColorBrush(IconBorderColor));
    public static readonly Brush TextPrimary = Freeze(new SolidColorBrush(TextPrimaryColor));
    public static readonly Brush TextSecondary = Freeze(new SolidColorBrush(TextSecondaryColor));
    public static readonly Brush TextTray = Freeze(new SolidColorBrush(TextTrayColor));
    public static readonly Brush TextMuted = Freeze(new SolidColorBrush(TextMutedColor));
    public static readonly Brush Accent = Freeze(new SolidColorBrush(AccentColor));
    public static readonly Brush AccentBright = Freeze(new SolidColorBrush(AccentBrightColor));

    /// <summary>Mint at 4% alpha (rgba(71,214,162,.04) — snip-rectangle fill). 0.04 * 255 rounds to 0x0A.</summary>
    public static readonly Brush AccentFill04 = Freeze(new SolidColorBrush(Color.FromArgb(0x0A, 0x47, 0xd6, 0xa2)));

    public static readonly Brush Error = Freeze(new SolidColorBrush(ErrorColor));

    /// <summary>Text color used on top of an Accent-filled surface (selected segment, icon glyph on tile background context) — same as DeepBg (#0e0e0e).</summary>
    public static readonly Brush TextOnAccent = DeepBg;

    // ---- Fonts ------------------------------------------------------------
    public static readonly FontFamily UiFontFamily = new("Segoe UI");
    public static readonly FontFamily MonoFontFamily = new("Consolas, Cascadia Mono, Courier New");

    // ---- Effects ------------------------------------------------------------
    /// <summary>
    /// Popup shadow per README: <c>0 12px 32px rgba(0,0,0,.6)</c>. WPF's
    /// DropShadowEffect models a directional offset rather than a symmetric
    /// CSS box-shadow, so a straight-down cast (Direction 270) with
    /// ShadowDepth 12 approximates the "0 12" (x=0, y=12) offset.
    /// </summary>
    public static DropShadowEffect CreatePopupShadow() => Freeze(new DropShadowEffect
    {
        Color = Colors.Black,
        Direction = 270,
        ShadowDepth = 12,
        BlurRadius = 32,
        Opacity = 0.6,
    });

    /// <summary>
    /// Section label style per README: mono, 10px, semibold, uppercase,
    /// #5c5c5c. WPF's TextBlock has no letter-spacing property, so the
    /// design's 0.08em tracking is not reproduced — documented WPF fidelity
    /// compromise. Pass the raw label text; it is upper-cased here.
    /// </summary>
    public static TextBlock CreateSectionLabel(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontFamily = MonoFontFamily,
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Foreground = TextMuted,
    };

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
            freezable.Freeze();
        return freezable;
    }
}
