using System;
using System.IO;
using System.Text;

namespace WindowLive.App.Logging;

/// <summary>
/// Process-wide file logger. The shipped app is a WinExe (no console), so the
/// existing Console/Debug writes go nowhere on an end-user machine; this appends
/// the same lines to %LOCALAPPDATA%\WindowLive\logs\app-YYYYMMDD.log so
/// field failures leave a trace. Thread-safe, flushes per line, and — by design —
/// never throws (all IO is wrapped): logging must not be able to crash the app.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>The resolved log-file path, or a placeholder before initialization.</summary>
    public static string LogPath => _path ?? "(log file not available)";

    /// <summary>
    /// Resolves today's log file, creating the directory as needed, and best-effort
    /// deletes log files older than 7 days. Safe to call once at startup.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowLive", "logs");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
            PurgeOld(dir);
        }
        catch { /* logging must never throw */ }
    }

    private static void PurgeOld(string dir)
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-7);
            foreach (string f in Directory.GetFiles(dir, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff)
                        File.Delete(f);
                }
                catch { /* best-effort per file */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Appends one already-formatted line (UTF-8, flushed) under a lock.</summary>
    public static void Write(string line)
    {
        try
        {
            string? path = _path;
            if (path is null) return;
            lock (Gate)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* logging must never throw */ }
    }
}
