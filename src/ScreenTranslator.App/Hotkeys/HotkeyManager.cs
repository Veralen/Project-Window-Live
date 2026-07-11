using System;
using System.Windows.Interop;
using ScreenTranslator.App.Native;

namespace ScreenTranslator.App.Hotkeys;

/// <summary>
/// Registers a system-wide hotkey via Win32 RegisterHotKey and dispatches
/// WM_HOTKEY through a message-only HwndSource window. Parses "Ctrl+Alt+T"
/// style strings (modifiers Ctrl/Alt/Shift/Win).
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0xA731;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _source;
    private bool _registered;

    /// <summary>Raised on the UI thread when the hotkey fires.</summary>
    public event Action? Triggered;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("ScreenTranslatorHotkey")
        {
            ParentWindow = HWND_MESSAGE, // message-only window
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    /// <summary>Attempts to register the hotkey. Returns false on failure (e.g. already taken).</summary>
    public bool TryRegister(string hotkey, out string error)
    {
        error = string.Empty;
        if (!TryParse(hotkey, out uint mods, out uint vk))
        {
            error = $"Could not parse hotkey '{hotkey}'.";
            return false;
        }
        if (NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, mods | NativeMethods.MOD_NOREPEAT, vk))
        {
            _registered = true;
            return true;
        }
        error = $"RegisterHotKey failed for '{hotkey}' (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}). It may be in use by another app.";
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Triggered?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public static bool TryParse(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(hotkey)) return false;

        foreach (var raw in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "win":
                case "windows":
                case "meta": modifiers |= NativeMethods.MOD_WIN; break;
                default:
                    if (!TryParseKey(raw, out vk)) return false;
                    break;
            }
        }
        return vk != 0 && modifiers != 0;
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c; // VK codes for A-Z / 0-9 equal their ASCII uppercase value
                return true;
            }
        }
        // Function keys F1-F24 (VK_F1 = 0x70).
        if ((key.Length is 2 or 3) && (key[0] is 'f' or 'F') && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + (fn - 1));
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
