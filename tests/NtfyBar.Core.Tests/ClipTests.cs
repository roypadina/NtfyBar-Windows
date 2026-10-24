using NtfyBar.Core;

namespace NtfyBar.Core.Tests;

public class ClipTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static NtfyAttachment Att(string? name, string? type, long? size, long? expires = null) =>
        new(name, "https://ntfy.example/file/x", type, size, expires ?? Now.ToUnixTimeSeconds() + 3600);

    [Fact]
    public void PlainMessageIsText() => Assert.Equal(ClipKind.Text, Clip.Classify(Msg.Make(), Now));

    [Theory]
    [InlineData("clip.txt", "text/plain; charset=utf-8", 10)]
    [InlineData("attachment.txt", "text/plain", 1024 * 1024)]
    [InlineData("clip.txt", "TEXT/PLAIN", 0)]
    public void TreatAsTextRuleMatches(string name, string type, long size) =>
        Assert.Equal(ClipKind.TextAttachment, Clip.Classify(Msg.Make(attachment: Att(name, type, size)), Now));

    [Theory]
    [InlineData("notes.txt", "text/plain", 10L)]               // any other .txt stays a file
    [InlineData("CLIP.TXT", "text/plain", 10L)]                // exact name only
    [InlineData("clip.txt", "text/plain", 1024L * 1024 + 1)]   // over 1 MB
    [InlineData("clip.txt", "application/octet-stream", 10L)]  // wrong type
    [InlineData("clip.txt", "text/html", 10L)]
    [InlineData("clip.txt", null, 10L)]
    [InlineData("clip.txt", "text/plain", null)]              // unknown size (external URL)
    [InlineData("report.pdf", "application/pdf", 10L)]
    public void EverythingElseIsAFile(string name, string? type, long? size) =>
        Assert.Equal(ClipKind.File, Clip.Classify(Msg.Make(attachment: Att(name, type, size)), Now));

    [Fact]
    public void ImageAttachment() =>
        Assert.Equal(ClipKind.Image, Clip.Classify(Msg.Make(attachment: Att("clip.png", "image/png", 2000)), Now));

    [Fact]
    public void ExpiredAttachmentWinsOverKind()
    {
        Assert.Equal(ClipKind.ExpiredAttachment, Clip.Classify(Msg.Make(attachment: Att("clip.txt", "text/plain", 5, Now.ToUnixTimeSeconds() - 1)), Now));
        Assert.Equal(ClipKind.File, Clip.Classify(Msg.Make(attachment: Att("a.bin", null, 5, Now.ToUnixTimeSeconds())), Now));
    }

    [Theory]
    [InlineData(false, 99999, false, false, Handling.Live)]    // live, any age
    [InlineData(true, 120, false, false, Handling.Live)]       // catch-up inside the freshness window
    [InlineData(true, 121, false, false, Handling.Missed)]     // too old
    [InlineData(true, 3600, false, true, Handling.Live)]       // processMissed
    [InlineData(false, 0, true, false, Handling.OwnEcho)]      // own echo wins
    [InlineData(true, 3600, true, true, Handling.OwnEcho)]
    public void DecideHandling(bool catchUp, int ageSec, bool ownEcho, bool processMissed, Handling expected) =>
        Assert.Equal(expected, Clip.Decide(Msg.Make(time: Now.ToUnixTimeSeconds() - ageSec), catchUp, ownEcho, processMissed, Now));

    [Fact]
    public void EchoGuardMatchesRegisteredIdOnceAndExpiresAfterTwoMinutes()
    {
        var now = Now;
        var guard = new EchoGuard(() => now);
        guard.Register("id1");
        guard.Register("id2");
        Assert.True(guard.IsOwnEcho(Msg.Make(id: "id1"), "ntfybar-win"));
        Assert.False(guard.IsOwnEcho(Msg.Make(id: "id1"), "ntfybar-win"));  // consumed
        Assert.False(guard.IsOwnEcho(Msg.Make(id: "other"), "ntfybar-win"));
        now += TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1);
        Assert.False(guard.IsOwnEcho(Msg.Make(id: "id2"), "ntfybar-win"));
    }

    [Fact]
    public void EchoGuardMatchesFirstTagOnly()
    {
        var guard = new EchoGuard();
        Assert.True(guard.IsOwnEcho(Msg.Make(tags: ["ntfybar-win", "x"]), "ntfybar-win"));
        Assert.False(guard.IsOwnEcho(Msg.Make(tags: ["ntfybar-mac", "ntfybar-win"]), "ntfybar-win"));
        Assert.False(guard.IsOwnEcho(Msg.Make(), "ntfybar-win"));
        Assert.False(guard.IsOwnEcho(Msg.Make(tags: [""]), ""));
    }
}
