using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NtfyBar.Core;

public enum PublishStatus
{
    Sent,
    /// <summary>401/403: no write access to this topic.</summary>
    NotAllowed,
    /// <summary>413, or refused before upload (over 100 MB).</summary>
    TooLarge,
    /// <summary>429.</summary>
    RateLimited,
    /// <summary>No connection / DNS / TLS failure / timeout.</summary>
    Unreachable,
    /// <summary>Refused before upload: more than <see cref="Publisher.MaxFiles"/> files.</summary>
    TooManyFiles,
    /// <summary>A local file could not be read.</summary>
    FileError,
    /// <summary>Server URL or topic name invalid, or topic unknown.</summary>
    InvalidTarget,
    /// <summary>Any other non-2xx (including a 3xx: redirects are not followed).</summary>
    HttpError,
}

public sealed record PublishResult(PublishStatus Status, string? Id = null, int HttpStatus = 0)
{
    public bool Ok => Status == PublishStatus.Sent;

    /// <summary>Short user-facing reason, e.g. for a "Couldn't send: …" toast.</summary>
    public string Describe() => Status switch
    {
        PublishStatus.Sent => "sent",
        PublishStatus.NotAllowed => "not allowed on this topic",
        PublishStatus.TooLarge => "too large (server limit 100 MB)",
        PublishStatus.RateLimited => "rate limited by the server, try again shortly",
        PublishStatus.Unreachable => "server unreachable",
        PublishStatus.TooManyFiles => $"more than {Publisher.MaxFiles} files",
        PublishStatus.FileError => "a file could not be read",
        PublishStatus.InvalidTarget => "invalid server or topic",
        _ => $"HTTP {HttpStatus}",
    };
}

/// <param name="DeviceTag">Sender id, always sent as the first tag.</param>
/// <param name="Tags">Extra user tags (after the device tag).</param>
public sealed record PublishOptions(string DeviceTag, string? Title = null, IReadOnlyList<string>? Tags = null, int Priority = 3);

/// <summary>
/// Publishes per docs/PROTOCOL.md: short text as a JSON POST to the server root, long text, files
/// and images as a PUT to the topic with title/tags/filename/message as query parameters (UTF-8
/// safe). Every returned id is registered with the <see cref="EchoGuard"/>. The
/// <see cref="HttpClient"/> must not follow redirects (they would carry credentials elsewhere).
/// </summary>
public sealed class Publisher(HttpClient http, EchoGuard echo, ILog? log = null)
{
    public const int MaxJsonTextBytes = 4000;
    public const int MaxCopyActionBytes = 2000;
    public const int MaxFiles = 20;
    public const long MaxFileBytes = 100L * 1024 * 1024;
    public const int PreviewChars = 80;

    public static readonly TimeSpan TextTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(10);

    private readonly ILog _log = log ?? NullLog.Instance;

    public Task<PublishResult> PublishTextAsync(ServerConfig server, string topic, string text, PublishOptions o, CancellationToken ct = default)
    {
        if (Target(server, topic) is not { } baseUri) return Task.FromResult(new PublishResult(PublishStatus.InvalidTarget));
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxJsonTextBytes)
            // Explicit filename = always an attachment, byte-exact (the text path trims whitespace).
            return PutAsync(server, baseUri, topic, new ByteArrayContent(bytes), "clip.txt", Preview(text), o, UploadTimeout, ct);

        var body = new Dictionary<string, object> { ["topic"] = topic, ["message"] = text, ["priority"] = o.Priority };
        if (!string.IsNullOrEmpty(o.Title)) body["title"] = o.Title;
        if (AllTags(o) is { Count: > 0 } tags) body["tags"] = tags;
        if (bytes.Length <= MaxCopyActionBytes)
            body["actions"] = new[] { new Dictionary<string, string> { ["action"] = "copy", ["label"] = "Copy", ["value"] = text } };
        var content = new StringContent(JsonSerializer.Serialize(body, Json.Compact), Encoding.UTF8, "application/json");
        // ntfy accepts JSON bodies only on the root URL.
        return SendAsync(server, new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/")) { Content = content }, TextTimeout, ct);
    }

