using System.Text;
using NtfyBar.Core;

namespace NtfyBar.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void MissingFileGivesDefaults()
    {
        using var dir = new TempDir();
        var c = new ConfigStore(dir.Path).LoadConfig()!;
        Assert.Equal("https://ntfy.sh", Assert.Single(c.Servers).Url);
        Assert.Empty(c.Topics);
        Assert.Equal("ntfybar-win", c.Publish.DeviceTag);
        Assert.Equal(Environment.MachineName, c.Publish.Title);
        Assert.Equal("Ctrl+Alt+Shift+C", c.Publish.Hotkey);
        Assert.Null(c.Publish.LastTopicId);
        Assert.Equal(AppConfig.DefaultDownloadDir, c.DownloadDir);
        Assert.False(c.LaunchAtLogin);
    }

    [Fact]
    public void TolerantDecodeIgnoresUnknownAndFallsBackPerField()
    {
        using var dir = new TempDir();
        var sid = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir.Path, "config.json"), $$"""
            {
              "servers": [ {"id":"{{sid}}","url":"https://ntfy.example","auth":{"type":"weird"},"future":1}, 42 ],
              "topics": [ {"serverId":"{{sid}}","topic":"example-topic","label":"Clipboard","notify":"yes","copyToClipboard":true,"popup":{"enabled":true} } ],
              "publish": {"deviceTag":7,"title":"Desk","hotkey":"Win+V"},
              "downloadDir": "",
              "launchAtLogin": "maybe",
              "quietHours": {"enabled":true},
              "plugins": {}
            }
            """);
        var c = new ConfigStore(dir.Path).LoadConfig()!;

        var s = Assert.Single(c.Servers);
        Assert.Equal(sid, s.Id);
        Assert.Equal("https://ntfy.example", s.Url);
        Assert.Equal(AuthTypes.None, s.Auth.Type);
        var t = Assert.Single(c.Topics);
        Assert.Equal("Clipboard", t.Label);
        Assert.True(t.Notify, "wrong type falls back to the default");
        Assert.True(t.CopyToClipboard, "the field next to a broken one survives");
        Assert.Equal("ntfybar-win", c.Publish.DeviceTag);
        Assert.Equal("Desk", c.Publish.Title);
        Assert.Equal("Win+V", c.Publish.Hotkey);
        Assert.Equal(AppConfig.DefaultDownloadDir, c.DownloadDir);
        Assert.False(c.LaunchAtLogin);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public void InvalidJsonReturnsNullAndLeavesTheFileAlone(string content)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(path, content);
        var log = new CapturingLog();
        Assert.Null(new ConfigStore(dir.Path, log: log).LoadConfig());
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Contains(log.Lines, l => l.Contains("keeping the last good config"));
    }

    [Fact]
    public void RoundTripKeepsEveryField()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path, new XorProtector());
        var c = new AppConfig { DownloadDir = dir.File("dl"), LaunchAtLogin = true };
        c.Servers[0].Url = "https://ntfy.example";
        c.Servers[0].Auth = ServerAuth.WithBasic("someone", "pässword");
        var topic = new TopicConfig
        {
            ServerId = c.Servers[0].Id, Topic = "example-topic", Label = "Clipboard", Notify = false,
            CopyToClipboard = true, DownloadAttachments = true, CopyAttachment = true, ProcessMissed = true,
        };
        c.Topics.Add(topic);
        c.Publish = new PublishSettings { DeviceTag = "ntfybar-test", Title = "Test PC", LastTopicId = topic.Id, Hotkey = "Ctrl+Alt+V" };
        store.SaveConfig(c);

        var back = store.LoadConfig()!;
        Assert.Equal(c.Servers[0].Id, back.Servers[0].Id);
        Assert.Equal("someone", back.Servers[0].Auth.User);
        Assert.Equal("pässword", back.Servers[0].Auth.Pass);
        var t = Assert.Single(back.Topics);
        Assert.Equal((topic.Id, topic.ServerId, "example-topic", "Clipboard", false, true, true, true, true, true),
            (t.Id, t.ServerId, t.Topic, t.Label, t.Notify, t.Enabled, t.CopyToClipboard, t.DownloadAttachments, t.CopyAttachment, t.ProcessMissed));
        Assert.Equal(("ntfybar-test", "Test PC", topic.Id, "Ctrl+Alt+V"),
            (back.Publish.DeviceTag, back.Publish.Title, back.Publish.LastTopicId, back.Publish.Hotkey));
        Assert.Equal(dir.File("dl"), back.DownloadDir);
        Assert.True(back.LaunchAtLogin);
    }

    [Fact]
    public void PasswordAndTokenAreProtectedOnDiskOnly()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path, new XorProtector());
        var c = new AppConfig();
        c.Servers[0].Auth = ServerAuth.WithBasic("someone", "secret-pass");
        c.Servers.Add(new ServerConfig { Url = "https://other.example", Auth = ServerAuth.WithToken("tk_secret") });
        store.SaveConfig(c);

        var text = File.ReadAllText(store.ConfigPath);
        Assert.DoesNotContain("secret-pass", text);
        Assert.DoesNotContain("tk_secret", text);
        Assert.Equal(2, text.Split("\"dpapi:").Length - 1);
        Assert.Equal("secret-pass", c.Servers[0].Auth.Pass);  // in-memory config untouched by saving

        var back = store.LoadConfig()!;
        Assert.Equal("secret-pass", back.Servers[0].Auth.Pass);
        Assert.Equal("tk_secret", back.Servers[1].Auth.Token);
    }

    [Fact]
    public void PlainSecretOnDiskIsAcceptedAndProtectedOnNextSave()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path, new XorProtector());
        File.WriteAllText(store.ConfigPath, """{"servers":[{"url":"https://ntfy.example","auth":{"type":"token","token":"tk_plain"}}]}""");
        var c = store.LoadConfig()!;
        Assert.Equal("Bearer tk_plain", c.Servers[0].Auth.HeaderValue);
        store.SaveConfig(c);
        Assert.DoesNotContain("tk_plain", File.ReadAllText(store.ConfigPath));
    }

    [Fact]
    public void UndecryptableSecretBecomesEmptyAndIsLogged()
    {
        using var dir = new TempDir();
        var log = new CapturingLog();
        var store = new ConfigStore(dir.Path, new XorProtector(), log);
        File.WriteAllText(store.ConfigPath, """{"servers":[{"url":"https://ntfy.example","auth":{"type":"basic","user":"u","pass":"dpapi:%%%"}}]}""");
        Assert.Equal("", store.LoadConfig()!.Servers[0].Auth.Pass);
        Assert.Contains(log.Lines, l => l.Contains("could not be decrypted"));
    }

    [Fact]
    public void AuthHeaderValues()
    {
        Assert.Null(new ServerAuth().HeaderValue);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("רועי:pä:ss")), ServerAuth.WithBasic("רועי", "pä:ss").HeaderValue);
        Assert.Equal("Bearer tk_abc", ServerAuth.WithToken("tk_abc").HeaderValue);
        Assert.Null(ServerAuth.WithToken("").HeaderValue);
    }

    [Theory]
    [InlineData("https://ntfy.example/", "https://ntfy.example/")]
    [InlineData(" http://ntfy.example:8080/sub// ", "http://ntfy.example:8080/sub")]
    [InlineData("ftp://ntfy.example", null)]
    [InlineData("https://user:pw@ntfy.example", null)]
    [InlineData("https://ntfy.example/?x=1", null)]
    [InlineData("https://ntfy.example/#frag", null)]
    [InlineData("ntfy.example", null)]
    [InlineData("", null)]
    public void ServerUrlValidation(string input, string? expected) =>
        Assert.Equal(expected, ServerConfig.ValidatedUri(input)?.AbsoluteUri);

    [Theory]
    [InlineData("example-topic", true)]
    [InlineData("A_b-9", true)]
    [InlineData("", false)]
    [InlineData("a,b", false)]
    [InlineData("a/b", false)]
    [InlineData("a?b", false)]
    [InlineData("ä", false)]
    public void TopicNameValidation(string name, bool valid) => Assert.Equal(valid, TopicConfig.IsValidTopicName(name));

    [Fact]
    public void TopicNameLongerThan64IsInvalid() => Assert.False(TopicConfig.IsValidTopicName(new string('a', 65)));

    [Fact]
    public void StateRoundTripAndBrokenStateIsEmpty()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        var id = Guid.NewGuid();
        store.SaveState(new Dictionary<Guid, StreamCursor> { [id] = new() { Time = 1_700_000_123, Ids = ["a", "b"] } });
        var back = store.LoadState()[id];
        Assert.Equal(1_700_000_123, back.Time);
        Assert.Equal(["a", "b"], back.Ids);

        File.WriteAllText(store.StatePath, "garbage");
        Assert.Empty(store.LoadState());
    }

    [Fact]
    public void CloneIsDeep()
    {
        var c = new AppConfig();
        c.Topics.Add(new TopicConfig { Topic = "example-topic", Label = "A" });
        var copy = c.Clone();
        copy.Topics[0].Label = "B";
        copy.Servers[0].Url = "https://ntfy.example";
        Assert.Equal("A", c.Topics[0].Label);
        Assert.Equal("https://ntfy.sh", c.Servers[0].Url);
        Assert.Equal(c.Topics[0].Id, copy.Topics[0].Id);
    }
}

public class FileLogTests
{
    [Fact]
    public void RotatesAndKeepsAFixedNumberOfFiles()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.Path, maxBytes: 100, maxFiles: 3);
        for (var i = 0; i < 20; i++) log.Info($"line {i:00} " + new string('x', 30));
        var files = Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["ntfybar.1.log", "ntfybar.2.log", "ntfybar.log"], files);
        Assert.Contains("line 19", File.ReadAllText(log.CurrentPath));
        Assert.DoesNotContain(files, f => File.ReadAllText(Path.Combine(dir.Path, f!)).Contains("line 00"));
    }
}
