using System.Collections.Concurrent;
using System.Text;
using NtfyBar.Core;

namespace NtfyBar.Core.Tests;

public class StreamParsingTests
{
    [Fact]
    public void ParsesAFullMessage()
    {
        var m = NtfyMessage.Parse("""
            {"id":"abc","time":1700000000,"expires":1700043200,"event":"message","topic":"example-topic","title":"Laptop","message":"photo.jpg",
             "priority":4,"tags":["ntfybar-win","x"],"click":"https://example.org",
             "actions":[{"id":"a1","action":"copy","label":"Copy","value":"hi","clear":true},{"action":"view","label":"Open","url":"https://example.org"},{"label":"no action"}],
             "attachment":{"name":"photo.jpg","type":"image/jpeg","size":12345,"expires":1700010800,"url":"https://ntfy.example/file/abc.jpg"}}
            """)!;
        Assert.Equal(("abc", 1700000000L, "message", "example-topic", "Laptop", "photo.jpg", 4), (m.Id, m.Time, m.Event, m.Topic, m.Title, m.Message, m.PriorityValue));
        Assert.Equal(["ntfybar-win", "x"], m.Tags);
        Assert.Equal(2, m.Actions.Count);
        Assert.Equal(new NtfyAction("copy", "Copy", null, "hi"), m.Actions[0]);
        Assert.Equal(new NtfyAttachment("photo.jpg", "https://ntfy.example/file/abc.jpg", "image/jpeg", 12345, 1700010800), m.Attachment);
    }

    [Theory]
    [InlineData("""{"id":"o","time":1,"event":"open","topic":"a,b"}""", "open")]
    [InlineData("""{"id":"k","time":1,"event":"keepalive","topic":"a"}""", "keepalive")]
    [InlineData("""{"id":"p","time":1,"event":"poll_request","topic":"a","message":"x"}""", "poll_request")]
    public void ParsesControlEvents(string line, string ev) => Assert.Equal(ev, NtfyMessage.Parse(line)!.Event);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{oops")]
    [InlineData("[]")]
    [InlineData("""{"time":1,"event":"message"}""")]
    [InlineData("""{"id":"x","event":"message"}""")]
    [InlineData("""{"id":"x","time":1}""")]
    public void RejectsBlankMalformedOrIncompleteLines(string line) => Assert.Null(NtfyMessage.Parse(line));

    [Fact]
    public void OddFieldDoesNotDropTheMessage()
    {
        var m = NtfyMessage.Parse("""{"id":"x","time":1,"event":"message","priority":"urgent","tags":"oops","attachment":"nope","message":"kept"}""")!;
        Assert.Equal("kept", m.Message);
        Assert.Equal(3, m.PriorityValue);
        Assert.Empty(m.Tags);
        Assert.Null(m.Attachment);
    }

    [Fact]
    public void BuildUriSortsTopicsAndAddsSinceOnlyWhenKnown()
    {
        var b = new Uri("https://ntfy.example/");
        Assert.Equal("https://ntfy.example/a2,b/json", NtfyStream.BuildUri(b, ["b", "a2"], null).AbsoluteUri);
        Assert.Equal("https://ntfy.example/t/json", NtfyStream.BuildUri(b, ["t"], 0).AbsoluteUri);
        Assert.Equal("https://ntfy.example/t/json?since=1700000000", NtfyStream.BuildUri(b, ["t"], 1700000000).AbsoluteUri);
        Assert.Equal("https://ntfy.example/sub/t/json", NtfyStream.BuildUri(new Uri("https://ntfy.example/sub"), ["t"], null).AbsoluteUri);
    }

    [Fact]
    public void BackoffDoublesCapsAndJitters()
    {
        var t = StreamTiming.Default;
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], Enumerable.Range(0, 8).Select(a => t.Backoff(a, 0).TotalSeconds));
        Assert.Equal(66, t.Backoff(100, 1).TotalSeconds, 3);
        Assert.Equal(1.05, t.Backoff(0, 0.5).TotalSeconds, 3);
    }

    [Fact]
    public async Task LineReaderSplitsOnNewlineOnlyAcrossChunksAndDropsOverlongLines()
    {
        var s = new PushStream();
        s.SendRaw(Encoding.UTF8.GetBytes("ab"));
        s.SendRaw(Encoding.UTF8.GetBytes("c\u2028d\u0085e\r\n"));
        s.SendRaw(Encoding.UTF8.GetBytes(new string('x', 40) + "\nok\n"));
        s.SendRaw(Encoding.UTF8.GetBytes(new string('y', 25)));
        s.SendRaw(Encoding.UTF8.GetBytes(new string('y', 25) + "\nlast\n"));
        s.SendRaw(Encoding.UTF8.GetBytes(new string('z', 35)));
        s.SendRaw(Encoding.UTF8.GetBytes("zz\nend\n"));
        s.End();
        var lines = new List<string?>();
        await foreach (var l in NtfyStream.ReadLinesAsync(s, 30, CancellationToken.None)) lines.Add(l);
        Assert.Equal(["abc\u2028d\u0085e\r", null, "ok", null, "last", null, "end"], lines);
    }
}

