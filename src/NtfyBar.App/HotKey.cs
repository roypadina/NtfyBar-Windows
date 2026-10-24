using System.Runtime.InteropServices;

namespace NtfyBar.App;

/// <summary>
/// One global hotkey via <c>RegisterHotKey</c> on a hidden message-only window. <c>Pressed</c> runs on
/// the UI thread (the window lives there).
/// </summary>
internal sealed class HotKey : NativeWindow, IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int Id = 1;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Action _pressed;

    private HotKey(Action pressed)
    {
        _pressed = pressed;
        CreateHandle(new CreateParams { Parent = HwndMessage });
    }

    /// <summary>Null + <paramref name="error"/> when the spec is invalid or another app already owns the combination.</summary>
    public static HotKey? TryRegister(string spec, Action pressed, out string? error)
    {
        if (!TryParse(spec, out var modifiers, out var key))
        {
            error = $"\"{spec}\" is not a valid hotkey (example: Ctrl+Alt+Shift+C)";
            return null;
        }
        var hotKey = new HotKey(pressed);
        if (!RegisterHotKey(hotKey.Handle, Id, modifiers | ModNoRepeat, (uint)key))
        {
            hotKey.DestroyHandle();
            error = $"{spec} is already used by another app";
            return null;
        }
        error = null;
        return hotKey;
    }

    /// <summary>"Ctrl+Alt+Shift+C", "Win+F5", "Ctrl+Alt+1": at least one modifier and exactly one key.</summary>
    public static bool TryParse(string spec, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        foreach (var part in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win": modifiers |= ModWin; break;
                default:
                    if (key != Keys.None) return false;
                    // A bare digit would parse as the enum's numeric value (1 = LButton), not the "1" key.
                    if (part.Length == 1 && char.IsAsciiDigit(part[0])) key = Keys.D0 + (part[0] - '0');
                    else if (!Enum.TryParse(part, ignoreCase: true, out key) || char.IsAsciiDigit(part[0])) return false;
                    break;
            }
        }
        return modifiers != 0 && key != Keys.None;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey && m.WParam == Id) _pressed();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
