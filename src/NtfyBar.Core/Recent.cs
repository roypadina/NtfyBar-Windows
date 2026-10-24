namespace NtfyBar.Core;

/// <summary>One entry of the tray's Recent menu: a received (or own, echoed) message plus what was fetched for it.</summary>
public sealed class RecentItem(IncomingMessage message)
{
    public const int LabelChars = 60;

    public IncomingMessage Message { get; } = message;
    public string Id => Message.Message.Id;
    /// <summary>Text to copy: the message for <see cref="ClipKind.Text"/>; the downloaded text for <see cref="ClipKind.TextAttachment"/> (null until fetched).</summary>
    public string? Text { get; set; } = message.Kind == ClipKind.Text ? message.Message.Message ?? "" : null;
    /// <summary>Saved file of an image or file attachment, once downloaded.</summary>
    public string? Path { get; set; }

    /// <summary>"Clipboard · hello world", "Sent · Clipboard · photo.jpg": one line, never the raw topic name.</summary>
    public string Label
    {
        get
        {
            var m = Message.Message;
            var what = Message.Kind switch
            {
                ClipKind.Text => m.Message ?? "",
                ClipKind.TextAttachment => Text ?? m.Message ?? "long text",
                ClipKind.ExpiredAttachment => $"{m.Attachment?.Name ?? "attachment"} (expired)",
                _ => m.Attachment?.Name ?? "attachment",
            };
            var sent = Message.Handling == Handling.OwnEcho ? "Sent · " : "";
            var line = string.Join(' ', $"{sent}{Message.Topic.DisplayLabel} · {what}".Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
            return Publisher.Preview(line, LabelChars);
        }
    }
}

/// <summary>The last <c>capacity</c> messages, newest first. UI thread only.</summary>
public sealed class RecentList(int capacity = 20)
{
    private readonly List<RecentItem> _items = [];

    public IReadOnlyList<RecentItem> Items => _items;

    public RecentItem Add(IncomingMessage m)
    {
        var item = new RecentItem(m);
        _items.Insert(0, item);
        if (_items.Count > capacity) _items.RemoveAt(_items.Count - 1);
        return item;
    }

    public RecentItem? Find(string? id) => _items.FirstOrDefault(i => i.Id == id);
}
