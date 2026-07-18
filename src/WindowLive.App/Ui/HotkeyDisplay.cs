using System;
using System.Text;

namespace WindowLive.App.Ui;

/// <summary>
/// Renders canonical hotkey strings — as produced/accepted by
/// <see cref="WindowLive.App.Hotkeys.HotkeyManager.Format"/> /
/// <see cref="WindowLive.App.Hotkeys.HotkeyManager.TryParse"/>, e.g.
/// "Ctrl+Shift+T" — as the compact glyph form used by the tray menu in
/// <c>design_handoff_project_window_1b</c> section 4 (e.g. "^⇧T").
/// </summary>
internal static class HotkeyDisplay
{
    /// <summary>
    /// "Ctrl+Shift+T" -&gt; "^⇧T". Modifier tokens map to Ctrl→^, Shift→⇧,
    /// Win→⊞; Alt has no single-glyph tray convention in the design, so it's
    /// kept as the literal "Alt+" prefix. The trailing main key passes
    /// through unchanged. Unknown tokens also pass through unchanged.
    /// </summary>
    public static string ToGlyphs(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
            return string.Empty;

        string[] parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new StringBuilder();
        foreach (string part in parts)
        {
            sb.Append(part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => "^",
                "SHIFT" => "⇧",
                "ALT" => "Alt+",
                "WIN" or "WINDOWS" => "⊞",
                _ => part,
            });
        }
        return sb.ToString();
    }
}
