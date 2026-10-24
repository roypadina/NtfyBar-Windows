using System.Runtime.CompilerServices;
using System.Text;

namespace NtfyBar.Core;

public enum StreamState
{
    Stopped,
    Connecting,
    Connected,
    /// <summary>Waiting for the next attempt; see <see cref="StreamStatus.RetryAt"/>.</summary>
    Reconnecting,
    /// <summary>401/403: stays stopped until the config changes (retrying bad credentials hammers the server).</summary>
    AuthFailed,
    /// <summary>3xx: not followed (it would carry credentials elsewhere); the user must enter the final URL.</summary>
    Redirected,
}

public sealed record StreamStatus(StreamState State, int HttpStatus = 0, DateTimeOffset? RetryAt = null)
{
    public static readonly StreamStatus Stopped = new(StreamState.Stopped);

    public bool IsTerminal => State is StreamState.AuthFailed or StreamState.Redirected;

    /// <summary>One line for the tray status item.</summary>
    public string Describe() => State switch
    {
        StreamState.Connected => "Connected",
        StreamState.Connecting => "Connecting…",
        StreamState.Reconnecting => "Reconnecting…",
        StreamState.AuthFailed => $"Sign-in rejected (HTTP {HttpStatus}) — check Settings",
        StreamState.Redirected => $"Server redirected (HTTP {HttpStatus}) — enter the final URL in Settings",
        _ => "Stopped",
    };
}

/// <param name="KeepaliveTimeout">ntfy sends a keepalive every 45 s; two missed ones = dead connection.</param>
public sealed record StreamTiming(TimeSpan KeepaliveTimeout, TimeSpan BackoffBase, TimeSpan BackoffCap)
{
    public static readonly StreamTiming Default = new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));

    /// <summary>1, 2, 4 … capped, plus up to 10 % jitter (0..1) so clients don't reconnect in lockstep.</summary>
    public TimeSpan Backoff(int attempt, double jitter)
    {
        var raw = BackoffBase.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempt, 0, 30));
        var d = Math.Min(BackoffCap.TotalMilliseconds, raw);
        return TimeSpan.FromMilliseconds(d + d * 0.1 * Math.Clamp(jitter, 0, 1));
    }
}

