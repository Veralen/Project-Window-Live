using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WindowLive.App.Logging;

namespace WindowLive.App.Ocr;

/// <summary>
/// Fetches and caches Tesseract "fast" trained-data files on first use, mirroring
/// how <c>models/</c> is populated on first app run (see CLAUDE.md "Model files").
/// Files land in {AppContext.BaseDirectory}\tessdata\{name}.traineddata — a
/// sibling of the shipped exe, gitignored (see .gitignore's tessdata/ entry) so
/// they're never committed and simply re-downloaded per machine.
///
/// Source: tesseract_fast (https://github.com/tesseract-ocr/tessdata_fast), the
/// same "fast" integerized-LSTM model set the upstream Tesseract docs recommend
/// for the common case over the much larger "best" set — a good size/accuracy
/// trade-off for a locally-run desktop tool.
/// </summary>
internal sealed class TessdataStore
{
    private const string TessdataFastRawUrlTemplate =
        "https://github.com/tesseract-ocr/tessdata_fast/raw/main/{0}.traineddata";

    private readonly HttpClient _http;
    private readonly string _tessdataDir;

    /// <summary>Per-language locks so concurrent EnsureLanguageAsync calls for the
    /// same language (snip + game mode overlapping, or a duplicate hotkey press)
    /// share one download instead of racing partial writes.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public TessdataStore(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tessdataDir = Path.Combine(AppContext.BaseDirectory, "tessdata");
    }

    /// <summary>
    /// Ensures {tessdataName}.traineddata exists under the tessdata directory,
    /// downloading it from tessdata_fast if missing, and returns the tessdata
    /// directory path (the value Tesseract's Engine constructor's "languages"
    /// argument is resolved relative to — see TesseractRecognizer). Concurrent
    /// calls for the same language share one download via a per-language
    /// semaphore; a second caller that arrives after the file has landed
    /// returns immediately without re-downloading.
    /// </summary>
    public async Task<string> EnsureLanguageAsync(string tessdataName, IProgress<int>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tessdataName))
            throw new ArgumentException("Tessdata name must not be empty.", nameof(tessdataName));

        Directory.CreateDirectory(_tessdataDir);
        string finalPath = Path.Combine(_tessdataDir, $"{tessdataName}.traineddata");
        if (File.Exists(finalPath))
            return _tessdataDir;

        SemaphoreSlim gate = _locks.GetOrAdd(tessdataName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check: another caller may have finished the download while we
            // were waiting on the semaphore.
            if (File.Exists(finalPath))
                return _tessdataDir;

            await DownloadAsync(tessdataName, finalPath, progress, ct).ConfigureAwait(false);
            return _tessdataDir;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DownloadAsync(string tessdataName, string finalPath, IProgress<int>? progress, CancellationToken ct)
    {
        string url = string.Format(TessdataFastRawUrlTemplate, tessdataName);
        // Download to a temp file alongside the target, then rename — a crash
        // or cancellation mid-download must never leave a partial/corrupt
        // .traineddata file that a later EnsureLanguageAsync mistakes for complete.
        string tempPath = finalPath + ".download";

        AppLog.Write($"[TessdataStore] downloading {tessdataName}.traineddata from {url}");
        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (FileStream dest = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    readTotal += read;
                    if (totalBytes is > 0)
                        progress?.Report((int)(readTotal * 100 / totalBytes.Value));
                }
            }

            File.Move(tempPath, finalPath, overwrite: true);
            progress?.Report(100);
            AppLog.Write($"[TessdataStore] {tessdataName}.traineddata ready ({new FileInfo(finalPath).Length} bytes)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDeleteTempFile(tempPath);
            AppLog.Write($"[TessdataStore] failed to download {tessdataName}.traineddata: {ex.Message}");
            throw new InvalidOperationException(
                $"Could not download OCR language data for \"{tessdataName}\". Check your internet connection and try again.", ex);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch { /* best-effort cleanup */ }
    }
}
