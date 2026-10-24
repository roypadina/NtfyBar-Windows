using System.Diagnostics;
using System.IO.Pipes;
using NtfyBar.Core;

namespace NtfyBar.App;

/// <summary>
/// Named pipe between a second launch and the running instance. <see cref="PipeOptions.CurrentUserOnly"/>
/// on both ends: another Windows user must not be able to make us upload their choice of files.
/// Wire format: the argv, one argument per line.
/// </summary>
internal static class SingleInstance
{
    private static readonly string PipeName = $"NtfyBar.{Process.GetCurrentProcess().SessionId}.args";

    public static void Forward(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            using var writer = new StreamWriter(pipe);
            writer.Write(string.Join('\n', args));
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // The running instance is busy or exiting; nothing better to do from a second process.
        }
    }

    /// <summary>Runs until cancelled; <paramref name="onArgs"/> runs on the caller's synchronization context (UI thread).</summary>
    public static async Task ListenAsync(Action<string[]> onArgs, ILog log, CancellationToken ct)
    {
        var logged = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe);
                var text = await reader.ReadToEndAsync(ct);
                onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Client vanished mid-write, or the pipe can't be created: pause so a persistent failure doesn't spin.
                if (!logged) log.Warn($"single-instance pipe failed: {e.GetType().Name}");
                logged = true;
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
