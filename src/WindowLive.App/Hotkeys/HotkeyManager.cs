using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using WindowLive.App.Native;

namespace WindowLive.App.Hotkeys;

/// <summary>
/// Registers system-wide hotkeys via Win32 RegisterHotKey and dispatches
/// WM_HOTKEY through a message-only HwndSource window. Supports multiple
/// simultaneous bindings, each identified by a caller-chosen "slot" name (see
/// <see cref="DesktopSlot"/> / <see cref="GameModeSlot"/>) — desktop snip mode
/// and game mode each own one slot, and rebinding one never disturbs the
/// other. Parses "Ctrl+Alt+T" style strings (modifiers Ctrl/Alt/Shift/Win) and
/// is the single source of truth for the token &lt;-&gt; virtual-key mapping
/// shared with the WPF capture box.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    /// <summary>Slot name for the desktop (one-shot snip) hotkey.</summary>
    public const string DesktopSlot = "desktop";

    /// <summary>Slot name for the game-mode (region setup/redefine) hotkey.</summary>
    public const string GameModeSlot = "gameMode";

    private const int BaseHotkeyId = 0xA731;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private sealed class Binding
    {
        public int Id;
        public bool Registered;
        public string? Hotkey;
    }

    private readonly HwndSource _source;
    private readonly Dictionary<string, Binding> _bindings = new();
    private int _nextId = BaseHotkeyId;

    /// <summary>Raised on the UI thread when a registered hotkey fires, with its slot name.</summary>
    public event Action<string>? Triggered;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("WindowLiveHotkey")
        {
            ParentWindow = HWND_MESSAGE, // message-only window
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    /// <summary>The hotkey string currently registered for <paramref name="slot"/>, or null.</summary>
    public string? CurrentHotkey(string slot) => _bindings.TryGetValue(slot, out var b) ? b.Hotkey : null;

    /// <summary>
    /// Registers a hotkey for a slot (used at startup). Implemented in terms of
    /// <see cref="TryReplace"/> so the two paths share registration/rollback logic.
    /// </summary>
    public bool TryRegister(string slot, string hotkey, out string error) => TryReplace(slot, hotkey, out error);

    /// <summary>
    /// Swaps the active hotkey for <paramref name="slot"/> without an app restart.
    /// Unregisters that slot's current binding, attempts to register
    /// <paramref name="newHotkey"/>, and — if that fails — re-registers the
    /// slot's previous binding so the app is never left without a working
    /// shortcut for that slot. Other slots are untouched. Returns false with a
    /// user-facing message on failure.
    /// </summary>
    public bool TryReplace(string slot, string newHotkey, out string error)
    {
        error = string.Empty;
        if (!TryParse(newHotkey, out uint mods, out uint vk))
        {
            error = $"Could not understand the shortcut '{newHotkey}'.";
            return false;
        }

        if (!_bindings.TryGetValue(slot, out var binding))
        {
            binding = new Binding { Id = _nextId++ };
            _bindings[slot] = binding;
        }

        string? previous = binding.Hotkey;
        UnregisterBinding(binding);

        if (NativeMethods.RegisterHotKey(_source.Handle, binding.Id, mods | NativeMethods.MOD_NOREPEAT, vk))
        {
            binding.Registered = true;
            binding.Hotkey = newHotkey;
            return true;
        }

        int win32 = Marshal.GetLastWin32Error();

        // Registration failed — roll back to the previous binding for this slot
        // if there was one so the user keeps a working shortcut.
        if (previous is not null && TryParse(previous, out uint pmods, out uint pvk) &&
            NativeMethods.RegisterHotKey(_source.Handle, binding.Id, pmods | NativeMethods.MOD_NOREPEAT, pvk))
        {
            binding.Registered = true;
            binding.Hotkey = previous;
        }

        error = $"That shortcut is already in use by another app. (Win32 error {win32})";
        return false;
    }

    private void UnregisterBinding(Binding binding)
    {
        if (binding.Registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, binding.Id);
            binding.Registered = false;
        }
        binding.Hotkey = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            foreach (var kv in _bindings)
            {
                if (kv.Value.Id == id)
                {
                    Triggered?.Invoke(kv.Key);
                    handled = true;
                    break;
                }
            }
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
                    if (vk != 0 || !TryParseKey(raw, out vk)) return false; // reject a second main key
                    break;
            }
        }
        return vk != 0 && modifiers != 0;
    }

    /// <summary>
    /// Maps a single key token to its virtual-key code. Accepts A-Z, 0-9, F1-F24,
    /// the named editing/navigation keys, and OEM punctuation tokens. Case-insensitive.
    /// </summary>
    internal static bool TryParseKey(string key, out uint vk)
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
        // Named editing/navigation keys and OEM punctuation.
        if (NamedKeyTokens.TryGetValue(key, out uint named))
        {
            vk = named;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the canonical hotkey string ("Ctrl+Shift+L") from WPF modifiers and a
    /// key, using the SAME tokens <see cref="TryParse"/> accepts. Returns null if the
    /// key has no supported token (e.g. a bare modifier or an unmapped key).
    /// </summary>
    public static string? Format(ModifierKeys mods, Key key)
    {
        string? keyToken = FormatKey(key);
        if (keyToken is null) return null;

        var parts = new List<string>(5);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(keyToken);
        return string.Join("+", parts);
    }

    /// <summary>Maps a WPF <see cref="Key"/> to its canonical token, or null if unsupported.</summary>
    public static string? FormatKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString();                       // "A".."Z"
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.F1 and <= Key.F24) return "F" + ((int)(key - Key.F1) + 1);
        return WpfKeyToToken.TryGetValue(key, out string? token) ? token : null;
    }

    // ---- Single source of truth: token tables shared by parse + format ----

    // Token (canonical + aliases) -> virtual-key code. Case-insensitive.
    private static readonly Dictionary<string, uint> NamedKeyTokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = NativeMethods.VK_SPACE,
            ["Enter"] = NativeMethods.VK_RETURN,
            ["Return"] = NativeMethods.VK_RETURN,
            ["Tab"] = NativeMethods.VK_TAB,
            ["Insert"] = NativeMethods.VK_INSERT,
            ["Ins"] = NativeMethods.VK_INSERT,
            ["Delete"] = NativeMethods.VK_DELETE,
            ["Del"] = NativeMethods.VK_DELETE,
            ["Home"] = NativeMethods.VK_HOME,
            ["End"] = NativeMethods.VK_END,
            ["PageUp"] = NativeMethods.VK_PRIOR,
            ["PgUp"] = NativeMethods.VK_PRIOR,
            ["PageDown"] = NativeMethods.VK_NEXT,
            ["PgDn"] = NativeMethods.VK_NEXT,
            ["Up"] = NativeMethods.VK_UP,
            ["Down"] = NativeMethods.VK_DOWN,
            ["Left"] = NativeMethods.VK_LEFT,
            ["Right"] = NativeMethods.VK_RIGHT,
            ["`"] = NativeMethods.VK_OEM_3,
            ["-"] = NativeMethods.VK_OEM_MINUS,
            ["="] = NativeMethods.VK_OEM_PLUS,
            ["["] = NativeMethods.VK_OEM_4,
            ["]"] = NativeMethods.VK_OEM_6,
            ["\\"] = NativeMethods.VK_OEM_5,
            [";"] = NativeMethods.VK_OEM_1,
            ["'"] = NativeMethods.VK_OEM_7,
            [","] = NativeMethods.VK_OEM_COMMA,
            ["."] = NativeMethods.VK_OEM_PERIOD,
            ["/"] = NativeMethods.VK_OEM_2,
        };

    // WPF Key -> canonical token (the token the capture box emits). Must round-trip
    // through NamedKeyTokens so a captured chord is registrable.
    private static readonly Dictionary<Key, string> WpfKeyToToken = new()
    {
        [Key.Space] = "Space",
        [Key.Enter] = "Enter",   // Key.Return shares this value
        [Key.Tab] = "Tab",
        [Key.Insert] = "Insert",
        [Key.Delete] = "Delete",
        [Key.Home] = "Home",
        [Key.End] = "End",
        [Key.PageUp] = "PageUp", // Key.Prior shares this value
        [Key.PageDown] = "PageDown",
        [Key.Up] = "Up",
        [Key.Down] = "Down",
        [Key.Left] = "Left",
        [Key.Right] = "Right",
        [Key.OemTilde] = "`",
        [Key.OemMinus] = "-",
        [Key.OemPlus] = "=",
        [Key.OemOpenBrackets] = "[",
        [Key.OemCloseBrackets] = "]",
        [Key.OemPipe] = "\\",
        [Key.OemSemicolon] = ";",
        [Key.OemQuotes] = "'",
        [Key.OemComma] = ",",
        [Key.OemPeriod] = ".",
        [Key.OemQuestion] = "/",
    };

    public void Dispose()
    {
        foreach (var binding in _bindings.Values)
            UnregisterBinding(binding);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
