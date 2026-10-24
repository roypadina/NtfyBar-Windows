namespace NtfyBar.Core;

public enum ClipKind
{
    /// <summary>No attachment: <see cref="NtfyMessage.Message"/> is the payload.</summary>
    Text,
    /// <summary>Long text sent as <c>clip.txt</c>/<c>attachment.txt</c>: download, decode UTF-8, handle as text.</summary>
    TextAttachment,
    /// <summary><c>image/*</c> attachment: paste as a picture.</summary>
    Image,
    File,
    /// <summary>Attachment past its <c>expires</c>: nothing to download.</summary>
    ExpiredAttachment,
}

public enum Handling
{
    /// <summary>Act on it: copy / download / toast per topic settings.</summary>
    Live,
    /// <summary>Replayed from the server cache and too old: list it in Recent and count it in the summary; no auto-copy.</summary>
    Missed,
    /// <summary>Our own publish coming back: keep it in Recent as read, run nothing.</summary>
    OwnEcho,
}

/// <summary>The shared-clipboard rules (docs/PROTOCOL.md): what a message is and whether to act on it.</summary>
public static class Clip
{
    public const long MaxTextAttachmentBytes = 1024 * 1024;
    /// <summary>A catch-up message at most this old at receipt still counts as live ("copied on the phone, opened the laptop").</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(120);

    private static readonly string[] TextAttachmentNames = ["clip.txt", "attachment.txt"];

    public static ClipKind Classify(NtfyMessage m, DateTimeOffset now)
    {
        if (m.Attachment is not { } a) return ClipKind.Text;
        if (a.Expires is { } expires && expires < now.ToUnixTimeSeconds()) return ClipKind.ExpiredAttachment;
        if (IsTextAttachment(a)) return ClipKind.TextAttachment;
        if (a.Type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) return ClipKind.Image;
        return ClipKind.File;
    }

    /// <summary>Treat-as-text rule: <c>text/plain*</c>, ≤ 1 MB, named exactly <c>clip.txt</c> or <c>attachment.txt</c>.</summary>
    public static bool IsTextAttachment(NtfyAttachment a) =>
        a.Type?.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) == true
        && a.Size is >= 0 and <= MaxTextAttachmentBytes
        && TextAttachmentNames.Contains(a.Name, StringComparer.Ordinal);

    public static Handling Decide(NtfyMessage m, bool isCatchUp, bool ownEcho, bool processMissed, DateTimeOffset now)
    {
        if (ownEcho) return Handling.OwnEcho;
        if (!isCatchUp || processMissed) return Handling.Live;
        return now - m.Date <= FreshWindow ? Handling.Live : Handling.Missed;
    }
}

/// <summary>
/// Recognises our own publishes coming back on the stream: ids returned by our publishes (2-minute
/// TTL) or a first tag equal to our device tag (covers a restart between publish and echo).
/// Thread-safe.
/// </summary>
public sealed class EchoGuard(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, DateTimeOffset> _ids = [];

    public void Register(string id)
    {
        lock (_ids)
        {
            Prune();
            _ids[id] = _clock();
        }
    }

    public bool IsOwnEcho(NtfyMessage m, string? deviceTag)
    {
        lock (_ids)
        {
            Prune();
            if (_ids.Remove(m.Id)) return true;
        }
        return !string.IsNullOrEmpty(deviceTag) && m.Tags.Count > 0 && m.Tags[0] == deviceTag;
    }

    private void Prune()
    {
        var cutoff = _clock() - Ttl;
        foreach (var (id, at) in _ids.ToList())
            if (at < cutoff)
                _ids.Remove(id);
    }
}
