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