/// <summary>
/// One long-lived <c>GET {server}/{t1,t2,…}/json?since=…</c> per server. Reconnects with
/// exponential backoff, resumes from <see cref="Cursor"/>, drops replayed duplicates, and stops for
/// good on 401/403/3xx. Events run on the <see cref="SynchronizationContext"/> that called
/// <see cref="Start"/> (the UI thread in the app), on pool threads otherwise.
/// The <see cref="HttpClient"/> must not follow redirects and must have an infinite timeout.
/// </summary>
public sealed class NtfyStream(
    HttpClient http, Uri baseUri, string? authHeader, IEnumerable<string> topics, StreamCursor cursor,
    ILog? log = null, StreamTiming? timing = null, Func<double>? jitter = null)
{
    /// <summary>A longer line is dropped whole, so a hostile server can't grow memory without bound.</summary>
    public const int MaxLineBytes = 256 * 1024;

    private readonly HashSet<string> _topics = topics.Where(TopicConfig.IsValidTopicName).ToHashSet(StringComparer.Ordinal);
    private readonly ILog _log = log ?? NullLog.Instance;
    private readonly StreamTiming _timing = timing ?? StreamTiming.Default;
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;
    private CancellationTokenSource? _run;
    private int _attempt;

    public StreamStatus Status { get; private set; } = StreamStatus.Stopped;
    /// <summary>Advanced before each <see cref="MessageReceived"/>; persist it to resume after a restart.</summary>
    public StreamCursor Cursor { get; } = cursor;

    public event Action<StreamStatus>? StatusChanged;
    /// <summary>(message, isCatchUp): catch-up = older than the server's <c>open</c> event, i.e. cached while we were away.</summary>
    public event Action<NtfyMessage, bool>? MessageReceived;

    public void Start()
    {
        if (_run is not null) return;
        _run = new CancellationTokenSource();
        _ = RunAsync(_run.Token);
    }

    public void Stop()
    {
        _run?.Cancel();
        _run = null;
        SetStatus(StreamStatus.Stopped);
    }

    /// <summary>Wake from sleep / network change: drop the possibly dead connection and retry now. No-op after 401/403/3xx.</summary>
    public void ReconnectNow()
    {
        if (Status.IsTerminal) return;
        _run?.Cancel();
        _run = null;
        _attempt = 0;
        Start();
    }

    /// <summary><c>{server}/{t1,t2,…}/json[?since=&lt;unix&gt;]</c>.</summary>
    public static Uri BuildUri(Uri baseUri, IEnumerable<string> topics, long? since)
    {
        var url = $"{baseUri.AbsoluteUri.TrimEnd('/')}/{string.Join(',', topics.Order(StringComparer.Ordinal))}/json";
        return new Uri(since is > 0 ? $"{url}?since={since}" : url);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (_topics.Count == 0) return;
        while (!ct.IsCancellationRequested)
        {
            SetStatus(new StreamStatus(StreamState.Connecting));
            var (terminal, httpStatus) = await ConnectOnceAsync(ct);
            if (ct.IsCancellationRequested) return;
            if (terminal is { } state)
            {
                _log.Error($"stream stopped: HTTP {httpStatus}; not reconnecting until settings change");
                SetStatus(new StreamStatus(state, httpStatus));
                return;
            }
            var delay = _timing.Backoff(_attempt++, _jitter());
            SetStatus(new StreamStatus(StreamState.Reconnecting, RetryAt: DateTimeOffset.Now + delay));
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<(StreamState? Terminal, int HttpStatus)> ConnectOnceAsync(CancellationToken ct)
    {
        // Watchdog: every received line pushes the deadline out again.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(_timing.KeepaliveTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(baseUri, _topics, Cursor.Time));
            if (authHeader is not null) request.Headers.TryAddWithoutValidation("Authorization", authHeader);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
            var status = (int)response.StatusCode;
            if (status is 401 or 403) return (StreamState.AuthFailed, status);
            if (status is >= 300 and < 400) return (StreamState.Redirected, status);
            if (status != 200)
            {
                _log.Warn($"stream HTTP {status}");
                return (null, status);
            }
            await using var body = await response.Content.ReadAsStreamAsync(watchdog.Token);
            long? openTime = null;
            await foreach (var line in ReadLinesAsync(body, MaxLineBytes, watchdog.Token))
            {
                watchdog.CancelAfter(_timing.KeepaliveTimeout);
                if (line is null)
                {
                    _log.Warn("dropped an over-long stream line");
                    continue;
                }
                var m = NtfyMessage.Parse(line);
                if (m is null)
                {
                    if (!string.IsNullOrWhiteSpace(line)) _log.Warn($"malformed stream line ({line.Length} chars)");
                    continue;
                }
                switch (m.Event)
                {
                    case "open":
                        openTime = m.Time;
                        _attempt = 0;
                        _log.Info($"stream connected ({_topics.Count} topics)");
                        SetStatus(new StreamStatus(StreamState.Connected));
                        break;
                    case "message" when _topics.Contains(m.Topic) && !Cursor.HasSeen(m):
                        Cursor.Advance(m);
                        Raise(m, openTime is { } open && m.Time < open);
                        break;
                    // keepalive only feeds the watchdog; poll_request, message_delete, message_clear: ignored.
                }
            }
            _log.Info("stream closed by the server");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Info($"nothing received for {_timing.KeepaliveTimeout.TotalSeconds:0} s; reconnecting");
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (Exception e)
        {
            // Network errors mostly; anything else must not kill the stream either. Type only: messages can hold the host.
            _log.Info($"stream ended: {e.GetType().Name} {(e as HttpRequestException)?.HttpRequestError}");
        }
        return (null, 0);
    }

    private void Raise(NtfyMessage m, bool isCatchUp) => Guard(() => MessageReceived?.Invoke(m, isCatchUp));

    private void SetStatus(StreamStatus s)
    {
        if (s == Status) return;
        Status = s;
        Guard(() => StatusChanged?.Invoke(s));
    }

    /// <summary>A bug in an event handler must not kill the stream.</summary>
    private void Guard(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception e)
        {
            _log.Error($"event handler threw {e.GetType().Name}");
        }
    }

    /// <summary>
    /// Splits on <c>\n</c> only. Yields null for a line longer than <paramref name="maxLineBytes"/>
    /// (dropped whole).
    /// </summary>
    internal static async IAsyncEnumerable<string?> ReadLinesAsync(Stream s, int maxLineBytes, [EnumeratorCancellation] CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var line = new MemoryStream();
        var overflow = false;
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            var start = 0;
            for (var i = 0; i < n; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                var tooLong = overflow || line.Length + (i - start) > maxLineBytes;
                if (!tooLong) line.Write(buf, start, i - start);
                yield return tooLong ? null : Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                line.SetLength(0);
                overflow = false;
                start = i + 1;
            }
            if (overflow) continue;
            if (line.Length + (n - start) > maxLineBytes)
            {
                overflow = true;
                line.SetLength(0);
            }
            else
            {
                line.Write(buf, start, n - start);
            }
        }
    }
}
