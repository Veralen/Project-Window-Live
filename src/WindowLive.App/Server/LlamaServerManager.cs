using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WindowLive.App.Logging;
using WindowLive.App.Native;
using WindowLive.Core.Config;

namespace WindowLive.App.Server;

/// <summary>
/// Owns the llama-server child process end to end: locating the GPU-appropriate
/// binary, launching it with the flags from docs/window-live-design.md
/// "Server startup", capturing its output, waiting for it to report ready, and
/// guaranteeing it dies with this process (Dispose kill + a Job Object safety
/// net for the crash case). No UI/WPF types — wired up from App.xaml.cs.
///
/// Binary resolution checks two layouts, in order:
///   1. Shipped layout: {AppContext.BaseDirectory}\{binaryName} — the binary sits
///      right beside WindowLive.exe in a distributed zip.
///   2. Dev layout: repo-root runtime/llama-cuda/{binaryName} — the 700+ MB
///      llama.cpp binaries are gitignored and not copied into bin output, so a
///      dev build walks up from BaseDirectory (typically deep under
///      bin/Debug/net10.0.../win-x64/) looking for a runtime/llama-cuda
///      directory. Whichever layout is used, the models directory (mmproj
///      download target + LLAMA_CACHE) is resolved alongside it: {exeDir}\models
///      for the shipped layout, {repoRoot}\models for the dev layout — always a
///      "models" folder, consistent with .gitignore's models/ entry.
///
/// mmproj route: llama-server's `--hf-repo` can auto-fetch *a* multimodal
/// projector via --mmproj-auto (default on), but per the llama.cpp server docs
/// (tools/server/README.md, "3 possible sources for model files" / mmproj-auto
/// section, verified July 2026) that auto-selection has no documented way to
/// pin the specific mmproj filename our AppConfig.MmprojFile names, and
/// --mmproj-url's caching behavior is undocumented. docs/window-live-design.md
/// is explicit that this app downloads that named file once and passes it via
/// --mmproj — so that's the route implemented here: HttpClient GETs
/// https://huggingface.co/{ModelRepo}/resolve/main/{MmprojFile} into models/
/// (skipped if already present) with progress reporting, then the local path is
/// passed via --mmproj, and --no-mmproj-auto is added defensively so
/// llama-server never races our download with its own auto-fetch.
/// </summary>
internal sealed class LlamaServerManager : IDisposable
{
    /// <summary>Verified against llama.cpp tools/server/README.md, master branch, July 2026:
    /// GET /health returns HTTP 503 while the model loads, HTTP 200 (`{"status":"ok"}`) once ready.</summary>
    private const int HealthPollIntervalMs = 300;

    private const int StderrTailMaxLines = 50;

    private readonly AppConfig _config;
    private Process? _process;
    private IntPtr _jobHandle = IntPtr.Zero;
    private readonly object _tailLock = new();
    private readonly List<string> _tail = new();

    /// <summary>Fired for every stdout/stderr line the child process writes.</summary>
    public event Action<string>? OutputLine;

    /// <summary>Fired with a 0-100 percentage whenever a model/mmproj download progress line is parsed.</summary>
    public event Action<int>? DownloadProgressChanged;

    public bool IsRunning => _process is { HasExited: false };