public class StreamConnectionTests
{
    private static readonly StreamTiming Fast = new(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20));

    private sealed class Harness
    {
        public readonly FakeNtfy Server = new();
        public readonly ConcurrentQueue<(NtfyMessage Message, bool CatchUp)> Received = new();
        public readonly ConcurrentQueue<StreamStatus> Statuses = new();
        public readonly NtfyStream Stream;

        public Harness(string? auth = null, StreamCursor? cursor = null, StreamTiming? timing = null)
        {
            Stream = new NtfyStream(new HttpClient(Server.Handler) { Timeout = Timeout.InfiniteTimeSpan }, new Uri("https://ntfy.example"),
                auth, ["example-topic", "other-topic"], cursor ?? new StreamCursor(), timing: timing ?? Fast, jitter: () => 0);
            Stream.MessageReceived += (m, c) => Received.Enqueue((m, c));
            Stream.StatusChanged += s => Statuses.Enqueue(s);
        }
    }

    [Fact]
    public async Task DeliversLiveAndCatchUpWithAuthHeader()
    {
        var h = new Harness(auth: ServerAuth.WithBasic("someone", "pw").HeaderValue);
        var body = h.Server.NextStream();
        h.Stream.Start();
        body.Send(Msg.Open(1000));
        body.Send(Msg.Line("old", 990));
        body.Send(Msg.Keepalive(1001));
        body.Send(Msg.Line("new", 1002, message: "live"));
        body.Send(Msg.Line("foreign", 1003, topic: "not-mine"));
        Assert.True(await Wait.Until(() => h.Received.Count == 2));
        h.Stream.Stop();

        Assert.Equal([("old", true), ("new", false)], h.Received.Select(r => (r.Message.Id, r.CatchUp)));
        var request = Assert.Single(h.Server.StreamRequests);
        Assert.Equal("https://ntfy.example/example-topic,other-topic/json", request.Uri.AbsoluteUri);
        Assert.Equal(ServerAuth.WithBasic("someone", "pw").HeaderValue, request.Header("Authorization"));
        Assert.Contains(h.Statuses, s => s.State == StreamState.Connected);
        Assert.Equal(1002, h.Stream.Cursor.Time);
    }

    [Fact]
    public async Task ReconnectResumesFromCursorAndDropsReplayedDuplicates()
    {
        var h = new Harness();
        var first = h.Server.NextStream();
        var second = h.Server.NextStream();
        h.Stream.Start();
        first.Send(Msg.Open(1000));
        first.Send(Msg.Line("a", 1005));
        first.End();  // server closes the connection
        Assert.True(await Wait.Until(() => h.Server.StreamRequests.Count == 2));
        second.Send(Msg.Open(1010));
        second.Send(Msg.Line("a", 1005));  // since=1005 replays the same second
        second.Send(Msg.Line("b", 1005));
        second.Send(Msg.Line("c", 1011));
        Assert.True(await Wait.Until(() => h.Received.Count == 3));
        h.Stream.Stop();

        Assert.EndsWith("/json?since=1005", h.Server.StreamRequests[1].Uri.AbsoluteUri);
        Assert.Equal(["a", "b", "c"], h.Received.Select(r => r.Message.Id));
        Assert.Contains(h.Statuses, s => s.State == StreamState.Reconnecting);
        Assert.Equal(1011, h.Stream.Cursor.Time);
        Assert.Equal(["c"], h.Stream.Cursor.Ids);
    }

    [Fact]
    public async Task PersistedCursorIsUsedOnFirstConnect()
    {
        var h = new Harness(cursor: new StreamCursor { Time = 1_700_000_000, Ids = ["seen"] });
        var body = h.Server.NextStream();
        h.Stream.Start();
        body.Send(Msg.Open(1_700_000_100));
        body.Send(Msg.Line("seen", 1_700_000_000));
        body.Send(Msg.Line("unseen", 1_700_000_000));
        Assert.True(await Wait.Until(() => h.Received.Count == 1));
        await Task.Delay(50);
        h.Stream.Stop();
        Assert.EndsWith("?since=1700000000", h.Server.StreamRequests[0].Uri.AbsoluteUri);
        Assert.Equal("unseen", Assert.Single(h.Received).Message.Id);
    }

    [Theory]
    [InlineData(401, StreamState.AuthFailed)]
    [InlineData(403, StreamState.AuthFailed)]
    [InlineData(301, StreamState.Redirected)]
    [InlineData(307, StreamState.Redirected)]
    public async Task AuthErrorsAndRedirectsStopForGood(int code, StreamState expected)
    {
        var h = new Harness();
        h.Server.NextStatus(code);
        h.Stream.Start();
        Assert.True(await Wait.Until(() => h.Stream.Status.State == expected));
        await Task.Delay(100);  // backoff would have retried several times by now
        Assert.Single(h.Server.StreamRequests);
        Assert.Equal(code, h.Stream.Status.HttpStatus);

        h.Stream.ReconnectNow();  // wake/network change must not retry bad credentials either
        await Task.Delay(50);
        Assert.Single(h.Server.StreamRequests);
    }

    [Fact]
    public async Task ServerErrorsAndNetworkFailuresRetryWithBackoff()
    {
        var calls = 0;
        var handler = new FakeHandler((_, _) => ++calls switch
        {
            1 => Task.FromResult(FakeHandler.Status(500)),
            2 => Task.FromException<HttpResponseMessage>(new HttpRequestException("no route")),
            _ => Task.FromResult(FakeHandler.Stream(new PushStream())),
        });
        var stream = new NtfyStream(new HttpClient(handler), new Uri("https://ntfy.example"), null, ["example-topic"], new StreamCursor(), timing: Fast);
        stream.Start();
        Assert.True(await Wait.Until(() => handler.Requests.Count == 3));
        stream.Stop();
        Assert.Equal(StreamState.Stopped, stream.Status.State);
    }

    [Fact]
    public async Task WatchdogReconnectsASilentConnection()
    {
        var h = new Harness(timing: Fast with { KeepaliveTimeout = TimeSpan.FromMilliseconds(150) });
        var body = h.Server.NextStream();
        h.Stream.Start();
        body.Send(Msg.Open(1000));  // then silence: no keepalives
        Assert.True(await Wait.Until(() => h.Server.StreamRequests.Count >= 2, 3000));
        h.Stream.Stop();
    }

    [Fact]
    public async Task KeepalivesHoldTheConnectionOpen()
    {
        var h = new Harness(timing: Fast with { KeepaliveTimeout = TimeSpan.FromMilliseconds(300) });
        var body = h.Server.NextStream();
        h.Stream.Start();
        body.Send(Msg.Open(1000));
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(100);
            body.Send(Msg.Keepalive(1000 + i));
        }
        Assert.Single(h.Server.StreamRequests);
        h.Stream.Stop();
    }

    [Fact]
    public async Task AHandlerThatThrowsDoesNotKillTheStream()
    {
        var h = new Harness();
        var seen = 0;
        h.Stream.MessageReceived += (_, _) => { if (Interlocked.Increment(ref seen) == 1) throw new InvalidOperationException("ui bug"); };
        var body = h.Server.NextStream();
        h.Stream.Start();
        body.Send(Msg.Open(1000));
        body.Send(Msg.Line("a", 1001));
        body.Send(Msg.Line("b", 1002));
        Assert.True(await Wait.Until(() => h.Received.Count == 2));
        h.Stream.Stop();
    }
}
