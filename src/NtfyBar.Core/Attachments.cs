using System.Globalization;
using System.Text;

namespace NtfyBar.Core;

/// <summary>Windows-safe names for files received from the network (message content is attacker-controlled).</summary>
public static class FileNames
{
    public const int MaxLength = 120;

    private static readonly char[] Invalid = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// One plain file name: strips <c>\ / : * ? " &lt; &gt; |</c>, control and bidi/format characters,
    /// trailing dots and spaces; prefixes reserved device names (CON, NUL, COM1 …) with <c>_</c>; caps
    /// the length keeping the extension. Falls back to <paramref name="fallback"/>, then <c>attachment</c>.
    /// </summary>
    public static string Sanitize(string? raw, string? fallback = null)
    {
        foreach (var candidate in new[] { raw, fallback })
        {
            if (candidate is null) continue;
            var sb = new StringBuilder(candidate.Length);
            foreach (var ch in candidate)
                if (!char.IsControl(ch) && Array.IndexOf(Invalid, ch) < 0
                    && CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.Format)
                    sb.Append(ch);
            var name = Cap(sb.ToString().Trim().TrimEnd('.', ' '));
            if (name.Length == 0) continue;
            return IsReserved(name) ? "_" + name : name;
        }
        return "attachment";
    }

    /// <summary><c>photo.jpg</c> → <c>photo (2).jpg</c> → <c>photo (3).jpg</c> … until <paramref name="exists"/> says no.</summary>
    public static string Unique(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!exists(candidate)) return candidate;
        }
    }

    // CON, PRN, AUX, NUL, COM1-9, LPT1-9 are devices on Windows, with or without an extension.
    private static bool IsReserved(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }

    private static string Cap(string name)
    {
        if (name.Length <= MaxLength) return name;
        var ext = Path.GetExtension(name);
        if (ext.Length > 16) ext = "";
        var stem = name[..(MaxLength - ext.Length)];
        if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        return stem.TrimEnd('.', ' ') + ext;
    }
}

public enum DownloadStatus
{
    Saved,
    /// <summary>404 (or 410): the server already deleted it.</summary>
    Expired,
    /// <summary>Over the size limit: skipped, nothing written.</summary>
    TooLarge,
    /// <summary>401/403.</summary>
    NotAllowed,
    Failed,
}

/// <param name="Path">Saved file (<see cref="AttachmentDownloader.DownloadFileAsync"/>).</param>
/// <param name="Text">Decoded text (<see cref="AttachmentDownloader.DownloadTextAsync"/>).</param>
public sealed record DownloadResult(DownloadStatus Status, string? Path = null, string? Text = null, int HttpStatus = 0);

/// <summary>
/// Attachment downloads. Only http(s) URLs; the server's Authorization header goes only to the same
/// origin (scheme + host + port) as the server; nothing is written outside the target folder.
/// </summary>
public sealed class AttachmentDownloader(HttpClient http, ILog? log = null)
{
    public const long MaxBytes = 100L * 1024 * 1024;

    private readonly ILog _log = log ?? NullLog.Instance;

    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    public static string? AuthHeaderFor(Uri url, ServerConfig? server) =>
        server?.BaseUri is { } b && SameOrigin(url, b) ? server.Auth.HeaderValue : null;

    /// <summary>Saves into <paramref name="dir"/> (created on demand) under a sanitized, unique name.</summary>
    public async Task<DownloadResult> DownloadFileAsync(NtfyAttachment a, ServerConfig? server, string dir, CancellationToken ct = default)
    {
        return await FetchAsync(a, server, MaxBytes, async body =>
        {
            Directory.CreateDirectory(dir);
            var root = Path.GetFullPath(dir);
            var fallback = Uri.TryCreate(a.Url, UriKind.Absolute, out var u) ? u.Segments[^1] : null;
            var name = FileNames.Unique(FileNames.Sanitize(a.Name, fallback), n => File.Exists(Path.Combine(root, n)));
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (Path.GetFileName(path) != name) return new DownloadResult(DownloadStatus.Failed);  // never outside root
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            var complete = false;
            try
            {
                await using (file)
                    complete = await CopyCappedAsync(body, file, MaxBytes, ct);
            }
            finally
            {
                if (!complete) File.Delete(path);
            }
            return complete ? new DownloadResult(DownloadStatus.Saved, Path: path) : new DownloadResult(DownloadStatus.TooLarge);
        }, ct);
    }

    /// <summary>Text attachment (<see cref="Clip.IsTextAttachment"/>): ≤ 1 MB, decoded as UTF-8.</summary>
    public async Task<DownloadResult> DownloadTextAsync(NtfyAttachment a, ServerConfig? server, CancellationToken ct = default)
    {
        return await FetchAsync(a, server, Clip.MaxTextAttachmentBytes, async body =>
        {
            using var buffer = new MemoryStream();
            if (!await CopyCappedAsync(body, buffer, Clip.MaxTextAttachmentBytes, ct))
                return new DownloadResult(DownloadStatus.TooLarge);
            return new DownloadResult(DownloadStatus.Saved, Text: Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }, ct);
    }

    private async Task<DownloadResult> FetchAsync(NtfyAttachment a, ServerConfig? server, long max,
        Func<Stream, Task<DownloadResult>> save, CancellationToken ct)
    {
        if (!Uri.TryCreate(a.Url, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            return new DownloadResult(DownloadStatus.Failed);
        if (a.Size > max) return new DownloadResult(DownloadStatus.TooLarge);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (AuthHeaderFor(url, server) is { } auth) request.Headers.TryAddWithoutValidation("Authorization", auth);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (status is 404 or 410) return new DownloadResult(DownloadStatus.Expired, HttpStatus: status);
            if (status is 401 or 403) return new DownloadResult(DownloadStatus.NotAllowed, HttpStatus: status);
            if (!response.IsSuccessStatusCode) return new DownloadResult(DownloadStatus.Failed, HttpStatus: status);
            if (response.Content.Headers.ContentLength > max) return new DownloadResult(DownloadStatus.TooLarge);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            return await save(body);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"attachment download failed: {e.GetType().Name}");
            return new DownloadResult(DownloadStatus.Failed);
        }
    }

    /// <summary>False when the body turned out bigger than <paramref name="max"/> (no Content-Length, or it lied).</summary>
    private static async Task<bool> CopyCappedAsync(Stream from, Stream to, long max, CancellationToken ct)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buf, ct)) > 0)
        {
            total += n;
            if (total > max) return false;
            await to.WriteAsync(buf.AsMemory(0, n), ct);
        }
        return true;
    }
}