    public LlamaServerManager(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Resolves the GPU-specific binary, ensures the mmproj file is present locally,
    /// and launches llama-server as a child process with output redirected. Throws
    /// <see cref="NoSupportedGpuException"/> if no supported GPU is present (never
    /// falls back to CPU), <see cref="FileNotFoundException"/> if the expected
    /// server binary is missing from the install directory, or an
    /// <see cref="InvalidOperationException"/> if the process fails to start.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_process is not null)
            throw new InvalidOperationException("LlamaServerManager.StartAsync was already called.");

        // Throws NoSupportedGpuException — never attempt a CPU fallback (CLAUDE.md hard rule).
        string binaryName = GpuDetector.SelectServerBinaryName();

        (string serverExePath, string modelsDir) = ResolveServerLayout(binaryName);
        Directory.CreateDirectory(modelsDir);

        string? mmprojLocalPath = await EnsureMmprojDownloadedAsync(modelsDir, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = serverExePath,
            WorkingDirectory = modelsDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // --- docs/window-live-design.md "Server startup" ---
        psi.ArgumentList.Add("--hf-repo");
        psi.ArgumentList.Add(_config.ModelRepo);
        psi.ArgumentList.Add("--hf-file");
        psi.ArgumentList.Add(_config.ModelFile);
        if (mmprojLocalPath is not null)
        {
            psi.ArgumentList.Add("--mmproj");
            psi.ArgumentList.Add(mmprojLocalPath);
            psi.ArgumentList.Add("--no-mmproj-auto"); // we already resolved it ourselves; don't race llama-server's own auto-fetch
        }
        // 4096 (not the original 512): image transcription is part of every
        // translation now, and a realistic snip/chat region costs ~1000 image
        // tokens (live 400 exceed_context_size_error at ctx 512 with a 569x521
        // snip). --parallel 2 bounds the slot count (default 4) so the KV
        // allocation stays modest; two slots cover snip + game mode overlapping.
        psi.ArgumentList.Add("--ctx-size");
        psi.ArgumentList.Add("4096");
        psi.ArgumentList.Add("--parallel");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(_config.ServerPort.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-ngl");
        psi.ArgumentList.Add("99");
        // Belt-and-braces thinking suppression for this thinking model (Qwen3.5):
        // the per-request "enable_thinking": false on the image transcription call
        // (LlamaClient.TranscribeImageAsync) only applies to /v1/chat/completions;
        // this server-wide flag caps the reasoning budget at 0 for every endpoint,
        // including /completion (the text-translation path), which has no
        // per-request thinking toggle of its own.
        psi.ArgumentList.Add("--reasoning-budget");
        psi.ArgumentList.Add("0");

        // Main-model downloads triggered by --hf-repo/--hf-file are cached by llama-server
        // itself under the directory named by LLAMA_CACHE (tools/server/README.md, "3
        // possible sources for model files") — point that at the resolved models/ dir
        // (see ResolveServerLayout) so nothing lands under the user profile or gets
        // re-downloaded on next launch.
        psi.EnvironmentVariables["LLAMA_CACHE"] = modelsDir;

        AppLog.Write($"[LlamaServerManager] launching \"{serverExePath}\" {string.Join(' ', psi.ArgumentList)}");

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => OnChildLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnChildLine(e.Data);

        if (!_process.Start())
            throw new InvalidOperationException("Failed to start the llama-server process (Process.Start returned false).");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        AssignToJobObject(_process);
    }

    /// <summary>
    /// Resolves the server binary path and its accompanying models directory,
    /// preferring the shipped (beside-exe) layout and falling back to the
    /// gitignored dev layout (repo-root runtime/llama-cuda/). Throws
    /// <see cref="FileNotFoundException"/> if neither layout has the binary.
    /// </summary>
    private static (string ServerExePath, string ModelsDir) ResolveServerLayout(string binaryName)
    {
        string exeDir = AppContext.BaseDirectory;
        string shippedPath = Path.Combine(exeDir, binaryName);
        if (File.Exists(shippedPath))
        {
            string shippedModelsDir = Path.Combine(exeDir, "models");
            AppLog.Write($"[LlamaServerManager] binary layout: shipped (\"{shippedPath}\").");
            return (shippedPath, shippedModelsDir);
        }

        for (DirectoryInfo? dir = new DirectoryInfo(exeDir); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "runtime", "llama-cuda", binaryName);
            if (!File.Exists(candidate))
                continue;

            // The dev llama.cpp binaries live under runtime/ (gitignored, not copied to
            // bin output), but models/ must still resolve to the repo root — same
            // directory .gitignore's "models/" entry refers to — not a models/ folder
            // nested under runtime/llama-cuda/ or the build output.
            string repoRootModelsDir = Path.Combine(dir.FullName, "models");
            AppLog.Write($"[LlamaServerManager] binary layout: dev (\"{candidate}\"), repo root \"{dir.FullName}\".");
            return (candidate, repoRootModelsDir);
        }

        throw new FileNotFoundException(
            $"Expected the llama.cpp server binary \"{binaryName}\" either beside the exe " +
            $"(\"{shippedPath}\", shipped layout) or under a \"runtime\\llama-cuda\\\" directory " +
            $"walking up from \"{exeDir}\" (dev layout), but it was not found in either location. " +
            "For a shipped install, reinstall/re-extract the zip. For a dev build, populate " +
            "runtime/llama-cuda/ at the repo root.",
            binaryName);
    }

    /// <summary>
    /// Polls GET http://127.0.0.1:{port}/health until it returns HTTP 200, the
    /// process exits (fails fast with the captured stderr/stdout tail), or the
    /// timeout elapses.
    /// </summary>
    public async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_process is null)
            throw new InvalidOperationException("StartAsync must be called before WaitForReadyAsync.");

