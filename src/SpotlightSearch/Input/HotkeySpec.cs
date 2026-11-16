namespace SpotlightSearch.Input;

/// <summary>
/// Parses "Alt+Space" style hotkey text into Win32 RegisterHotKey arguments.
/// Only the combinations a user can reasonably type are supported; anything else is
/// rejected so the settings dialog can say "no" instead of silently doing nothing.
/// </summary>
internal readonly record struct HotkeySpec(uint Modifiers, uint VirtualKey, string Text)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public static readonly HotkeySpec Default = new(ModAlt | ModNoRepeat, 0x20, "Alt+Space");

    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = Default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2) return false;

        uint modifiers = ModNoRepeat;
        string? keyName = null;

        foreach (string token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win" or "windows" or "meta":
                    modifiers |= ModWin;
                    break;
                default:
                    if (keyName != null) return false;
                    keyName = token;
                    break;
            }
        }

        if (keyName == null) return false;
        if ((modifiers & ~ModNoRepeat) == 0) return false;
        if (!TryParseKey(keyName, out uint virtualKey)) return false;

        spec = new HotkeySpec(modifiers, virtualKey, Normalize(modifiers, keyName));
        return true;
    }

    private static bool TryParseKey(string name, out uint virtualKey)
    {
        virtualKey = 0;

        if (name.Length == 1)
        {
            char ch = char.ToUpperInvariant(name[0]);
            if (ch is >= 'A' and <= 'Z') virtualKey = ch;
            else if (ch is >= '0' and <= '9') virtualKey = ch;
            return virtualKey != 0;
        }

        if (name.StartsWith('F') && int.TryParse(name[1..], out int function) && function is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + function - 1);
            return true;
        }

        virtualKey = name.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            "backspace" => 0x08,
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "`" => 0xC0,
            "-" => 0xBD,
            "=" => 0xBB,
            "[" => 0xDB,
            "]" => 0xDD,
            ";" => 0xBA,
            "'" => 0xDE,
            "," => 0xBC,
            "." => 0xBE,
            "/" => 0xBF,
            "\\" => 0xDC,
            _ => 0
        };

        return virtualKey != 0;
    }

    private static string Normalize(uint modifiers, string keyName)
    {
        var parts = new List<string>(4);
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");

        string key = keyName.Length == 1 ? keyName.ToUpperInvariant() : char.ToUpperInvariant(keyName[0]) + keyName[1..];
        parts.Add(key);
        return string.Join("+", parts);
    }
}
