namespace Esi.Platform.Input;

/// <summary>
/// Maps W3C <c>UI Events KeyboardEvent.code</c> values (and modifier names) to Windows
/// virtual-key codes.
/// <para>
/// Using the protocol's <c>code</c> rather than <c>key</c> is deliberate: <c>code</c>
/// identifies the physical key, which is what a remote user actually intends when they
/// tap a key on their phone. Mapping to a virtual key and injecting with
/// <c>KEYEVENTF_SCANCODE</c> then lets the target machine's active keyboard layout decide
/// which character is produced, so a French <c>AZERTY</c> machine behaves correctly when
/// driven from a US-QWERTY phone.
/// </para>
/// </summary>
public static class KeyMap
{
    /// <summary>Virtual-key code for a <c>KeyboardEvent.code</c>, or <c>null</c> if unmapped.</summary>
    public static ushort? ToVirtualKey(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        if (code.Length == 1)
        {
            // Single characters: digits and punctuation already carry their VK.
            var ch = char.ToUpperInvariant(code[0]);
            if (ch is >= '0' and <= '9')
                return ch;
            if (ch is >= 'A' and <= 'Z')
                return (ushort)ch;
        }

        if (s_map.TryGetValue(code, out var vk))
            return vk;

        // Accept the common aliases so a client can send either spelling.
        return s_aliases.TryGetValue(code, out var alias) && s_map.TryGetValue(alias, out var aliased)
            ? aliased
            : null;
    }

    /// <summary>
    /// Whether a key needs the <c>KEYEVENTF_EXTENDEDKEY</c> flag. Without it, the arrow
    /// keys and the numpad duplicate the numeric keypad and Home/End/Delete misbehave.
    /// </summary>
    public static bool IsExtended(string? code) => code is not null && s_extended.Contains(code);

    private static readonly HashSet<string> s_extended = new(StringComparer.Ordinal)
    {
        "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
        "Home", "End", "PageUp", "PageDown", "Insert", "Delete",
        "NumpadDivide", "NumpadEnter", "NumpadDecimal",
        "Numpad1", "Numpad2", "Numpad3", "Numpad4", "Numpad5",
        "Numpad6", "Numpad7", "Numpad8", "Numpad9", "Numpad0",
        "PrintScreen", "NumLock", "ScrollLock", "Pause",
        "BrowserBack", "BrowserForward", "BrowserRefresh", "BrowserStop", "BrowserSearch", "BrowserFavorites", "BrowserHome",
        "AudioVolumeMute", "AudioVolumeDown", "AudioVolumeUp",
        "MediaPlayPause", "MediaStop", "MediaTrackNext", "MediaTrackPrevious",
    };

    private static readonly Dictionary<string, string> s_aliases = new(StringComparer.Ordinal)
    {
        ["Up"] = "ArrowUp",
        ["Down"] = "ArrowDown",
        ["Left"] = "ArrowLeft",
        ["Right"] = "ArrowRight",
        ["Esc"] = "Escape",
        ["Enter"] = "Enter",
        ["Space"] = "Space",
        ["Tab"] = "Tab",
        ["Backspace"] = "Backspace",
        ["Del"] = "Delete",
        ["PgUp"] = "PageUp",
        ["PgDn"] = "PageDown",
        ["Return"] = "Enter",
        ["VolumeUp"] = "AudioVolumeUp",
        ["VolumeDown"] = "AudioVolumeDown",
        ["VolumeMute"] = "AudioVolumeMute",
        ["PlayPause"] = "MediaPlayPause",
        ["MediaNext"] = "MediaTrackNext",
        ["MediaPrev"] = "MediaTrackPrevious",
    };

