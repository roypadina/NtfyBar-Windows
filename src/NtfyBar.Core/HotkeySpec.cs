namespace NtfyBar.Core;

/// <summary>
/// Parses a global hotkey such as <c>Ctrl+Alt+Shift+C</c> into <c>RegisterHotKey</c> modifier flags
/// and a Windows virtual-key code (no Windows API here, so it is testable anywhere).
/// </summary>
public static class HotkeySpec
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8;

    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Tab"] = 0x09, ["Backspace"] = 0x08,
        ["Insert"] = 0x2D, ["Ins"] = 0x2D, ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PgUp"] = 0x21, ["PageDown"] = 0x22, ["PgDn"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Pause"] = 0x13, ["PrintScreen"] = 0x2C, ["PrtSc"] = 0x2C,
    };

    /// <summary>
    /// "Ctrl+Alt+Shift+C", "Win+F5", "Ctrl+Alt+1", "Ctrl+Shift+NumPad3": at least one modifier and
    /// exactly one key (A–Z, 0–9, F1–F24, NumPad0–9 or a name in <see cref="Named"/>), case-insensitive.
    /// </summary>
    public static bool TryParse(string? spec, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        foreach (var part in (spec ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Control; break;
                case "alt": modifiers |= Alt; break;
                case "shift": modifiers |= Shift; break;
                case "win": modifiers |= Win; break;
                default:
                    if (virtualKey != 0 || KeyCode(part) is not { } vk) return false;
                    virtualKey = vk;
                    break;
            }
        }
        return modifiers != 0 && virtualKey != 0;
    }

    private static uint? KeyCode(string name)
    {
        var upper = name.ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') return upper[0];  // VK_A… and VK_0… equal ASCII
        if (upper.StartsWith('F') && int.TryParse(upper[1..], out var f) && f is >= 1 and <= 24 && upper[1] != '0') return (uint)(0x70 + f - 1);
        if (upper.StartsWith("NUMPAD", StringComparison.Ordinal) && upper.Length == 7 && char.IsAsciiDigit(upper[6])) return (uint)(0x60 + upper[6] - '0');
        return Named.TryGetValue(name, out var vk) ? vk : null;
    }
}
