using System;

namespace WindowLive.App.Server;

/// <summary>
/// Thrown when <see cref="GpuDetector"/> cannot find an Nvidia GPU (the only
/// supported vendor in v1 — see docs/window-live-design.md "GPU support").
/// CPU fallback is explicitly unsupported (CLAUDE.md hard rule): this exception
/// is the only acceptable outcome of a failed detection, surfaced to the user
/// as a clear error dialog followed by exit, never a silent fallback.
/// </summary>
internal sealed class NoSupportedGpuException : Exception
{
    public NoSupportedGpuException(string message) : base(message)
    {
    }

    public NoSupportedGpuException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
