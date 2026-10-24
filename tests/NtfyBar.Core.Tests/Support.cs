using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using NtfyBar.Core;

namespace NtfyBar.Core.Tests;

internal sealed record Captured(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, byte[]? Body)
{
    public string BodyText => Body is null ? "" : Encoding.UTF8.GetString(Body);
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    /// <summary>Decoded query parameters.</summary>
    public Dictionary<string, string> Query => Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
}

/// <summary>Records every request (body read eagerly) and answers via <c>respond</c>.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly ConcurrentQueue<Captured> _requests = new();

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((r, _) => Task.FromResult(respond(r))) { }

    public IReadOnlyList<Captured> Requests => _requests.ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        _requests.Enqueue(new Captured(request.Method, request.RequestUri!, headers, body));
        return await respond(request, ct);
    }

    public static HttpResponseMessage Status(int code, string body = "") =>
        new((HttpStatusCode)code) { Content = new StringContent(body) };

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Stream(Stream body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(body) };
}

/// <summary>Endless response body the test writes NDJSON lines into; honours read cancellation.</summary>
internal sealed class PushStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[] _current = [];
    private int _pos;

    public void Send(string line) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));
    public void SendRaw(byte[] bytes) => _chunks.Writer.TryWrite(bytes);
    public void End() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pos >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(ct)) return 0;
            _current = await _chunks.Reader.ReadAsync(ct);
            _pos = 0;
        }
        var n = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsMemory(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Fake ntfy server: each GET …/json takes the next scripted response; when the script is empty it
/// answers with a body that never sends anything (no busy reconnect loop). Anything else is a
/// successful publish answering <c>{"id":"published-N"}</c>.
/// </summary>
internal sealed class FakeNtfy
{
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _script = new();
    private int _published;

    public FakeNtfy() => Handler = new FakeHandler(r =>
        !r.RequestUri!.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
            ? FakeHandler.Json($$"""{"id":"published-{{Interlocked.Increment(ref _published)}}"}""")
            : _script.TryDequeue(out var next) ? next() : FakeHandler.Stream(new PushStream()));

    public FakeHandler Handler { get; }
    public IReadOnlyList<Captured> StreamRequests => Handler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)).ToList();

    public PushStream NextStream()
    {
        var s = new PushStream();
        _script.Enqueue(() => FakeHandler.Stream(s));
        return s;
    }

    public void NextStatus(int code) => _script.Enqueue(() => FakeHandler.Status(code));
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ntfybar-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Reversible fake for DPAPI.</summary>
internal sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
    public byte[] Unprotect(byte[] cipher) => Protect(cipher);
}

internal sealed class CapturingLog : ILog
{
    private readonly ConcurrentQueue<string> _lines = new();
    public IReadOnlyList<string> Lines => _lines.ToList();
    public void Write(LogLevel level, string message) => _lines.Enqueue($"{level} {message}");
}

internal static class Wait
{
    public static async Task<bool> Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }
}

internal static class Msg
{
    public static string Line(string id, long time, string topic = "example-topic", string? message = "hello", string? extra = null) =>
        $$"""{"id":"{{id}}","time":{{time}},"event":"message","topic":"{{topic}}"{{(message is null ? "" : $",\"message\":\"{message}\"")}}{{extra ?? ""}}}""";

    public static string Open(long time, string topic = "example-topic") =>
        $$"""{"id":"o{{time}}","time":{{time}},"event":"open","topic":"{{topic}}"}""";

    public static string Keepalive(long time) => $$"""{"id":"k{{time}}","time":{{time}},"event":"keepalive","topic":"example-topic"}""";

    public static NtfyMessage Make(string id = "m1", long time = 1_700_000_000, string? message = "hello", NtfyAttachment? attachment = null, params string[] tags) =>
        new() { Id = id, Time = time, Event = "message", Topic = "example-topic", Message = message, Attachment = attachment, Tags = tags };
}