        string url = $"http://127.0.0.1:{_config.ServerPort}/health";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        DateTime deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"llama-server exited (code {_process.ExitCode}) before becoming ready.\n{GetTail()}");
            }

            try
            {
                using HttpResponseMessage resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                    return;
                // Anything else (503 while loading, etc.) — keep polling.
            }
            catch (HttpRequestException)
            {
                // Not listening yet (port not bound / connection refused) — keep polling.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-request timeout (slow health check), not our overall deadline — keep polling.
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"llama-server did not become ready within {timeout}.\n{GetTail()}");
            }

            await Task.Delay(HealthPollIntervalMs, ct).ConfigureAwait(false);
        }
    }

    private void OnChildLine(string? line)
    {
        if (line is null)
            return;

        AppLog.Write($"[llama-server] {line}");

        lock (_tailLock)
        {
            _tail.Add(line);
            if (_tail.Count > StderrTailMaxLines)
                _tail.RemoveAt(0);
        }

        OutputLine?.Invoke(line);

        int? pct = StdoutProgressParser.TryParseProgressPercent(line);
        if (pct.HasValue)
            DownloadProgressChanged?.Invoke(pct.Value);
    }

    private string GetTail()
    {
        lock (_tailLock)
            return string.Join(Environment.NewLine, _tail);
    }

    /// <summary>
    /// Downloads AppConfig.MmprojFile from the model repo into modelsDir if it
    /// isn't already there, reporting progress via <see cref="DownloadProgressChanged"/>.
    /// Returns the local path, or null if no mmproj is configured.
    /// </summary>
    private async Task<string?> EnsureMmprojDownloadedAsync(string modelsDir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.MmprojFile))
            return null;

        string localPath = Path.Combine(modelsDir, _config.MmprojFile);
        if (File.Exists(localPath))
        {
            AppLog.Write($"[LlamaServerManager] mmproj already present: \"{localPath}\"");
            return localPath;
        }

        string url = $"https://huggingface.co/{_config.ModelRepo}/resolve/main/{_config.MmprojFile}";
        AppLog.Write($"[LlamaServerManager] downloading mmproj from {url}");
        OnChildLine($"[WindowLive] Downloading vision projector: {_config.MmprojFile}");

        string tempPath = localPath + ".part";
        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        using (HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long? totalBytes = response.Content.Headers.ContentLength;

            await using Stream httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int lastPct = -1;
                int read;
                while ((read = await httpStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    readTotal += read;

                    if (totalBytes is > 0)
                    {
                        int pct = (int)Math.Clamp(readTotal * 100 / totalBytes.Value, 0, 100);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            DownloadProgressChanged?.Invoke(pct);
                        }
                    }
                }
            }
        }

        File.Move(tempPath, localPath, overwrite: true);
        AppLog.Write($"[LlamaServerManager] mmproj download complete: \"{localPath}\"");
        return localPath;
    }

    /// <summary>
    /// Best-effort: puts the child in a Windows Job Object with
    /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE so it is force-killed by the OS the moment
    /// the job handle closes — i.e. even if this process crashes/is killed and never
    /// runs <see cref="Dispose"/>. Failure here is logged, not thrown: Dispose()'s
    /// explicit Process.Kill remains the primary termination path.
    /// </summary>
    private void AssignToJobObject(Process process)
    {
        IntPtr job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            AppLog.Write("[LlamaServerManager] CreateJobObject failed; relying solely on Dispose() to kill the child.");
            return;
        }

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        int length = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr infoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);

            if (!NativeMethods.SetInformationJobObject(job, NativeMethods.JOBOBJECTINFOCLASS.ExtendedLimitInformation, infoPtr, (uint)length))
            {
                AppLog.Write($"[LlamaServerManager] SetInformationJobObject failed (err={Marshal.GetLastWin32Error()}).");
                return;
            }

            if (!NativeMethods.AssignProcessToJobObject(job, process.Handle))
            {
                AppLog.Write($"[LlamaServerManager] AssignProcessToJobObject failed (err={Marshal.GetLastWin32Error()}).");
                return;
            }

            // Ownership transfers to the field; closed in Dispose().
            _jobHandle = job;
            job = IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
            if (job != IntPtr.Zero)
                NativeMethods.CloseHandle(job);
        }
    }

    /// <summary>Kills the child process (if still running) and releases the Job Object handle.</summary>
    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                AppLog.Write("[LlamaServerManager] killing llama-server child process.");
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[LlamaServerManager] error killing child process: {ex.Message}");
        }
        finally
        {
            _process?.Dispose();
            _process = null;

            if (_jobHandle != IntPtr.Zero)
            {
                // Closing the job handle would also kill any surviving child via
                // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE — belt-and-suspenders vs. the
                // explicit Kill() above, which is why this is unconditional.
                NativeMethods.CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }
    }
}
