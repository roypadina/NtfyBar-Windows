using System.Runtime.InteropServices;
using NtfyBar.Core;

namespace NtfyBar.App;

/// <summary>
/// One global hotkey via <c>RegisterHotKey</c> on a hidden message-only window. <c>Pressed</c> runs on
/// the UI thread (the window lives there).
/// </summary>
internal sealed class HotKey : NativeWindow, IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int Id = 1;
    private const uint ModNoRepeat = 0x4000;
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
        if (!HotkeySpec.TryParse(spec, out var modifiers, out var key))
        {
            error = $"\"{spec}\" is not a valid hotkey (example: Ctrl+Alt+Shift+C)";
            return null;
        }
        var hotKey = new HotKey(pressed);
        if (!RegisterHotKey(hotKey.Handle, Id, modifiers | ModNoRepeat, key))
        {
            hotKey.DestroyHandle();
            error = $"{spec} is already used by another app";
            return null;
        }
        error = null;
        return hotKey;
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
