using System.Collections.Specialized;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NtfyBar.Core;

namespace NtfyBar.App;

/// <summary>What Send Clipboard found: exactly one of files / PNG / text is set, per <see cref="Payload"/>.</summary>
internal sealed record ClipboardContent(ClipboardPayload Payload, IReadOnlyList<string> Files, byte[]? Png, string? Text);

/// <summary>
/// The Windows clipboard, UI (STA) thread only. Another app holding the clipboard open makes the
/// calls throw <see cref="ExternalException"/>: each call is retried a few times before giving up.
/// </summary>
internal static class ClipboardIO
{
    private const int Tries = 3;

    /// <summary>Line endings become CRLF: plain Win32 text boxes show a bare LF as nothing.</summary>
    public static bool SetText(string text) =>
        text.Length > 0 && Try(() => Clipboard.SetText(text.ReplaceLineEndings("\r\n")));

    /// <summary>As a file drop list (CF_HDROP): pastes as files in Explorer, mail, chat apps.</summary>
    public static bool SetFiles(params string[] paths) => Try(() =>
    {
        var list = new StringCollection();
        list.AddRange(paths);
        Clipboard.SetFileDropList(list);
    });

    /// <summary>As a picture. False when GDI+ can't decode it (e.g. WebP/HEIC) or the clipboard stays busy.</summary>
    public static bool SetImageFile(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var image = Image.FromStream(file);
            return Try(() => Clipboard.SetImage(image));
        }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or ExternalException or IOException or UnauthorizedAccessException)
        {
            return false;  // GDI+ reports an unknown format as ArgumentException or OutOfMemoryException
        }
    }

    /// <summary>Files win, then a picture (as PNG), then text (<see cref="Outgoing.Choose"/>). Null when the clipboard stays busy.</summary>
    public static ClipboardContent? Read()
    {
        ClipboardContent? content = null;
        return Try(() => content = ReadOnce()) ? content : null;
    }

    private static ClipboardContent ReadOnce()
    {
        var files = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : [];
        byte[]? png = null;
        if (files.Count == 0 && Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            using (image)
            using (var buffer = new MemoryStream())
            {
                image.Save(buffer, ImageFormat.Png);
                png = buffer.ToArray();
            }
        }
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        return Outgoing.Choose(files.Count, png is not null, text) switch
        {
            ClipboardPayload.Files => new ClipboardContent(ClipboardPayload.Files, files, null, null),
            ClipboardPayload.Image => new ClipboardContent(ClipboardPayload.Image, [], png, null),
            ClipboardPayload.Text => new ClipboardContent(ClipboardPayload.Text, [], null, text),
            _ => new ClipboardContent(ClipboardPayload.Empty, [], null, null),
        };
    }

    private static bool Try(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (ExternalException) when (attempt < Tries)
            {
                Thread.Sleep(100);  // another app has the clipboard open; it is usually a few ms
            }
            catch (ExternalException)
            {
                return false;
            }
        }
    }
}
