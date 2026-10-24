using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NtfyBar.Core;

namespace NtfyBar.App;

/// <summary>Explorer, autostart and Send To integration. Everything is per user (HKCU, %APPDATA%): no admin rights.</summary>
internal static class Shell
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NtfyBar";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static string SendToShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "NtfyBar.lnk");

    /// <summary>HKCU Run entry pointing at this exe (rewritten each time, so a moved exe is picked up).</summary>
    public static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{ExePath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    public static bool SendToExists => File.Exists(SendToShortcut);

    /// <summary>
    /// Creates or removes <c>SendTo\NtfyBar.lnk</c> → <c>NtfyBar.exe --send</c>; Explorer appends the
    /// selected files. WScript.Shell COM writes the .lnk (fewer lines than IShellLink interop).
    /// </summary>
    public static void SetSendTo(bool on)
    {
        if (!on)
        {
            File.Delete(SendToShortcut);
            return;
        }
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new COMException("WScript.Shell is not available");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(SendToShortcut);
            try
            {
                link.TargetPath = ExePath;
                link.Arguments = CommandLine.Send;
                link.IconLocation = ExePath + ",0";
                link.Description = "Send the files to your ntfy topic with NtfyBar";
                link.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Opens a file with its default app. False when Windows has no app for it.</summary>
    public static bool Open(string path) => Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Explorer with the file selected.</summary>
    public static void Reveal(string path) => Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));

    public static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    /// <summary>
    /// Mark of the Web (Zone.Identifier, Internet zone) on a downloaded file, like a browser does:
    /// SmartScreen asks before running a received .exe and Office opens documents in Protected View.
    /// Silently skipped where alternate data streams don't exist (FAT32/exFAT drives).
    /// </summary>
    public static void MarkFromInternet(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private static bool Start(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}
