using System;
using System.Runtime.InteropServices;

namespace WindowLive.App.Native;

/// <summary>
/// Win32 P/Invoke surface for capture, monitor enumeration, DPI, and hotkeys.
/// All coordinates returned by these APIs are physical pixels (the process is
/// PerMonitorV2 DPI-aware via app.manifest).
/// </summary>
internal static class NativeMethods
{
    // ---- GetSystemMetrics indices (virtual screen) ----
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // ---- Device contexts / GDI capture ----
    public const int SRCCOPY = 0x00CC0020;
    public const int CAPTUREBLT = 0x40000000;
    public const int BI_RGB = 0;
    public const int DIB_RGB_COLORS = 0;

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // Color table not needed for 32bpp BI_RGB.
        public uint bmiColors;
    }

    // ---- Monitor enumeration ----
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    // ---- Per-monitor DPI ----
    public const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---- Window positioning ----
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // ---- Extended window styles (used by GameMode's persistent panel to stay
    // non-activating and never take keyboard focus from the game underneath) ----
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;  // no taskbar/alt-tab entry
    public const int WS_EX_NOACTIVATE = 0x08000000;  // never receives keyboard focus/activation

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ---- Window messages (used by GamePanelWindow's WndProc hook to implement
    // custom hit-testing for drag/resize and to persist the user-sized rect) ----
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int WM_GETMINMAXINFO = 0x0024;

    // ---- WM_NCHITTEST return codes ----
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    // ---- Hotkeys ----
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- Virtual-key codes (main keyboard) used by the hotkey parser ----
    // A-Z / 0-9 equal their ASCII value and F1-F24 are computed, so only the
    // named/editing/navigation/OEM keys need explicit constants here.
    public const uint VK_BACK = 0x08;
    public const uint VK_TAB = 0x09;
    public const uint VK_RETURN = 0x0D;
    public const uint VK_ESCAPE = 0x1B;
    public const uint VK_SPACE = 0x20;
    public const uint VK_PRIOR = 0x21;  // Page Up
    public const uint VK_NEXT = 0x22;   // Page Down
    public const uint VK_END = 0x23;
    public const uint VK_HOME = 0x24;
    public const uint VK_LEFT = 0x25;
    public const uint VK_UP = 0x26;
    public const uint VK_RIGHT = 0x27;
    public const uint VK_DOWN = 0x28;
    public const uint VK_INSERT = 0x2D;
    public const uint VK_DELETE = 0x2E;

    // OEM punctuation keys with stable VK codes on a standard US layout.
    public const uint VK_OEM_1 = 0xBA;      // ';:'
    public const uint VK_OEM_PLUS = 0xBB;   // '=+'
    public const uint VK_OEM_COMMA = 0xBC;  // ',<'
    public const uint VK_OEM_MINUS = 0xBD;  // '-_'
    public const uint VK_OEM_PERIOD = 0xBE; // '.>'
    public const uint VK_OEM_2 = 0xBF;      // '/?'
    public const uint VK_OEM_3 = 0xC0;      // '`~'
    public const uint VK_OEM_4 = 0xDB;      // '[{'
    public const uint VK_OEM_5 = 0xDC;      // '\|'
    public const uint VK_OEM_6 = 0xDD;      // ']}'
    public const uint VK_OEM_7 = 0xDE;      // '\'"'

    // ---- Handles ----
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    // ---- Windows Job Objects (used by Server/LlamaServerManager to guarantee the
    // llama-server child dies even if this process crashes without running Dispose) ----
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS JobObjectInfoClass,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    public enum JOBOBJECTINFOCLASS
    {
        ExtendedLimitInformation = 9,
    }

    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    // ---- DXGI adapter enumeration (used by Server/GpuDetector for Nvidia/AMD/Intel
    // classification — no existing DXGI dependency in the capture pipeline, this is
    // the first use in this codebase, so the full COM vtable is declared by hand). ----

    public const uint DXGI_ADAPTER_FLAG_SOFTWARE = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    public static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);

    /// <summary>
    /// Raw COM vtable for IDXGIAdapter1. QueryInterface/AddRef/Release (IUnknown)
    /// are handled automatically by the CLR for InterfaceIsIUnknown types and must
    /// NOT be declared. The remaining members must appear in exact base-to-derived
    /// declaration order (IDXGIObject, then IDXGIAdapter, then IDXGIAdapter1's own
    /// GetDesc1) so the generated vtable offsets line up with the real interface —
    /// only GetDesc1 is ever actually invoked; the rest are unused placeholder slots.
    /// </summary>
    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIAdapter1
    {
        // IDXGIObject — unused, vtable-order placeholders.
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();

        // IDXGIAdapter — unused, vtable-order placeholders.
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();

        // IDXGIAdapter1 — the only member actually called. PreserveSig is
        // required: without it the CLR appends a phantom retval out-param
        // (corrupting the native call) and converts failure HRESULTs into
        // COMExceptions instead of returning them for the caller to inspect.
        [PreserveSig]
        int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }

    /// <summary>Same vtable-ordering rules as <see cref="IDXGIAdapter1"/> — see its doc comment.</summary>
    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIFactory1
    {
        // IDXGIObject — unused, vtable-order placeholders.
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();

        // IDXGIFactory — unused, vtable-order placeholders.
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();

        // IDXGIFactory1 — EnumAdapters1 is the only member actually called.
        // PreserveSig is required (see GetDesc1): EnumAdapters1 returns
        // DXGI_ERROR_NOT_FOUND as its normal end-of-enumeration signal, which
        // must reach the caller as a return code, never as a COMException.
        [PreserveSig]
        int EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter);
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsCurrent();
    }
}
