using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NtfyBar.Core;

/// <summary>
/// Encrypts secrets at rest. The App implements it with DPAPI (CurrentUser); tests use a fake.
/// May throw <see cref="CryptographicException"/> (e.g. the file came from another Windows user).
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] cipher);
}

/// <summary>
/// Per-server stream bookmark: the newest message time seen and the ids seen at that second.
/// Reconnects ask for <c>since=&lt;Time&gt;</c>; ntfy returns messages with time ≥ since, so the ids
/// drop the ones we already had.
/// </summary>
public sealed class StreamCursor
{
    public long Time { get; set; }
    public List<string> Ids { get; set; } = [];

    public bool HasSeen(NtfyMessage m) => m.Time == Time && Ids.Contains(m.Id);

    public void Advance(NtfyMessage m)
    {
        if (m.Time > Time)
        {
            Time = m.Time;
            Ids = [m.Id];
        }
        else if (m.Time == Time && !Ids.Contains(m.Id))
        {
            Ids.Add(m.Id);
        }
    }
}

/// <summary>
/// <c>config.json</c> + <c>state.json</c> in one directory (default <c>%APPDATA%\NtfyBar</c>).
/// Writes are atomic (temp file + rename). <c>auth.pass</c> and <c>auth.token</c> are stored as
/// <c>dpapi:&lt;base64&gt;</c> when a protector is given; plain values found on disk are accepted
/// and get protected on the next save.
/// </summary>
public sealed class ConfigStore(string? directory = null, ISecretProtector? protector = null, ILog? log = null)
{
    public const string SecretPrefix = "dpapi:";

    private readonly ILog _log = log ?? NullLog.Instance;

    public string Directory { get; } = directory ?? DefaultDirectory;
    public string ConfigPath => Path.Combine(Directory, "config.json");
    public string StatePath => Path.Combine(Directory, "state.json");

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NtfyBar");

    /// <summary>
    /// Missing file → defaults. Unreadable or invalid JSON → null: the caller keeps its last good
    /// config, and the file is left untouched for the user to fix.
    /// </summary>
    public AppConfig? LoadConfig()
    {
        if (!File.Exists(ConfigPath)) return new AppConfig();
        AppConfig config;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                _log.Error("config.json is not a JSON object; keeping the last good config");
                return null;
            }
            config = AppConfig.FromJson(doc.RootElement);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Error($"config.json unreadable ({e.GetType().Name}); keeping the last good config");
            return null;
        }
        foreach (var s in config.Servers)
        {
            s.Auth.Pass = Reveal(s.Auth.Pass);
            s.Auth.Token = Reveal(s.Auth.Token);
        }
        return config;
    }

    public void SaveConfig(AppConfig config)
    {
        var node = JsonSerializer.SerializeToNode(config, Json.Options)!;
        foreach (var server in node["servers"]!.AsArray())
        {
            if (server?["auth"] is not JsonObject auth) continue;
            foreach (var key in new[] { "pass", "token" })
                if (auth[key]?.GetValue<string>() is { Length: > 0 } value)
                    auth[key] = Hide(value);
        }
        WriteAtomic(ConfigPath, node.ToJsonString(Json.Options));
    }

    /// <summary>Server id → cursor. Missing or broken file → empty (it is only a cache).</summary>
    public Dictionary<Guid, StreamCursor> LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<Dictionary<Guid, StreamCursor>>(File.ReadAllText(StatePath), Json.Options) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Warn($"state.json unreadable ({e.GetType().Name}); starting without a since cursor");
        }
        return [];
    }

    public void SaveState(IReadOnlyDictionary<Guid, StreamCursor> state) =>
        WriteAtomic(StatePath, JsonSerializer.Serialize(state, Json.Options));

    private string Hide(string plain) =>
        protector is null || plain.StartsWith(SecretPrefix, StringComparison.Ordinal)
            ? plain
            : SecretPrefix + Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(plain)));

    private string? Reveal(string? stored)
    {
        if (protector is null || stored is null || !stored.StartsWith(SecretPrefix, StringComparison.Ordinal)) return stored;
        try
        {
            return Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(stored[SecretPrefix.Length..])));
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            _log.Error($"a stored secret could not be decrypted ({e.GetType().Name}); re-enter it in Settings");
            return "";
        }
    }

    private void WriteAtomic(string path, string text)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
