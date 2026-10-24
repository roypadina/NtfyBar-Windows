using Microsoft.Toolkit.Uwp.Notifications;
using NtfyBar.Core;

namespace NtfyBar.App;

internal static class Program
{
    /// <summary>Per logon session: a second launch (Send To, Explorer double-click) hands its args over and exits.</summary>
    private const string MutexName = @"Local\NtfyBar.SingleInstance";

    private static DateTime _lastErrorToast = DateTime.MinValue;

    [STAThread]
    private static void Main(string[] args)
    {
        // Full paths before they are forwarded: the running instance has another working folder.
        if (CommandLine.SendFiles(args) is { } files) args = [CommandLine.Send, .. files];
        var uninstall = args.Contains(CommandLine.Uninstall);
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first && !uninstall)
        {
            SingleInstance.Forward(args);
            return;
        }
        ApplicationConfiguration.Initialize();
        if (uninstall)
        {
            Uninstall(running: !first);
            return;
        }

        var log = new FileLog(FileLog.DefaultDirectory);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(log, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error($"fatal error: {Describe(e.ExceptionObject as Exception)}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error($"unobserved task error: {Describe(e.Exception)}");
            e.SetObserved();
        };
        using var tray = new TrayContext(args, log);
        Application.Run(tray);
    }

    /// <summary>A UI-thread exception: logged, at most one toast a minute, the app keeps running (never a dialog loop).</summary>
    private static void Report(ILog log, Exception e)
    {
        log.Error($"unexpected error: {Describe(e)}");
        if (DateTime.UtcNow - _lastErrorToast < TimeSpan.FromMinutes(1)) return;
        _lastErrorToast = DateTime.UtcNow;
        Toasts.Show("NtfyBar hit an unexpected error", @"It keeps running. Details: %LOCALAPPDATA%\NtfyBar\logs");
    }

    /// <summary>Type names and stack only: exception messages can hold message text, paths or the server host.</summary>
    private static string Describe(Exception? e)
    {
        if (e is null) return "unknown";
        var types = string.Join(" <- ", Enumerable.Repeat(e, 1).Concat(Inner(e)).Select(x => x.GetType().FullName));
        return $"{types}{Environment.NewLine}{e.StackTrace}";

        static IEnumerable<Exception> Inner(Exception e)
        {
            for (var x = e.InnerException; x is not null; x = x.InnerException) yield return x;
        }
    }

    /// <summary><c>NtfyBar.exe --uninstall</c>: removes what the app registered outside its folder.</summary>
    private static void Uninstall(bool running)
    {
        if (running)
        {
            MessageBox.Show("Quit NtfyBar from its tray menu first, then run this again.", "NtfyBar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var failed = new List<string>();
        foreach (var (what, action) in new (string, Action)[]
                 {
                     ("Start with Windows", () => Shell.SetAutostart(false)),
                     ("Send To", () => Shell.SetSendTo(false)),
                     ("notification registration", ToastNotificationManagerCompat.Uninstall),
                 })
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failed.Add($"{what} ({e.GetType().Name})");
            }
        }
        MessageBox.Show(failed.Count == 0
                ? "Removed Start with Windows, the Send To entry and the notification registration.\n\n" +
                  @"To finish, delete the NtfyBar folder, plus %APPDATA%\NtfyBar (settings) and %LOCALAPPDATA%\NtfyBar (logs) unless you want to keep them."
                : "Could not remove: " + string.Join(", ", failed),
            "NtfyBar", MessageBoxButtons.OK, failed.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }
}
