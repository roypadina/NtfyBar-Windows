using System.Net;
using System.Text;
using NtfyBar.Core;

namespace NtfyBar.Core.Tests;

public class FileNameTests
{
    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("..\\..\\Windows\\evil.exe", "....Windowsevil.exe")]
    [InlineData("a/b\\c.txt", "abc.txt")]
    [InlineData("a:b*c?d\"e<f>g|h.txt", "abcdefgh.txt")]
    [InlineData("tab\there\u0001.txt", "tabhere.txt")]
    [InlineData("invoice\u202Etxt.exe", "invoicetxt.exe")]  // right-to-left override spoofing
    [InlineData("  name. . ", "name")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("Nul.tar.gz", "_Nul.tar.gz")]
    [InlineData("COM1.log", "_COM1.log")]
    [InlineData("lpt9", "_lpt9")]
    [InlineData("COM10.txt", "COM10.txt")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("קובץ 😀.pdf", "קובץ 😀.pdf")]
    public void Sanitize(string raw, string expected) => Assert.Equal(expected, FileNames.Sanitize(raw));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "abc.jpg")]
    [InlineData("...", "abc.jpg")]
    [InlineData("<>|", null)]
    public void FallsBackToUrlNameThenAttachment(string? raw, string? fallback) =>
        Assert.Equal(fallback ?? "attachment", FileNames.Sanitize(raw, fallback));

    [Fact]
    public void LongNamesAreCappedKeepingTheExtension()
    {
        var capped = FileNames.Sanitize(new string('a', 300) + ".pdf");
        Assert.Equal(FileNames.MaxLength, capped.Length);
        Assert.EndsWith("a.pdf", capped);

        // 😀 is two UTF-16 chars; the cut must not leave half of one.
        var emoji = FileNames.Sanitize("x" + string.Concat(Enumerable.Repeat("😀", 100)) + ".txt");
        Assert.True(emoji.Length <= FileNames.MaxLength);
        Assert.False(char.IsHighSurrogate(emoji[^5]));
        Assert.EndsWith(".txt", emoji);
    }

    [Fact]
    public void UniqueAddsACounter()
    {
        var taken = new HashSet<string> { "photo.jpg", "photo (2).jpg", "README" };
        Assert.Equal("photo (3).jpg", FileNames.Unique("photo.jpg", taken.Contains));
        Assert.Equal("README (2)", FileNames.Unique("README", taken.Contains));
        Assert.Equal("new.txt", FileNames.Unique("new.txt", taken.Contains));
    }
}

public class AttachmentDownloadTests
{
    private static readonly ServerConfig Server = new() { Url = "https://ntfy.example", Auth = ServerAuth.WithBasic("someone", "pw") };

    [Theory]
    [InlineData("https://ntfy.example/file/x.jpg", true)]
    [InlineData("https://NTFY.example:443/file/x.jpg", true)]
    [InlineData("http://ntfy.example/file/x.jpg", false)]
    [InlineData("https://ntfy.example:8443/file/x.jpg", false)]
    [InlineData("https://cdn.example/file/x.jpg", false)]
    [InlineData("https://ntfy.example.evil.example/file/x.jpg", false)]
    public void AuthOnlyToTheServersOrigin(string url, bool withAuth) =>
        Assert.Equal(withAuth ? Server.Auth.HeaderValue : null, AttachmentDownloader.AuthHeaderFor(new Uri(url), Server));

    [Fact]
    public async Task SavesWithSanitizedUniqueNamesAndSameOriginAuth()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var d = new AttachmentDownloader(new HttpClient(handler));
        var a = new NtfyAttachment("CON.jpg", "https://ntfy.example/file/abc.jpg", "image/jpeg", 3, null);

        var first = await d.DownloadFileAsync(a, Server, dir.File("dl"));
        var second = await d.DownloadFileAsync(a, Server, dir.File("dl"));
        var foreign = await d.DownloadFileAsync(a with { Url = "https://cdn.example/abc.jpg" }, Server, dir.File("dl"));

        Assert.Equal(DownloadStatus.Saved, first.Status);
        Assert.Equal(dir.File(Path.Combine("dl", "_CON.jpg")), first.Path);
        Assert.Equal(dir.File(Path.Combine("dl", "_CON (2).jpg")), second.Path);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(first.Path!));
        Assert.Equal(Server.Auth.HeaderValue, handler.Requests[0].Header("Authorization"));
        Assert.Null(handler.Requests[2].Header("Authorization"));
        Assert.Equal(DownloadStatus.Saved, foreign.Status);
    }

    [Theory]
    [InlineData(404, DownloadStatus.Expired)]
    [InlineData(410, DownloadStatus.Expired)]
    [InlineData(401, DownloadStatus.NotAllowed)]
    [InlineData(403, DownloadStatus.NotAllowed)]
    [InlineData(500, DownloadStatus.Failed)]
    [InlineData(302, DownloadStatus.Failed)]
    public async Task HttpErrorsMapAndWriteNothing(int code, DownloadStatus expected)
    {
        using var dir = new TempDir();
        var d = new AttachmentDownloader(new HttpClient(new FakeHandler(_ => FakeHandler.Status(code))));
        var r = await d.DownloadFileAsync(new NtfyAttachment("a.bin", "https://ntfy.example/file/a", null, 1, null), Server, dir.Path);
        Assert.Equal(expected, r.Status);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task TooLargeIsSkippedBeforeOrDuringDownload()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(_ =>
        {
            var content = new ByteArrayContent([0]);
            content.Headers.ContentLength = AttachmentDownloader.MaxBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var d = new AttachmentDownloader(new HttpClient(handler));
        var declared = await d.DownloadFileAsync(new NtfyAttachment("big.iso", "https://ntfy.example/file/b", null, AttachmentDownloader.MaxBytes + 1, null), Server, dir.Path);
        Assert.Equal(DownloadStatus.TooLarge, declared.Status);
        Assert.Empty(handler.Requests);  // refused from the message's size alone

        var lied = await d.DownloadFileAsync(new NtfyAttachment("big.iso", "https://ntfy.example/file/b", null, 10, null), Server, dir.Path);
        Assert.Equal(DownloadStatus.TooLarge, lied.Status);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task TextAttachmentIsDecodedAndCappedAtOneMegabyte()
    {
        var text = "שלום 😀\nline 2  ";
        var body = Encoding.UTF8.GetBytes(text);
        var d = new AttachmentDownloader(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) })));
        var a = new NtfyAttachment("clip.txt", "https://ntfy.example/file/t.txt", "text/plain", body.Length, null);
        var r = await d.DownloadTextAsync(a, Server);
        Assert.Equal((DownloadStatus.Saved, text), (r.Status, r.Text));

        // Body larger than announced, no Content-Length: stopped at the cap.
        var unbounded = new PushStream();
        unbounded.SendRaw(new byte[Clip.MaxTextAttachmentBytes + 1]);
        unbounded.End();
        var big = new AttachmentDownloader(new HttpClient(new FakeHandler(_ => FakeHandler.Stream(unbounded))));
        Assert.Equal(DownloadStatus.TooLarge, (await big.DownloadTextAsync(a, Server)).Status);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData(null)]
    public async Task NonHttpUrlsAreRefusedWithoutARequest(string? url)
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(200));
        var d = new AttachmentDownloader(new HttpClient(handler));
        var r = await d.DownloadFileAsync(new NtfyAttachment("a", url, null, 1, null), Server, Path.GetTempPath());
        Assert.Equal(DownloadStatus.Failed, r.Status);
        Assert.Empty(handler.Requests);
    }
}
