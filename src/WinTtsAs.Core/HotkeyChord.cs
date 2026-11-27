using System.Text;

namespace WinTtsAs.Core;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

/// <summary>A global shortcut such as "Ctrl+Alt+R".</summary>
public sealed record HotkeyChord(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<uint, string> KeyNames = BuildKeyNames();

    public string KeyName => KeyNames.TryGetValue(VirtualKey, out var name)
        ? name
        : $"0x{VirtualKey:X2}";

    public string Display
    {
        get
        {
            var text = new StringBuilder();
            if (Modifiers.HasFlag(HotkeyModifiers.Control))
            {
                text.Append("Ctrl+");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                text.Append("Alt+");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            {
                text.Append("Shift+");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Win))
            {
                text.Append("Win+");
            }

            text.Append(KeyName);
            return text.ToString();
        }
    }

    public override string ToString() => Display;

    public static bool TryParse(string? text, out HotkeyChord? chord, out string? error)
    {
        chord = null;
        error = null;

        var raw = text?.Trim() ?? "";
        if (raw.Length == 0)
        {
            error = "a shortcut is required";
            return false;
        }

        var parts = raw.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(part => part.Length == 0))
        {
            error = $"invalid shortcut: {raw}";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        uint? virtualKey = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "win" or "windows":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            if (virtualKey is not null)
            {
                error = $"only one key is allowed in {raw}";
                return false;
            }

            virtualKey = VirtualKeyFromName(part);
            if (virtualKey is null)
            {
                error = $"unknown key: {part}";
                return false;
            }
        }

        if (modifiers == HotkeyModifiers.None)
        {
            error = "a shortcut needs Ctrl, Alt, Shift, or Win";
            return false;
        }

        if (virtualKey is null)
        {
            error = "a shortcut needs a key";
            return false;
        }

        chord = new HotkeyChord(modifiers, virtualKey.Value);
        return true;
    }

    public static bool TryFromVirtualKey(
        HotkeyModifiers modifiers,
        uint virtualKey,
        out HotkeyChord? chord)
    {
        chord = null;
        if (modifiers == HotkeyModifiers.None || !KeyNames.ContainsKey(virtualKey))
        {
            return false;
        }

        chord = new HotkeyChord(modifiers, virtualKey);
        return true;
    }

    public static string? NameForKey(uint virtualKey) =>
        KeyNames.TryGetValue(virtualKey, out var name) ? name : null;

    private static uint? VirtualKeyFromName(string name)
    {
        var upper = name.ToUpperInvariant();

        if (upper.Length == 1)
        {
            var character = upper[0];
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return character;
            }
        }

        if (upper.Length is 2 or 3 &&
            upper[0] == 'F' &&
            int.TryParse(upper[1..], out var function) &&
            function is >= 1 and <= 24)
        {
            return (uint)(0x70 + function - 1);
        }

        return upper switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" or "BS" => 0x08,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            _ => null
        };
    }

    private static Dictionary<uint, string> BuildKeyNames()
    {
        var names = new Dictionary<uint, string>
        {
            [0x08] = "Backspace",
            [0x09] = "Tab",
            [0x0D] = "Enter",
            [0x1B] = "Esc",
            [0x20] = "Space",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x2D] = "Insert",
            [0x2E] = "Delete"
        };

        for (var character = 'A'; character <= 'Z'; character++)
        {
            names[(uint)character] = character.ToString();
        }

        for (var character = '0'; character <= '9'; character++)
        {
            names[(uint)character] = character.ToString();
        }

        for (var function = 1; function <= 24; function++)
        {
            names[(uint)(0x70 + function - 1)] = "F" + function;
        }

        return names;
    }
}