    private static readonly Dictionary<string, ushort> s_map = new(StringComparer.Ordinal)
    {
        // ---- Editing / control ------------------------------------------------
        ["Enter"] = 0x0D,
        ["NumpadEnter"] = 0x0D,
        ["Tab"] = 0x09,
        ["Space"] = 0x20,
        ["Backspace"] = 0x08,
        ["Escape"] = 0x1B,
        ["CapsLock"] = 0x14,
        ["Delete"] = 0x2E,
        ["Insert"] = 0x2D,
        ["PrintScreen"] = 0x2C,
        ["Pause"] = 0x13,
        ["ContextMenu"] = 0x5D,
        ["NumLock"] = 0x90,
        ["ScrollLock"] = 0x91,

        // ---- Modifiers --------------------------------------------------------
        ["ShiftLeft"] = 0xA0,
        ["ShiftRight"] = 0xA1,
        ["ControlLeft"] = 0xA2,
        ["ControlRight"] = 0xA3,
        ["AltLeft"] = 0xA4,
        ["AltRight"] = 0xA5,
        ["MetaLeft"] = 0x5B,
        ["MetaRight"] = 0x5C,
        // Short aliases the app uses for the on-screen modifier toggles.
        ["Shift"] = 0x10,
        ["Control"] = 0x11,
        ["Alt"] = 0x12,
        ["Meta"] = 0x5B,
        ["Win"] = 0x5B,
        ["Cmd"] = 0x5B,

        // ---- Arrows / navigation ---------------------------------------------
        ["ArrowUp"] = 0x26,
        ["ArrowDown"] = 0x28,
        ["ArrowLeft"] = 0x25,
        ["ArrowRight"] = 0x27,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,

        // ---- Function keys ----------------------------------------------------
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,

        // ---- Numpad -----------------------------------------------------------
        ["Numpad0"] = 0x60, ["Numpad1"] = 0x61, ["Numpad2"] = 0x62, ["Numpad3"] = 0x63,
        ["Numpad4"] = 0x64, ["Numpad5"] = 0x65, ["Numpad6"] = 0x66, ["Numpad7"] = 0x67,
        ["Numpad8"] = 0x68, ["Numpad9"] = 0x69,
        ["NumpadMultiply"] = 0x6A,
        ["NumpadAdd"] = 0x6B,
        ["NumpadSubtract"] = 0x6D,
        ["NumpadDecimal"] = 0x6E,
        ["NumpadDivide"] = 0x6F,

        // ---- OEM punctuation (US layout; other layouts remap at the driver) ---
        ["Semicolon"] = 0xBA,
        ["Equal"] = 0xBB,
        ["Comma"] = 0xBC,
        ["Minus"] = 0xBD,
        ["Period"] = 0xBE,
        ["Slash"] = 0xBF,
        ["Backquote"] = 0xC0,
        ["BracketLeft"] = 0xDB,
        ["Backslash"] = 0xDC,
        ["BracketRight"] = 0xDD,
        ["Quote"] = 0xDE,

        // ---- Media / volume ---------------------------------------------------
        ["MediaPlayPause"] = 0xB3,
        ["MediaStop"] = 0xB2,
        ["MediaTrackNext"] = 0xB0,
        ["MediaTrackPrevious"] = 0xB1,
        ["AudioVolumeMute"] = 0xAD,
        ["AudioVolumeDown"] = 0xAE,
        ["AudioVolumeUp"] = 0xAF,

        // ---- Browser ----------------------------------------------------------
        ["BrowserBack"] = 0xA6,
        ["BrowserForward"] = 0xA7,
        ["BrowserRefresh"] = 0xA8,
        ["BrowserStop"] = 0xA9,
        ["BrowserSearch"] = 0xAA,
        ["BrowserFavorites"] = 0xAB,
        ["BrowserHome"] = 0xAC,
    };

    /// <summary>Human-readable label for a <c>code</c>, for the on-screen keyboard UI.</summary>
    public static string Describe(string code) => code switch
    {
        "ArrowUp" => "↑",
        "ArrowDown" => "↓",
        "ArrowLeft" => "←",
        "ArrowRight" => "→",
        "Enter" => "Enter",
        "Escape" => "Esc",
        "Space" => "Space",
        "Backspace" => "⌫",
        "Tab" => "Tab",
        "Delete" => "Del",
        "ShiftLeft" or "ShiftRight" or "Shift" => "Shift",
        "ControlLeft" or "ControlRight" or "Control" => "Ctrl",
        "AltLeft" or "AltRight" or "Alt" => "Alt",
        "MetaLeft" or "MetaRight" or "Meta" => "Win",
        "CapsLock" => "Caps",
        "PageUp" => "PgUp",
        "PageDown" => "PgDn",
        _ when code.StartsWith("F") && int.TryParse(code[1..], out var n) && n is >= 1 and <= 24 => $"F{n}",
        _ => code,
    };
}
