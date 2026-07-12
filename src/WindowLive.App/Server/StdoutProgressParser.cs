using System;
using System.Text.RegularExpressions;

namespace WindowLive.App.Server;

/// <summary>
/// Parses llama-server's Hugging Face model-download progress lines. Verified
/// against llama.cpp's common/download.cpp (ProgressBar::on_update, master branch,
/// July 2026) which prints, per update:
///   "Downloading {filename} {unicode-box-drawing-bar}{pct,4}%\x1b[K"
/// prefixed by a bare '\r' (no trailing '\n'). .NET's redirected-output line
/// reader treats a bare '\r' as a line terminator, so each progress tick still
/// arrives as one line via Process.OutputDataReceived/ErrorDataReceived — this
/// class only has to strip the ANSI "clear to end of line" sequence and pull the
/// percentage out of that already-split line. Kept separate from
/// LlamaServerManager (a static, dependency-free parser) so it is unit-testable
/// on its own.
/// </summary>
internal static class StdoutProgressParser
{
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex ProgressLine = new(
        @"^Downloading\s+\S.*?(?<pct>\d{1,3})\s*%",
        RegexOptions.Compiled);

    /// <summary>
    /// Attempts to parse a download-progress percentage (0-100) out of one line of
    /// llama-server output. Returns null for any line that isn't a progress update.
    /// </summary>
    public static int? TryParseProgressPercent(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        string clean = AnsiEscape.Replace(line, string.Empty).Trim();
        Match m = ProgressLine.Match(clean);
        if (!m.Success)
            return null;

        if (!int.TryParse(m.Groups["pct"].Value, out int pct))
            return null;

        return Math.Clamp(pct, 0, 100);
    }
}
