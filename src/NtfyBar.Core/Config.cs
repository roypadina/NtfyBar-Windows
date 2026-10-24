using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NtfyBar.Core;

/// <summary>
/// <c>config.json</c>: a subset of the macOS NtfyBar schema (same field names), so knowledge and
/// hand-edited snippets transfer. Secrets are plain text in memory; <see cref="ConfigStore"/>
/// stores them as <c>dpapi:&lt;base64&gt;</c> on disk.
/// </summary>
public sealed class AppConfig
{
    public List<ServerConfig> Servers { get; set; } = [new ServerConfig()];
    public List<TopicConfig> Topics { get; set; } = [];
    public PublishSettings Publish { get; set; } = new();
    /// <summary>Where received files go. Default <c>%USERPROFILE%\Downloads\NtfyBar</c>.</summary>
    public string DownloadDir { get; set; } = DefaultDownloadDir;
    public bool LaunchAtLogin { get; set; }

    public static string DefaultDownloadDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NtfyBar");

    public ServerConfig? Server(Guid id) => Servers.FirstOrDefault(s => s.Id == id);
    public TopicConfig? Topic(Guid id) => Topics.FirstOrDefault(t => t.Id == id);

    /// <summary>Deep copy for editing (Settings form): change the copy, then pass it to <see cref="Engine.UpdateConfig"/>.</summary>
    public AppConfig Clone() => FromJson(JsonSerializer.SerializeToElement(this, Json.Options));

    public static AppConfig FromJson(JsonElement o)
    {
        var d = new AppConfig();
        var dir = o.Get("downloadDir", "");
        return new AppConfig
        {
            Servers = o.GetList("servers", ServerConfig.FromJson, d.Servers),
            Topics = o.GetList("topics", TopicConfig.FromJson, []),
            Publish = o.TryGetProperty("publish", out var p) ? PublishSettings.FromJson(p) : new PublishSettings(),
            DownloadDir = string.IsNullOrWhiteSpace(dir) ? DefaultDownloadDir : dir,
            LaunchAtLogin = o.Get("launchAtLogin", false),
        };
    }
}

public sealed class ServerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "https://ntfy.sh";
    public ServerAuth Auth { get; set; } = new();

    /// <summary>
    /// Base URL without a trailing slash, or null when invalid: http/https with a host, and no
    /// user info, query or fragment (they would leak into or break every request built on it).
    /// </summary>
    [JsonIgnore]
    public Uri? BaseUri => ValidatedUri(Url);

    public static Uri? ValidatedUri(string? text)
    {
        var trimmed = (text ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return null;
        if (u.Host.Length == 0 || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0) return null;
        return u;
    }

    public static ServerConfig FromJson(JsonElement o) => new()
    {
        Id = o.Get("id", Guid.NewGuid()),
        Url = o.Get("url", "https://ntfy.sh"),
        Auth = o.TryGetProperty("auth", out var a) ? ServerAuth.FromJson(a) : new ServerAuth(),
    };
}

public static class AuthTypes
{
    public const string None = "none", Basic = "basic", Token = "token";
}

/// <summary><c>{type:"none"}</c>, <c>{type:"basic",user,pass}</c> or <c>{type:"token",token}</c>.</summary>
public sealed class ServerAuth
{
    /// <summary>One of <see cref="AuthTypes"/>.</summary>
    public string Type { get; set; } = AuthTypes.None;
    public string? User { get; set; }
    public string? Pass { get; set; }
    public string? Token { get; set; }

    public static ServerAuth WithBasic(string user, string pass) => new() { Type = AuthTypes.Basic, User = user, Pass = pass };
    public static ServerAuth WithToken(string token) => new() { Type = AuthTypes.Token, Token = token };

    /// <summary>Value for the <c>Authorization</c> header, null when the server needs none.</summary>
    [JsonIgnore]
    public string? HeaderValue => Type switch
    {
        AuthTypes.Basic => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Pass}")),
        AuthTypes.Token => string.IsNullOrEmpty(Token) ? null : "Bearer " + Token,
        _ => null,
    };

    public static ServerAuth FromJson(JsonElement o) => o.Get("type", AuthTypes.None) switch
    {
        AuthTypes.Basic => WithBasic(o.Get("user", ""), o.Get("pass", "")),
        AuthTypes.Token => WithToken(o.Get("token", "")),
        _ => new ServerAuth(),
    };
}

public sealed partial class TopicConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ServerId { get; set; }
    /// <summary>The raw topic name: a capability. Never log it.</summary>
    public string Topic { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Notify { get; set; } = true;
    public bool CopyToClipboard { get; set; }
    public bool DownloadAttachments { get; set; }
    public bool CopyAttachment { get; set; }
    /// <summary>Catch-up messages (older than the freshness window) act like live ones.</summary>
    public bool ProcessMissed { get; set; }

    [JsonIgnore]
    public string DisplayLabel => Label.Length == 0 ? "Untitled topic" : Label;

    /// <summary>ntfy topic names: a comma would subscribe a second topic, <c>/</c> or <c>?</c> would inject into the URL.</summary>
    public static bool IsValidTopicName(string? name) => name is not null && TopicName().IsMatch(name);

    [GeneratedRegex("^[-_A-Za-z0-9]{1,64}$")]
    private static partial Regex TopicName();

    public static TopicConfig FromJson(JsonElement o)
    {
        var d = new TopicConfig();
        return new TopicConfig
        {
            Id = o.Get("id", Guid.NewGuid()),
            ServerId = o.Get("serverId", Guid.Empty),
            Topic = o.Get("topic", ""),
            Label = o.Get("label", ""),
            Enabled = o.Get("enabled", d.Enabled),
            Notify = o.Get("notify", d.Notify),
            CopyToClipboard = o.Get("copyToClipboard", d.CopyToClipboard),
            DownloadAttachments = o.Get("downloadAttachments", d.DownloadAttachments),
            CopyAttachment = o.Get("copyAttachment", d.CopyAttachment),
            ProcessMissed = o.Get("processMissed", d.ProcessMissed),
        };
    }
}

public sealed class PublishSettings
{
    /// <summary>Sender id, always the first tag of everything this app publishes.</summary>
    public string DeviceTag { get; set; } = "ntfybar-win";
    /// <summary>Default title of published messages (the device name).</summary>
    public string Title { get; set; } = Environment.MachineName;
    public Guid? LastTopicId { get; set; }
    /// <summary>Global "Send Clipboard" hotkey, e.g. <c>Ctrl+Alt+Shift+C</c>.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Shift+C";

    public static PublishSettings FromJson(JsonElement o)
    {
        var d = new PublishSettings();
        return new PublishSettings
        {
            DeviceTag = o.Get("deviceTag", d.DeviceTag),
            Title = o.Get("title", d.Title),
            LastTopicId = o.Get<Guid?>("lastTopicId", null),
            Hotkey = o.Get("hotkey", d.Hotkey),
        };
    }
}
