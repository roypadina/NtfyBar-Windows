namespace NtfyBar.Core;

/// <summary>What "Send Clipboard" sends.</summary>
public enum ClipboardPayload { Empty, Files, Image, Text }

/// <summary>Result of one user-initiated send, worded for a toast or the Publish window's status line.</summary>
public sealed record SendOutcome(bool Ok, string Text);

public static class Outgoing
{
    /// <summary>Files on the clipboard win, then a picture, then text (docs/PROTOCOL.md §1). Whitespace-only text counts as empty.</summary>
    public static ClipboardPayload Choose(int fileCount, bool hasImage, string? text) =>
        fileCount > 0 ? ClipboardPayload.Files
        : hasImage ? ClipboardPayload.Image
        : string.IsNullOrWhiteSpace(text) ? ClipboardPayload.Empty
        : ClipboardPayload.Text;
}

public static class CommandLine
{
    /// <summary>Explorer Send To runs <c>NtfyBar.exe --send &lt;file&gt; …</c>.</summary>
    public const string Send = "--send";
    public const string Uninstall = "--uninstall";

    /// <summary>
    /// The files after <see cref="Send"/>, as full paths (resolved against this process's folder, so a
    /// forwarded relative path still means the same file), without duplicates; null when there is no
    /// <see cref="Send"/>. Other switches (e.g. a toast's <c>-ToastActivated</c>) are skipped.
    /// </summary>
    public static List<string>? SendFiles(IEnumerable<string> args)
    {
        var list = args.ToList();
        var i = list.IndexOf(Send);
        if (i < 0) return null;
        return list.Skip(i + 1)
            .Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-'))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
