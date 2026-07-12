using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WindowLive.App.Logging;
using WindowLive.App.Native;

namespace WindowLive.App.Server;

/// <summary>Known GPU vendors, classified by PCI vendor id.</summary>
internal enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
    Microsoft,
    Other,
}

/// <summary>One enumerated DXGI adapter, already classified.</summary>
internal sealed record GpuAdapterInfo(string Description, GpuVendor Vendor, uint VendorId, uint DeviceId, bool IsSoftware);

/// <summary>
/// Enumerates display adapters via DXGI (CreateDXGIFactory1 / IDXGIFactory1.EnumAdapters1
/// / DXGI_ADAPTER_DESC1) and picks which llama-server binary to launch. See
/// docs/window-live-design.md "GPU support": v1 ships Nvidia-only (CUDA build); AMD
/// and Intel are v2 (Vulkan build, not shipped yet) and — per the design brief — must
/// currently produce the same "no supported GPU" outcome as having no GPU at all.
/// No CPU fallback path exists or should ever be added here (CLAUDE.md hard rule).
/// </summary>
internal static class GpuDetector
{
    public const uint VendorIdNvidia = 0x10DE;
    public const uint VendorIdAmd = 0x1002;
    public const uint VendorIdIntel = 0x8086;
    public const uint VendorIdMicrosoft = 0x1414; // Microsoft Basic Render Driver

    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    /// <summary>
    /// Enumerates all DXGI adapters on the system, in DXGI's own enumeration order
    /// (primary-display adapter first). Never throws for "no adapters" — an empty
    /// list is a valid (if unusual) result; throws only if DXGI itself is unusable.
    /// </summary>
    public static IReadOnlyList<GpuAdapterInfo> EnumerateAdapters()
    {
        var result = new List<GpuAdapterInfo>();

        Guid iidFactory1 = typeof(NativeMethods.IDXGIFactory1).GUID;
        int hrCreate = NativeMethods.CreateDXGIFactory1(ref iidFactory1, out NativeMethods.IDXGIFactory1 factory);
        if (hrCreate != 0 || factory is null)
        {
            throw new InvalidOperationException(
                $"CreateDXGIFactory1 failed (hr=0x{hrCreate:X8}). DXGI is required for GPU detection.");
        }

        try
        {
            uint index = 0;
            while (true)
            {
                int hrEnum = factory.EnumAdapters1(index, out NativeMethods.IDXGIAdapter1 adapter);
                if (hrEnum == DXGI_ERROR_NOT_FOUND)
                    break; // normal end-of-list
                if (hrEnum != 0 || adapter is null)
                {
                    AppLog.Write($"[GpuDetector] EnumAdapters1({index}) returned hr=0x{hrEnum:X8}; stopping enumeration.");
                    break;
                }

                index++;
                try
                {
                    int hrDesc = adapter.GetDesc1(out NativeMethods.DXGI_ADAPTER_DESC1 desc);
                    if (hrDesc != 0)
                    {
                        AppLog.Write($"[GpuDetector] GetDesc1 failed (hr=0x{hrDesc:X8}) for adapter #{index - 1}; skipping.");
                        continue;
                    }

                    bool isSoftware = (desc.Flags & NativeMethods.DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                    var vendor = ClassifyVendor(desc.VendorId);
                    result.Add(new GpuAdapterInfo(desc.Description, vendor, desc.VendorId, desc.DeviceId, isSoftware));
                }
                finally
                {
                    if (Marshal.IsComObject(adapter))
                        Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            if (Marshal.IsComObject(factory))
                Marshal.ReleaseComObject(factory);
        }

        foreach (var a in result)
            AppLog.Write($"[GpuDetector] adapter: \"{a.Description}\" vendor=0x{a.VendorId:X4} ({a.Vendor}) device=0x{a.DeviceId:X4} software={a.IsSoftware}");

        return result;
    }

    private static GpuVendor ClassifyVendor(uint vendorId) => vendorId switch
    {
        VendorIdNvidia => GpuVendor.Nvidia,
        VendorIdAmd => GpuVendor.Amd,
        VendorIdIntel => GpuVendor.Intel,
        VendorIdMicrosoft => GpuVendor.Microsoft,
        _ => GpuVendor.Other,
    };

    /// <summary>
    /// Selects the llama-server binary name to launch, or throws
    /// <see cref="NoSupportedGpuException"/> with a user-actionable message.
    /// </summary>
    public static string SelectServerBinaryName()
    {
        var adapters = EnumerateAdapters()
            .Where(a => !a.IsSoftware && a.Vendor != GpuVendor.Microsoft)
            .ToList();

        if (adapters.Any(a => a.Vendor == GpuVendor.Nvidia))
            return "llama-server-cuda.exe";

        // v2 (not yet shipped): AMD / Intel Arc via the Vulkan build. When
        // llama-server-vulkan.exe ships, this is the one line to change —
        // replace the throw with `return "llama-server-vulkan.exe";`.
        if (adapters.Any(a => a.Vendor is GpuVendor.Amd or GpuVendor.Intel))
            throw BuildNoSupportedGpuException(adapters);

        throw BuildNoSupportedGpuException(adapters);
    }

    private static NoSupportedGpuException BuildNoSupportedGpuException(IReadOnlyList<GpuAdapterInfo> detected)
    {
        string found = detected.Count == 0
            ? "No display adapters were detected."
            : "Detected: " + string.Join(", ", detected.Select(a => $"{a.Description} (vendor 0x{a.VendorId:X4})"));

        return new NoSupportedGpuException(
            "WindowLive requires an Nvidia GPU with CUDA 12 drivers — no supported GPU was found.\n\n" +
            found + "\n\n" +
            "AMD and Intel Arc GPUs are not yet supported (planned for a future release). " +
            "CPU-only translation is not supported: it is too slow for live game chat.");
    }
}
