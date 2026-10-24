namespace NtfyBar.App;

internal static class Program
{
    /// <summary>Per logon session: a second launch (Send To, Explorer double-click) hands its args over and exits.</summary>
    private const string MutexName = @"Local\NtfyBar.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            SingleInstance.Forward(args);
            return;
        }
        ApplicationConfiguration.Initialize();
        using var tray = new TrayContext(args);
        Application.Run(tray);
    }
}
