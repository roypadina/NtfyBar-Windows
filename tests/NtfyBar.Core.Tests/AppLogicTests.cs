using NtfyBar.Core;

namespace NtfyBar.Core.Tests;

/// <summary>The non-UI decisions the tray app makes: hotkey, Send To args, clipboard choice, file bursts, Recent.</summary>
public class AppLogicTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Shift+C", HotkeySpec.Control | HotkeySpec.Alt | HotkeySpec.Shift, 0x43u)]
    [InlineData(" control + shift + v ", HotkeySpec.Control | HotkeySpec.Shift, 0x56u)]
    [InlineData("Win+F5", HotkeySpec.Win, 0x74u)]
    [InlineData("Alt+F24", HotkeySpec.Alt, 0x87u)]
    [InlineData("Ctrl+Alt+1", HotkeySpec.Control | HotkeySpec.Alt, 0x31u)]
    [InlineData("Ctrl+NumPad3", HotkeySpec.Control, 0x63u)]
    [InlineData("Ctrl+Shift+Space", HotkeySpec.Control | HotkeySpec.Shift, 0x20u)]
    [InlineData("Ctrl+pgdn", HotkeySpec.Control, 0x22u)]
    public void HotkeyParses(string spec, uint modifiers, uint key)
    {
        Assert.True(HotkeySpec.TryParse(spec, out var m, out var k));
        Assert.Equal((modifiers, key), (m, k));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("C")]                 // no modifier
    [InlineData("Ctrl+Alt")]          // no key
    [InlineData("Ctrl+C+V")]          // two keys
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F01")]
    [InlineData("Ctrl+LButton")]      // not a keyboard key
    [InlineData("Ctrl+12")]
    [InlineData("Hyper+C")]
    public void HotkeyRejects(string? spec) => Assert.False(HotkeySpec.TryParse(spec, out _, out _));

    [Fact]
    public void SendToFilesAreFullPathsAfterTheSwitch()
    {
        var a = Path.GetFullPath("a.txt");
        var b = Path.Combine(Path.GetTempPath(), "b c.pdf");
        Assert.Null(CommandLine.SendFiles([]));
        Assert.Null(CommandLine.SendFiles(["-ToastActivated"]));
        Assert.Empty(CommandLine.SendFiles(["--send"])!);
        Assert.Equal([a, b], CommandLine.SendFiles(["-ToastActivated", "--send", "a.txt", b, "", b.ToUpperInvariant(), "-x"])!);
    }

    [Theory]
    [InlineData(2, true, "text", ClipboardPayload.Files)]
    [InlineData(0, true, "text", ClipboardPayload.Image)]
    [InlineData(0, false, "text", ClipboardPayload.Text)]
    [InlineData(0, false, " \r\n\t", ClipboardPayload.Empty)]
    [InlineData(0, false, null, ClipboardPayload.Empty)]
    public void ClipboardChoiceIsFilesThenImageThenText(int files, bool image, string? text, ClipboardPayload expected) =>
        Assert.Equal(expected, Outgoing.Choose(files, image, text));

    [Fact]
    public void FileBurstCountsFilesWithinFiveSecondsOfThePreviousOne()
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var burst = new FileBurst(FileBurst.DefaultWindow);
        Assert.Equal(1, burst.Add(t));
        Assert.Equal(2, burst.Add(t.AddSeconds(4)));
        Assert.Equal(3, burst.Add(t.AddSeconds(9)));    // sliding: 5 s after the 2nd
        Assert.Equal(1, burst.Add(t.AddSeconds(14.5))); // gap > 5 s: new burst
        Assert.Equal(2, burst.Add(t.AddSeconds(14.5)));
    }

    private static IncomingMessage Incoming(string id, ClipKind kind, Handling handling = Handling.Live, string? message = "hello", NtfyAttachment? a = null) =>
        new(Guid.Empty, new TopicConfig { Topic = "example-topic", Label = "Clipboard" }, Msg.Make(id, message: message, attachment: a), kind, handling, false, DateTimeOffset.UnixEpoch);

    [Fact]
    public void RecentKeepsTheNewest20()
    {
        var recent = new RecentList();
        for (var i = 0; i < 25; i++) recent.Add(Incoming($"m{i}", ClipKind.Text));
        Assert.Equal(20, recent.Items.Count);
        Assert.Equal(("m24", "m5"), (recent.Items[0].Id, recent.Items[^1].Id));
        Assert.Same(recent.Items[3], recent.Find("m21"));
        Assert.Null(recent.Find("m4"));
        Assert.Null(recent.Find(null));
    }

    [Fact]
    public void RecentLabelsAreOneLineAndNeverTheTopicName()
    {
        var file = new NtfyAttachment("photo.jpg", "https://ntfy.example/file/x.jpg", "image/jpeg", 5, null);
        Assert.Equal("Clipboard · line one line two", new RecentItem(Incoming("a", ClipKind.Text, message: "line one\r\nline two")).Label);
        Assert.Equal("Sent · Clipboard · photo.jpg", new RecentItem(Incoming("b", ClipKind.Image, Handling.OwnEcho, null, file)).Label);
        Assert.Equal("Clipboard · photo.jpg (expired)", new RecentItem(Incoming("c", ClipKind.ExpiredAttachment, a: file)).Label);
        var longText = new RecentItem(Incoming("d", ClipKind.Text, message: new string('x', 200)));
        Assert.Equal(RecentItem.LabelChars + 1, longText.Label.Length);
        Assert.EndsWith("…", longText.Label);
        Assert.DoesNotContain("example-topic", longText.Label);

        var clip = new RecentItem(Incoming("e", ClipKind.TextAttachment, message: "preview…", a: new NtfyAttachment("clip.txt", "u", "text/plain", 9, null)));
        Assert.Null(clip.Text);
        Assert.Equal("Clipboard · preview…", clip.Label);
        clip.Text = "the full text";
        Assert.Equal("Clipboard · the full text", clip.Label);
        Assert.Equal("hello", new RecentItem(Incoming("f", ClipKind.Text)).Text);
    }

    [Fact]
    public void DownloadProblemsAreWorded()
    {
        Assert.Equal("attachment expired on the server", new DownloadResult(DownloadStatus.Expired).Describe());
        Assert.Equal("download not allowed (HTTP 403)", new DownloadResult(DownloadStatus.NotAllowed, HttpStatus: 403).Describe());
        Assert.Equal("download failed", new DownloadResult(DownloadStatus.Failed).Describe());
    }
}
