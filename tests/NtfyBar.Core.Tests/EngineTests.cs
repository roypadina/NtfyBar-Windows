using System.Collections.Concurrent;
using System.Text.Json;
using NtfyBar.Core;

namespace NtfyBar.Core.Tests;

public class EngineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
    private static readonly long T = Now.ToUnixTimeSeconds();

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeNtfy Server = new();
        public readonly ConfigStore Store;
        public readonly AppConfig Config = new();
        public readonly TopicConfig Clipboard;
        public readonly TopicConfig Other;
        public readonly CapturingLog Log = new();
        public readonly ConcurrentQueue<IncomingMessage> Got = new();
        public readonly ConcurrentQueue<MissedSummary> Summaries = new();
        public Engine Engine = null!;

        public Rig()
        {
            Store = new ConfigStore(Dir.Path, new XorProtector());
            Config.Servers[0].Url = "https://ntfy.example";
            Config.Servers[0].Auth = ServerAuth.WithBasic("someone", "secret-pass");
            Config.Publish.Title = "Test PC";
            Clipboard = new TopicConfig { ServerId = Config.Servers[0].Id, Topic = "example-topic", Label = "Clipboard" };
            Other = new TopicConfig { ServerId = Config.Servers[0].Id, Topic = "other-topic", Label = "Other", ProcessMissed = true };
            Config.Topics.AddRange([Clipboard, Other]);
            Store.SaveConfig(Config);
        }

        public Guid ServerId => Config.Servers[0].Id;

        public bool Start()
        {
            Engine = new Engine(Store, Log, Server.Handler, clock: () => Now, missedDebounce: TimeSpan.FromMilliseconds(50));
            Engine.MessageReceived += Got.Enqueue;
            Engine.MissedSummaryReady += Summaries.Enqueue;
            return Engine.Start();
        }

        public void Dispose()
        {
            Engine?.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task ClassifiesDecidesSummarisesAndPersists()
    {
        using var rig = new Rig();
        var body = rig.Server.NextStream();
        Assert.True(rig.Start());
        body.Send(Msg.Open(T));
        body.Send(Msg.Line("old1", T - 3600));                                  // stale catch-up
        body.Send(Msg.Line("old2", T - 600));                                   // stale catch-up
        body.Send(Msg.Line("fresh", T - 60));                                   // catch-up inside 120 s
        body.Send(Msg.Line("old3", T - 3600, topic: "other-topic"));            // processMissed topic
        body.Send(Msg.Line("mine", T + 1, extra: ""","tags":["ntfybar-win"]"""));
        body.Send(Msg.Line("pic", T + 2, message: null,
            extra: $$""","attachment":{"name":"a.jpg","type":"image/jpeg","size":5,"expires":{{T + 3600}},"url":"https://ntfy.example/file/a.jpg"}"""));
        Assert.True(await Wait.Until(() => rig.Got.Count == 6));

        Assert.Equal(
            [("old1", Handling.Missed), ("old2", Handling.Missed), ("fresh", Handling.Live), ("old3", Handling.Live), ("mine", Handling.OwnEcho), ("pic", Handling.Live)],
            rig.Got.Select(m => (m.Message.Id, m.Handling)));
        var pic = rig.Got.Last();
        Assert.Equal((ClipKind.Image, "Clipboard", false, rig.ServerId), (pic.Kind, pic.Topic.Label, pic.IsCatchUp, pic.ServerId));
        Assert.True(rig.Got.First().IsCatchUp);

        Assert.True(await Wait.Until(() => rig.Summaries.Count == 1));
        var summary = Assert.Single(rig.Summaries);
        Assert.Equal(("Clipboard", 2), (summary.Topic.Label, summary.Count));

        var request = Assert.Single(rig.Server.StreamRequests);
        Assert.Equal("https://ntfy.example/example-topic,other-topic/json", request.Uri.AbsoluteUri);
        Assert.Equal(ServerAuth.WithBasic("someone", "secret-pass").HeaderValue, request.Header("Authorization"));
        Assert.Equal(StreamState.Connected, rig.Engine.OverallStatus!.State);
        Assert.Equal(T + 2, rig.Store.LoadState()[rig.ServerId].Time);
    }

    [Fact]
    public async Task PublishUsesDeviceTagAndTitleAndItsEchoIsRecognised()
    {
        using var rig = new Rig();
        var body = rig.Server.NextStream();
        rig.Start();
        var r = await rig.Engine.PublishTextAsync(rig.Clipboard.Id, "copied text");
        Assert.Equal("published-1", r.Id);
        var post = rig.Server.Handler.Requests.Single(q => q.Method == HttpMethod.Post);
        var json = JsonDocument.Parse(post.Body).RootElement;
        Assert.Equal(("example-topic", "Test PC", "ntfybar-win"),
            (json.GetProperty("topic").GetString(), json.GetProperty("title").GetString(), json.GetProperty("tags")[0].GetString()));

        // The echo comes back untagged (e.g. the device tag changed meanwhile): the id still matches.
        body.Send(Msg.Open(T));
        body.Send(Msg.Line("published-1", T + 1, message: "copied text"));
        Assert.True(await Wait.Until(() => rig.Got.Count == 1));
        Assert.Equal(Handling.OwnEcho, rig.Got.Single().Handling);

        Assert.Equal(PublishStatus.InvalidTarget, (await rig.Engine.PublishTextAsync(Guid.NewGuid(), "x")).Status);
        Assert.Equal("clip.png", (await rig.Engine.PublishImageAsync(rig.Clipboard.Id, [1])).Ok
            ? rig.Server.Handler.Requests.Last().Query["filename"] : null);
    }

    [Fact]
    public async Task UpdateConfigRestartsOnlyChangedStreams()
    {
        using var rig = new Rig();
        rig.Start();
        Assert.True(await Wait.Until(() => rig.Server.StreamRequests.Count == 1));

        rig.Engine.UpdateConfig(rig.Engine.Config.Clone());  // same content
        await Task.Delay(50);
        Assert.Single(rig.Server.StreamRequests);

        var changed = rig.Engine.Config.Clone();
        changed.Servers[0].Auth = ServerAuth.WithToken("tk_new");
        rig.Engine.UpdateConfig(changed);
        Assert.True(await Wait.Until(() => rig.Server.StreamRequests.Count == 2));
        Assert.Equal("Bearer tk_new", rig.Server.StreamRequests[1].Header("Authorization"));
        Assert.DoesNotContain("tk_new", File.ReadAllText(rig.Store.ConfigPath));

        var none = rig.Engine.Config.Clone();
        none.Topics.ForEach(t => t.Enabled = false);
        rig.Engine.UpdateConfig(none);
        Assert.Null(rig.Engine.OverallStatus);
        await Task.Delay(50);
        Assert.Equal(2, rig.Server.StreamRequests.Count);
    }

    [Fact]
    public async Task SendGoesTextThenPictureThenFilesAndRemembersTheTopic()
    {
        using var rig = new Rig();
        rig.Start();
        var file = rig.Dir.File("notes.pdf");
        File.WriteAllText(file, "pdf");

        var r = await rig.Engine.SendAsync(rig.Other.Id, "hi", [1, 2], [file], title: "", tags: ["work"], priority: 4);

        Assert.Equal(new SendOutcome(true, "Sent to “Other” · 2 files"), r);
        var sends = rig.Server.Handler.Requests.Where(q => q.Method != HttpMethod.Get).ToList();
        Assert.Equal(["/", "/other-topic", "/other-topic"], sends.Select(q => q.Uri.AbsolutePath));
        Assert.Equal(["clip.png", "notes.pdf"], sends.Skip(1).Select(q => q.Query["filename"]));
        Assert.Equal("ntfybar-win,work", sends[1].Query["tags"]);
        Assert.False(sends[1].Query.ContainsKey("title"));
        Assert.Equal(rig.Other.Id, rig.Engine.Config.Publish.LastTopicId);
        Assert.Equal(rig.Other.Id, rig.Store.LoadConfig()!.Publish.LastTopicId);

        Assert.Equal(new SendOutcome(true, "Sent to “Other”"), await rig.Engine.SendAsync(rig.Other.Id, "text only", null, []));
        Assert.Equal(new SendOutcome(false, "Couldn't send: nothing to send"), await rig.Engine.SendAsync(rig.Other.Id, " \n", null, []));
    }

    [Fact]
    public async Task FailedSendSaysWhyAndHowMuchWentOutAndKeepsTheLastTopic()
    {
        using var rig = new Rig();
        rig.Start();
        var file = rig.Dir.File("a.txt");
        File.WriteAllText(file, "a");

        var missing = await rig.Engine.SendAsync(rig.Clipboard.Id, "hi", null, [file, rig.Dir.File("gone.txt")]);
        Assert.Equal(new SendOutcome(false, "Couldn't send: a file could not be read"), missing);  // refused before any upload
        var invalid = await rig.Engine.SendAsync(Guid.NewGuid(), "hi", null, []);
        Assert.Equal(new SendOutcome(false, "Couldn't send: invalid server or topic"), invalid);
        Assert.Null(rig.Engine.Config.Publish.LastTopicId);

        var denied = new Rig();
        try
        {
            var engine = new Engine(denied.Store, denied.Log, new FakeHandler(q => q.Method == HttpMethod.Post
                ? FakeHandler.Json("""{"id":"x"}""")
                : FakeHandler.Status(q.Method == HttpMethod.Put ? 403 : 200)));
            engine.Start();
            var partial = await engine.SendAsync(denied.Clipboard.Id, "hi", [1], []);
            Assert.Equal(new SendOutcome(false, "Couldn't send: not allowed on this topic (1 of 2 went out)"), partial);
            engine.Dispose();
        }
        finally
        {
            denied.Dispose();
        }
    }

    [Fact]
    public async Task ConnectionTestPollsTheTopicsWithTheGivenSignIn()
    {
        using var rig = new Rig();
        rig.Start();
        var server = new ServerConfig { Url = "https://ntfy.example/", Auth = ServerAuth.WithToken("tk_test") };

        Assert.Equal(ConnectionCheck.Ok, (await rig.Engine.TestConnectionAsync(server, ["example-topic", "bad/name", "example-topic"])).Check);
        var poll = rig.Server.Handler.Requests.Last();
        Assert.Equal($"https://ntfy.example/example-topic/json?poll=1&since={T}", poll.Uri.AbsoluteUri);
        Assert.Equal("Bearer tk_test", poll.Header("Authorization"));

        Assert.Equal(ConnectionCheck.Ok, (await rig.Engine.TestConnectionAsync(server, [])).Check);
        Assert.Equal("https://ntfy.example/v1/account", rig.Server.Handler.Requests.Last().Uri.AbsoluteUri);

        rig.Server.NextStatus(401);
        var rejected = await rig.Engine.TestConnectionAsync(server, ["example-topic"]);
        Assert.Equal((ConnectionCheck.NotAllowed, 401), (rejected.Check, rejected.HttpStatus));
        Assert.Contains("sign-in rejected", rejected.Describe());
        rig.Server.NextStatus(301);
        Assert.Equal(ConnectionCheck.Redirected, (await rig.Engine.TestConnectionAsync(server, ["example-topic"])).Check);
        Assert.Equal(ConnectionCheck.InvalidUrl, (await rig.Engine.TestConnectionAsync(new ServerConfig { Url = "ftp://x" }, [])).Check);
    }

    [Fact]
    public async Task ConnectionTestReportsUnreachable()
    {
        using var rig = new Rig();
        var engine = new Engine(rig.Store, rig.Log, new FakeHandler(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError)));
        var r = await engine.TestConnectionAsync(rig.Config.Servers[0], []);
        Assert.Equal("Unreachable: no answer from the server", r.Describe());
        engine.Dispose();
    }

    [Fact]
    public async Task SavingTheSameConfigRetriesAStreamStoppedByAuth()
    {
        using var rig = new Rig();
        rig.Server.NextStatus(401);
        rig.Start();
        Assert.True(await Wait.Until(() => rig.Engine.OverallStatus?.State == StreamState.AuthFailed));

        rig.Engine.UpdateConfig(rig.Engine.Config.Clone());  // e.g. the server ACL was fixed, Settings → Save
        Assert.True(await Wait.Until(() => rig.Server.StreamRequests.Count == 2));
        Assert.True(await Wait.Until(() => rig.Engine.OverallStatus?.State == StreamState.Connecting));
    }

    [Fact]
    public void InvalidConfigKeepsDefaultsAndTheFile()
    {
        using var rig = new Rig();
        File.WriteAllText(rig.Store.ConfigPath, "{ broken");
        Assert.False(rig.Start());
        Assert.Empty(rig.Engine.Config.Topics);
        Assert.Null(rig.Engine.OverallStatus);
        Assert.Equal("{ broken", File.ReadAllText(rig.Store.ConfigPath));
        Assert.False(rig.Engine.Reload());
    }

    [Fact]
    public void FirstSaveAfterInvalidConfigKeepsTheBadFileAside()
    {
        using var rig = new Rig();
        File.WriteAllText(rig.Store.ConfigPath, "{ broken");
        Assert.False(rig.Start());
        var c = rig.Engine.Config.Clone();
        c.LaunchAtLogin = true;
        rig.Engine.UpdateConfig(c);
        Assert.Equal("{ broken", File.ReadAllText(rig.Store.ConfigPath + ".invalid"));
        Assert.True(rig.Store.LoadConfig()!.LaunchAtLogin);
        rig.Engine.UpdateConfig(c);  // only the first save moves anything
        Assert.Equal("{ broken", File.ReadAllText(rig.Store.ConfigPath + ".invalid"));
    }

    [Fact]
    public async Task TextIsSentWithLfLineEndings()
    {
        using var rig = new Rig();
        rig.Start();
        await rig.Engine.SendAsync(rig.Clipboard.Id, "a\r\nb\rc", null, []);
        var body = rig.Server.Handler.Requests.Last(r => r.Method == HttpMethod.Post).Body;
        Assert.Equal("a\nb\nc", JsonDocument.Parse(body!).RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ExpiredAttachmentIsNotRequested()
    {
        using var rig = new Rig();
        rig.Start();
        var m = new IncomingMessage(rig.ServerId, rig.Clipboard,
            Msg.Make(attachment: new NtfyAttachment("a.jpg", "https://ntfy.example/file/a.jpg", "image/jpeg", 5, T - 1)),
            ClipKind.ExpiredAttachment, Handling.Live, false, Now);
        Assert.Equal(DownloadStatus.Expired, (await rig.Engine.DownloadAsync(m)).Status);
        Assert.Equal(DownloadStatus.Expired, (await rig.Engine.DownloadTextAsync(m)).Status);
        Assert.DoesNotContain(rig.Server.Handler.Requests, r => r.Uri.AbsolutePath.StartsWith("/file/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LogsNeverContainBodiesSecretsTopicsOrHosts()
    {
        using var rig = new Rig();
        var body = rig.Server.NextStream();
        rig.Start();
        body.Send(Msg.Open(T));
        body.Send("{ not json but has secret-body");
        body.Send(Msg.Line("m1", T + 1, message: "secret-body", extra: ""","title":"Test PC" """));
        Assert.True(await Wait.Until(() => rig.Got.Count == 1));
        await rig.Engine.PublishTextAsync(rig.Clipboard.Id, "secret-body " + new string('x', 5000));
        body.End();
        Assert.True(await Wait.Until(() => rig.Server.StreamRequests.Count == 2));
        rig.Engine.Stop();

        Assert.NotEmpty(rig.Log.Lines);
        foreach (var secret in new[] { "secret-body", "secret-pass", "someone", "example-topic", "other-topic", "ntfy.example", "Test PC" })
            Assert.DoesNotContain(rig.Log.Lines, l => l.Contains(secret, StringComparison.Ordinal));
    }
}
