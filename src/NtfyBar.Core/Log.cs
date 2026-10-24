namespace NtfyBar.Core;

public enum LogLevel { Info, Warn, Error }

/// <summary>
/// Minimal log sink. Callers never pass message bodies, titles, tokens, passwords, topic names or
/// server URLs: ids, counts, sizes, HTTP status codes and exception type names only.
/// </summary>
public interface ILog
{
    void Write(LogLevel level, string message);
}

public static class LogExtensions
{
    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);
    public static void Warn(this ILog log, string message) => log.Write(LogLevel.Warn, message);
    public static void Error(this ILog log, string message) => log.Write(LogLevel.Error, message);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string message) { }
}

/// <summary>
/// Rolling file log: <c>ntfybar.log</c>, rotated to <c>ntfybar.1.log</c> … <c>ntfybar.{maxFiles-1}.log</c>
/// once it passes <paramref name="maxBytes"/>. Never throws: logging must not take the app down.
/// </summary>
public sealed class FileLog(string directory, long maxBytes = 1_000_000, int maxFiles = 3) : ILog
{
    private readonly object _gate = new();

    public string Directory { get; } = directory;
    public string CurrentPath => Path.Combine(Directory, "ntfybar.log");

    /// <summary><c>%LOCALAPPDATA%\NtfyBar\logs</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtfyBar", "logs");

    public void Write(LogLevel level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level.ToString().ToUpperInvariant(),-5} {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (File.Exists(CurrentPath) && new FileInfo(CurrentPath).Length > maxBytes)
                    Rotate();
                File.AppendAllText(CurrentPath, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Disk full / locked file: drop the line.
            }
        }
    }

    private void Rotate()
    {
        string Numbered(int i) => Path.Combine(Directory, $"ntfybar.{i}.log");
        File.Delete(Numbered(maxFiles - 1));
        for (var i = maxFiles - 2; i >= 1; i--)
            if (File.Exists(Numbered(i)))
                File.Move(Numbered(i), Numbered(i + 1));
        File.Move(CurrentPath, Numbered(1));
    }
}
