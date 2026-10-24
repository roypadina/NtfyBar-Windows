using System.Text;
using System.Text.Json;
using NtfyBar.Core;

namespace NtfyBar.Core.Tests;

public class PublisherTests
{
    private static readonly ServerConfig Server = new() { Url = "https://ntfy.example", Auth = ServerAuth.WithBasic("someone", "pw") };
    private static readonly PublishOptions Opts = new("ntfybar-win", "Test PC", ["work"], 4);

    private sealed class Rig
    {
        public readonly EchoGuard Echo = new();
        public readonly FakeHandler Handler;
        public readonly Publisher Publisher;

        public Rig(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
        {
            Handler = new FakeHandler(respond ?? (_ => FakeHandler.Json("""{"id":"new-id","time":1,"event":"message","topic":"example-topic"}""")));
            Publisher = new Publisher(new HttpClient(Handler), Echo);
        }

        public Captured Only => Assert.Single(Handler.Requests);
    }

    [Fact]
    public async Task ShortTextIsJsonToTheRootWithCopyAction()
    {
        var rig = new Rig();
        var r = await rig.Publisher.PublishTextAsync(Server, "example-topic", "hello\nworld", Opts);

        Assert.True(r.Ok);
        Assert.Equal("new-id", r.Id);
        var req = rig.Only;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://ntfy.example/", req.Uri.AbsoluteUri);
        Assert.Equal(Server.Auth.HeaderValue, req.Header("Authorization"));
        Assert.StartsWith("application/json", req.Header("Content-Type"));
        var json = JsonDocument.Parse(req.Body).RootElement;
        Assert.Equal("example-topic", json.GetProperty("topic").GetString());
        Assert.Equal("hello\nworld", json.GetProperty("message").GetString());
        Assert.Equal("Test PC", json.GetProperty("title").GetString());
        Assert.Equal(4, json.GetProperty("priority").GetInt32());
        Assert.Equal(["ntfybar-win", "work"], json.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        var action = Assert.Single(json.GetProperty("actions").EnumerateArray().ToList());
        Assert.Equal(("copy", "Copy", "hello\nworld"), (action.GetProperty("action").GetString(), action.GetProperty("label").GetString(), action.GetProperty("value").GetString()));
        Assert.True(rig.Echo.IsOwnEcho(Msg.Make(id: "new-id"), deviceTag: null), "returned id registered as own echo");
    }

    [Fact]
    public async Task TextOver2000BytesHasNoCopyActionAndHebrewStaysRawUtf8()
    {
        var rig = new Rig();
        var text = new string('ש', 1500);  // 3000 bytes in UTF-8
        await rig.Publisher.PublishTextAsync(Server, "example-topic", text, Opts with { Title = "", Tags = null });

        var req = rig.Only;
        var json = JsonDocument.Parse(req.Body).RootElement;
        Assert.False(json.TryGetProperty("actions", out _));
        Assert.False(json.TryGetProperty("title", out _));
        Assert.Equal(["ntfybar-win"], json.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(text, json.GetProperty("message").GetString());
        Assert.DoesNotContain("\\u", req.BodyText);
        Assert.True(req.Body!.Length < 3200, "no \\uXXXX escaping, no indentation");
    }

    [Fact]
    public async Task ExactlyAtTheLimitsStillJsonAndCopy()
    {
        var rig = new Rig();
        await rig.Publisher.PublishTextAsync(Server, "example-topic", new string('a', 2000), Opts);
        await rig.Publisher.PublishTextAsync(Server, "example-topic", new string('a', 4000), Opts);
        var (two, four) = (rig.Handler.Requests[0], rig.Handler.Requests[1]);
        Assert.True(JsonDocument.Parse(two.Body).RootElement.TryGetProperty("actions", out _));
        Assert.Equal(HttpMethod.Post, four.Method);
        Assert.False(JsonDocument.Parse(four.Body).RootElement.TryGetProperty("actions", out _));
    }

    [Fact]
    public async Task LongTextIsPutAsClipTxtByteExact()
    {
        var rig = new Rig();
        var text = "  leading and trailing whitespace kept  " + new string('x', 4000) + "\n";
        await rig.Publisher.PublishTextAsync(Server, "example-topic", text, Opts);

        var req = rig.Only;
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal("/example-topic", req.Uri.AbsolutePath);
        Assert.Equal(Encoding.UTF8.GetBytes(text), req.Body);
        var q = req.Query;
        Assert.Equal("clip.txt", q["filename"]);
        Assert.Equal("Test PC", q["title"]);
        Assert.Equal("ntfybar-win,work", q["tags"]);
        Assert.Equal(text[..80] + "…", q["message"]);
        Assert.Equal("4", req.Header("X-Priority"));
        Assert.Equal(Server.Auth.HeaderValue, req.Header("Authorization"));
    }

    [Fact]
    public async Task FileIsPutWithNameAsMessageAndUnicodeSafeQuery()
    {
        using var dir = new TempDir();
        var path = dir.File("דוח Q3 & more.pdf");
        File.WriteAllBytes(path, [9, 8, 7]);
        var rig = new Rig();
        var results = await rig.Publisher.PublishFilesAsync(Server, "example-topic", [path], Opts with { Title = "מחשב 😀 & co", Tags = ["a b", "c"] });

        Assert.True(Assert.Single(results).Ok);
        var req = rig.Only;
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal([9, 8, 7], req.Body);
        Assert.DoesNotContain(" ", req.Uri.Query);
        Assert.DoesNotContain("ד", req.Uri.AbsoluteUri);  // percent-encoded, not raw
        var q = req.Query;
        Assert.Equal(4, q.Count);  // the & inside values did not split parameters
        Assert.Equal("דוח Q3 & more.pdf", q["filename"]);
        Assert.Equal("דוח Q3 & more.pdf", q["message"]);
        Assert.Equal("מחשב 😀 & co", q["title"]);
        Assert.Equal("ntfybar-win,a b,c", q["tags"]);
    }

    [Fact]
    public async Task ImageBytesGoAsClipPng()
    {
        var rig = new Rig();
        await rig.Publisher.PublishBytesAsync(Server, "example-topic", [137, 80, 78, 71], "clip.png", Opts);
        var q = rig.Only.Query;
        Assert.Equal(("clip.png", "clip.png"), (q["filename"], q["message"]));
        Assert.Equal([137, 80, 78, 71], rig.Only.Body);
    }

    [Fact]
    public async Task TooManyFilesOrTooBigAreRefusedBeforeAnyUpload()
    {
        using var dir = new TempDir();
        var small = dir.File("small.txt");
        File.WriteAllText(small, "x");
        var big = dir.File("big.bin");
        using (var fs = File.Create(big)) fs.SetLength(Publisher.MaxFileBytes + 1);
        var rig = new Rig();

        var many = await rig.Publisher.PublishFilesAsync(Server, "example-topic", Enumerable.Repeat(small, 21).ToList(), Opts);
        var tooBig = await rig.Publisher.PublishFilesAsync(Server, "example-topic", [small, big], Opts);
        var missing = await rig.Publisher.PublishFilesAsync(Server, "example-topic", [dir.File("gone.txt")], Opts);

        Assert.Equal(PublishStatus.TooManyFiles, Assert.Single(many).Status);
        Assert.Equal(PublishStatus.TooLarge, Assert.Single(tooBig).Status);
        Assert.Equal(PublishStatus.FileError, Assert.Single(missing).Status);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task TwentyFilesAreFineAndAFailureStopsTheRest()
    {
        using var dir = new TempDir();
        var paths = Enumerable.Range(1, 20).Select(i => { var p = dir.File($"f{i}.txt"); File.WriteAllText(p, $"{i}"); return p; }).ToList();
        var calls = 0;
        var rig = new Rig(_ => ++calls == 3 ? FakeHandler.Status(429) : FakeHandler.Json($$"""{"id":"id{{calls}}"}"""));
        var results = await rig.Publisher.PublishFilesAsync(Server, "example-topic", paths, Opts);
        Assert.Equal([PublishStatus.Sent, PublishStatus.Sent, PublishStatus.RateLimited], results.Select(r => r.Status));
        Assert.Equal(["f1.txt", "f2.txt", "f3.txt"], rig.Handler.Requests.Select(r => r.Query["filename"]));
    }

    [Theory]
    [InlineData(401, PublishStatus.NotAllowed)]
    [InlineData(403, PublishStatus.NotAllowed)]
    [InlineData(413, PublishStatus.TooLarge)]
    [InlineData(429, PublishStatus.RateLimited)]
    [InlineData(500, PublishStatus.HttpError)]
    [InlineData(302, PublishStatus.HttpError)]
    public async Task HttpErrorsMap(int code, PublishStatus expected)
    {
        var rig = new Rig(_ => FakeHandler.Status(code));
        var r = await rig.Publisher.PublishTextAsync(Server, "example-topic", "x", Opts);
        Assert.Equal((expected, code), (r.Status, r.HttpStatus));
        Assert.False(string.IsNullOrEmpty(r.Describe()));
    }

    [Fact]
    public async Task NetworkFailureIsUnreachable()
    {
        var handler = new FakeHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("refused")));
        var r = await new Publisher(new HttpClient(handler), new EchoGuard()).PublishTextAsync(Server, "example-topic", "x", Opts);
        Assert.Equal(PublishStatus.Unreachable, r.Status);
        Assert.Equal("server unreachable", r.Describe());
    }

    [Theory]
    [InlineData("https://ntfy.example", "bad,topic")]
    [InlineData("https://ntfy.example", "")]
    [InlineData("not a url", "example-topic")]
    public async Task InvalidTargetsSendNothing(string url, string topic)
    {
        var rig = new Rig();
        var r = await rig.Publisher.PublishTextAsync(new ServerConfig { Url = url }, topic, "x", Opts);
        Assert.Equal(PublishStatus.InvalidTarget, r.Status);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task TokenAuthAndNoAuth()
    {
        var rig = new Rig();
        await rig.Publisher.PublishTextAsync(new ServerConfig { Url = "https://ntfy.example", Auth = ServerAuth.WithToken("tk_x") }, "example-topic", "x", Opts);
        await rig.Publisher.PublishTextAsync(new ServerConfig { Url = "https://ntfy.example" }, "example-topic", "x", Opts);
        Assert.Equal("Bearer tk_x", rig.Handler.Requests[0].Header("Authorization"));
        Assert.Null(rig.Handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public void DeviceTagComesFirstWithoutDuplicatesOrBlanks()
    {
        Assert.Equal(["ntfybar-win", "a"], Publisher.AllTags(new PublishOptions("ntfybar-win", Tags: [" a ", "ntfybar-win", "", "a"])));
        Assert.Equal(["a"], Publisher.AllTags(new PublishOptions("", Tags: ["a"])));
    }

    [Fact]
    public void PreviewCutsAt80WithoutSplittingSurrogates()
    {
        Assert.Equal("short", Publisher.Preview("short"));
        Assert.Equal(new string('a', 80), Publisher.Preview(new string('a', 80)));
        Assert.Equal(new string('a', 80) + "…", Publisher.Preview(new string('a', 81)));
        var emoji = Publisher.Preview(new string('a', 79) + "😀😀");
        Assert.Equal(new string('a', 79) + "…", emoji);
    }
}
