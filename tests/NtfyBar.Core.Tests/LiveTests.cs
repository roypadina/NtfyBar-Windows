using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using NtfyBar.Core;

namespace NtfyBar.Core.Tests;

/// <summary>Opt-in only: <c>NTFYBAR_LIVE_TEST=1 dotnet test --filter Live</c>. Never in CI.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("NTFYBAR_LIVE_TEST") != "1")
            Skip = "set NTFYBAR_LIVE_TEST=1 to run against ntfy.sh";
    }
}

/// <summary>Against public ntfy.sh with a freshly generated throwaway topic and no credentials.</summary>
public class LiveTests
{
    private static readonly ServerConfig NtfySh = new() { Url = "https://ntfy.sh" };

    private static string RandomTopic() => "ntfybar-win-test-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    [LiveFact]
    public async Task PublishVariantsArriveAsTheProtocolSays()
    {
        var topic = RandomTopic();
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var pub = new Publisher(http, new EchoGuard());
        var o = new PublishOptions("ntfybar-win", "Live test 😀 & co", ["x"]);
        using var dir = new TempDir();
        var file = dir.File("file 1.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        var longText = "  " + string.Concat(Enumerable.Repeat("שלום world ", 500)) + "  ";

        var text = await pub.PublishTextAsync(NtfySh, topic, "short שלום", o);
        var upload = Assert.Single(await pub.PublishFilesAsync(NtfySh, topic, [file], o));
        var big = await pub.PublishTextAsync(NtfySh, topic, longText, o);
        Assert.True(text.Ok && upload.Ok && big.Ok, $"{text.Status} {upload.Status} {big.Status}");

        // ntfy.sh writes its cache in batches: poll until all three are there.
        var polled = new Dictionary<string, NtfyMessage>();
        for (var i = 0; i < 20 && polled.Count < 3; i++, await Task.Delay(500))
            polled = (await http.GetStringAsync($"https://ntfy.sh/{topic}/json?poll=1"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(NtfyMessage.Parse).OfType<NtfyMessage>().ToDictionary(m => m.Id);
        var now = DateTimeOffset.UtcNow;

        var m1 = polled[text.Id!];
        Assert.Equal(("short שלום", "Live test 😀 & co"), (m1.Message, m1.Title));
        Assert.Equal(["ntfybar-win", "x"], m1.Tags);
        Assert.Equal(new NtfyAction("copy", "Copy", null, "short שלום"), Assert.Single(m1.Actions));
        Assert.Equal(ClipKind.Text, Clip.Classify(m1, now));

        var m2 = polled[upload.Id!];
        Assert.Equal(("file 1.bin", "file 1.bin", 3L), (m2.Message, m2.Attachment!.Name, m2.Attachment.Size));
        Assert.Equal(ClipKind.File, Clip.Classify(m2, now));

        var m3 = polled[big.Id!];
        Assert.Equal(Publisher.Preview(longText).Trim(), m3.Message);  // the server trims the message text
        Assert.Equal("clip.txt", m3.Attachment!.Name);
        Assert.Equal(ClipKind.TextAttachment, Clip.Classify(m3, now));
        var downloaded = await new AttachmentDownloader(http).DownloadTextAsync(m3.Attachment, NtfySh);
        Assert.Equal(longText, downloaded.Text);  // byte-exact, whitespace kept
    }

    [LiveFact]
    public async Task EngineStreamsLiveOwnEchoAndFreshCatchUpWithoutDuplicates()
    {
        var topic = RandomTopic();
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        var config = new AppConfig();
        config.Servers[0].Url = NtfySh.Url;
        var t = new TopicConfig { ServerId = config.Servers[0].Id, Topic = topic, Label = "Live" };
        config.Topics.Add(t);
        store.SaveConfig(config);
        using var foreign = new HttpClient();
        Task<HttpResponseMessage> Foreign(string text) => foreign.PostAsJsonAsync("https://ntfy.sh/", new { topic, message = text });

        var got = new ConcurrentQueue<IncomingMessage>();
        var engine = new Engine(store);
        engine.MessageReceived += got.Enqueue;
        engine.Start();
        Assert.True(await Wait.Until(() => engine.OverallStatus?.State == StreamState.Connected, 10_000));

        var sent = DateTime.UtcNow;
        Assert.True((await engine.PublishTextAsync(t.Id, "from me")).Ok);
        (await Foreign("from phone")).EnsureSuccessStatusCode();
        Assert.True(await Wait.Until(() => got.Count == 2, 10_000));
        Assert.True(DateTime.UtcNow - sent < TimeSpan.FromSeconds(5), "stream must not be buffered");
        Assert.Equal([("from me", Handling.OwnEcho), ("from phone", Handling.Live)], got.Select(m => (m.Message.Message, m.Handling)));

        engine.Dispose();
        await Task.Delay(1100);  // ntfy times are whole seconds
        (await Foreign("while away")).EnsureSuccessStatusCode();
        await Task.Delay(1100);

        got.Clear();
        using var again = new Engine(store);
        again.MessageReceived += got.Enqueue;
        again.Start();
        Assert.True(await Wait.Until(() => got.Count >= 1, 10_000));
        await Task.Delay(1000);
        var replay = Assert.Single(got);  // the two earlier ones were not replayed
        Assert.Equal(("while away", true, Handling.Live), (replay.Message.Message, replay.IsCatchUp, replay.Handling));
    }
}
