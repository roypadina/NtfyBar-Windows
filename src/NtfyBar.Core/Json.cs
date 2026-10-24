using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NtfyBar.Core;

/// <summary>
/// Shared JSON settings plus tolerant field readers: every field falls back to its own default,
/// so an old, partial or hand-edited file (or an odd field in a server message) never wipes the
/// rest (same pattern as the macOS app).
/// </summary>
internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Raw UTF-8 instead of \uXXXX: Hebrew/emoji text would otherwise triple in size and can
        // push a 4000-byte message over ntfy's JSON body limit.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Same, without indentation: request bodies count against ntfy's size limits.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static T Get<T>(this JsonElement o, string name, T fallback)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return fallback;
        try
        {
            return v.Deserialize<T>(Options) ?? fallback;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NotSupportedException)
        {
            return fallback;
        }
    }

    /// <summary>Array of objects, each read on its own; non-objects are skipped.</summary>
    public static List<T> GetList<T>(this JsonElement o, string name, Func<JsonElement, T?> read, List<T> fallback) where T : class
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return fallback;
        var list = new List<T>();
        foreach (var e in v.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Object && read(e) is { } item)
                list.Add(item);
        return list;
    }
}