    /// <summary>An in-memory file, e.g. a clipboard image as <c>clip.png</c>.</summary>
    public Task<PublishResult> PublishBytesAsync(ServerConfig server, string topic, byte[] data, string fileName, PublishOptions o, CancellationToken ct = default)
    {
        if (Target(server, topic) is not { } baseUri) return Task.FromResult(new PublishResult(PublishStatus.InvalidTarget));
        if (data.LongLength > MaxFileBytes) return Task.FromResult(new PublishResult(PublishStatus.TooLarge));
        return PutAsync(server, baseUri, topic, new ByteArrayContent(data), fileName, fileName, o, UploadTimeout, ct);
    }

    /// <summary>
    /// One message per file, in order. Refuses before any upload when there are more than
    /// <see cref="MaxFiles"/> files or one is over 100 MB; stops at the first failure.
    /// </summary>
    public async Task<IReadOnlyList<PublishResult>> PublishFilesAsync(ServerConfig server, string topic, IReadOnlyList<string> paths, PublishOptions o, CancellationToken ct = default)
    {
        if (Target(server, topic) is not { } baseUri) return [new PublishResult(PublishStatus.InvalidTarget)];
        if (paths.Count > MaxFiles) return [new PublishResult(PublishStatus.TooManyFiles)];
        try
        {
            if (paths.Any(p => new FileInfo(p).Length > MaxFileBytes)) return [new PublishResult(PublishStatus.TooLarge)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [new PublishResult(PublishStatus.FileError)];
        }

        var results = new List<PublishResult>();
        foreach (var path in paths)
        {
            PublishResult r;
            try
            {
                await using var file = File.OpenRead(path);
                var name = Path.GetFileName(path);
                r = await PutAsync(server, baseUri, topic, new StreamContent(file), name, name, o, UploadTimeout, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                r = new PublishResult(PublishStatus.FileError);
            }
            results.Add(r);
            if (!r.Ok) break;
        }
        return results;
    }

    /// <summary><c>tags</c> as sent: device tag first, then the user's tags (no duplicates, no blanks).</summary>
    public static List<string> AllTags(PublishOptions o) =>
        new[] { o.DeviceTag }.Concat(o.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>First <see cref="PreviewChars"/> characters plus "…" when cut (never splits a surrogate pair).</summary>
    public static string Preview(string text)
    {
        if (text.Length <= PreviewChars) return text;
        var cut = char.IsHighSurrogate(text[PreviewChars - 1]) ? PreviewChars - 1 : PreviewChars;
        return text[..cut] + "…";
    }

    private static Uri? Target(ServerConfig server, string topic) => TopicConfig.IsValidTopicName(topic) ? server.BaseUri : null;

    private Task<PublishResult> PutAsync(ServerConfig server, Uri baseUri, string topic, HttpContent body, string fileName, string message,
        PublishOptions o, TimeSpan timeout, CancellationToken ct)
    {
        var query = new List<(string Key, string Value)> { ("filename", fileName) };
        if (!string.IsNullOrEmpty(o.Title)) query.Add(("title", o.Title));
        if (AllTags(o) is { Count: > 0 } tags) query.Add(("tags", string.Join(',', tags)));
        query.Add(("message", message));
        var url = $"{baseUri.AbsoluteUri.TrimEnd('/')}/{topic}?" + string.Join('&', query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
        body.Headers.ContentType ??= new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url)) { Content = body };
        request.Headers.TryAddWithoutValidation("X-Priority", o.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return SendAsync(server, request, timeout, ct);
    }

    private async Task<PublishResult> SendAsync(ServerConfig server, HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var _ = request;
        if (server.Auth.HeaderValue is { } auth) request.Headers.TryAddWithoutValidation("Authorization", auth);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, cts.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"publish failed: HTTP {status}");
                return new PublishResult(status switch
                {
                    401 or 403 => PublishStatus.NotAllowed,
                    413 => PublishStatus.TooLarge,
                    429 => PublishStatus.RateLimited,
                    _ => PublishStatus.HttpError,
                }, HttpStatus: status);
            }
            var id = ReadId(await response.Content.ReadAsStringAsync(cts.Token));
            if (id is not null) echo.Register(id);
            _log.Info($"published message {id}");
            return new PublishResult(PublishStatus.Sent, id, status);
        }
        catch (HttpRequestException e)
        {
            _log.Warn($"publish failed: {e.HttpRequestError}");
            return new PublishResult(PublishStatus.Unreachable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn("publish timed out");
            return new PublishResult(PublishStatus.Unreachable);
        }
    }

    private static string? ReadId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Get<string?>("id", null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
