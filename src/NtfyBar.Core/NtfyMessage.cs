using System.Text.Json;

namespace NtfyBar.Core;

/// <summary><c>attachment</c> of an ntfy message. Type/size/expires exist only for files uploaded to the server.</summary>
public sealed record NtfyAttachment(string? Name, string? Url, string? Type, long? Size, long? Expires)
{
    public static NtfyAttachment FromJson(JsonElement o) => new(
        o.Get<string?>("name", null), o.Get<string?>("url", null), o.Get<string?>("type", null),
        o.Get<long?>("size", null), o.Get<long?>("expires", null));
}

/// <summary>One action button: <c>view</c> (Url), <c>copy</c> (Value), <c>http</c>, <c>broadcast</c>.</summary>
public sealed record NtfyAction(string Action, string Label, string? Url, string? Value)
{
    public static NtfyAction? FromJson(JsonElement o) => o.Get<string?>("action", null) is { } action
        ? new NtfyAction(action, o.Get("label", ""), o.Get<string?>("url", null), o.Get<string?>("value", null))
        : null;
}

/// <summary>
/// One line of ntfy's <c>/json</c> stream (<c>open</c>, <c>keepalive</c>, <c>message</c>,
/// <c>poll_request</c>, …). <c>id</c>, <c>time</c> and <c>event</c> are required; every other field
/// is read on its own, so one odd field never drops the message.
/// </summary>
public sealed record NtfyMessage
{
    public required string Id { get; init; }
    /// <summary>Unix seconds, server clock.</summary>
    public required long Time { get; init; }
    public required string Event { get; init; }
    public string Topic { get; init; } = "";
    public string? Title { get; init; }
    public string? Message { get; init; }
    public int? Priority { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Click { get; init; }
    public IReadOnlyList<NtfyAction> Actions { get; init; } = [];
    public NtfyAttachment? Attachment { get; init; }

    /// <summary>1…5; ntfy's default 3 when absent or out of range.</summary>
    public int PriorityValue => Priority is >= 1 and <= 5 ? Priority.Value : 3;

    public DateTimeOffset Date => DateTimeOffset.FromUnixTimeSeconds(Time);

    /// <summary>Null for blank or malformed lines.</summary>
    public static NtfyMessage? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            var id = o.Get<string?>("id", null);
            var ev = o.Get<string?>("event", null);
            var time = o.Get<long?>("time", null);
            if (id is null || ev is null || time is null) return null;
            return new NtfyMessage
            {
                Id = id,
                Time = time.Value,
                Event = ev,
                Topic = o.Get("topic", ""),
                Title = o.Get<string?>("title", null),
                Message = o.Get<string?>("message", null),
                Priority = o.Get<int?>("priority", null),
                Tags = o.Get<List<string>>("tags", []),
                Click = o.Get<string?>("click", null),
                Actions = o.GetList("actions", NtfyAction.FromJson, []),
                Attachment = o.TryGetProperty("attachment", out var a) && a.ValueKind == JsonValueKind.Object ? NtfyAttachment.FromJson(a) : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
