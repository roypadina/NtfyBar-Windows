using Microsoft.Toolkit.Uwp.Notifications;
using NtfyBar.Core;

namespace NtfyBar.App;

/// <summary>A toast button: <paramref name="Action"/> + optional <paramref name="Value"/> come back in the activation arguments.</summary>
internal sealed record ToastButtonSpec(string Label, string Action, string? Value = null);

/// <summary>
/// Thin wrapper over the Community Toolkit compat API (works unpackaged: it registers an AUMID and a
/// COM activator for this exe under HKCU). Toasts fail silently for elevated processes.
/// </summary>
internal static class Toasts
{
    public const string ActionKey = "action";
    public const string ValueKey = "value";

    private static ILog _log = NullLog.Instance;

    /// <summary>
    /// Call once, on the UI thread. <paramref name="onActivated"/> gets the clicked button's arguments
    /// (empty when the toast body itself was clicked), marshalled to the UI thread. Also fires when a
    /// click launched the app (<c>-ToastActivated</c>).
    /// </summary>
    public static void Init(Action<ToastArguments> onActivated, ILog log)
    {
        _log = log;
        var ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Toasts.Init must run on the UI thread");
        ToastNotificationManagerCompat.OnActivated += e => ui.Post(_ => onActivated(ToastArguments.Parse(e.Argument)), null);
    }

    /// <summary>Up to 2 buttons (more are ignored).</summary>
    public static void Show(string title, string body, params ToastButtonSpec[] buttons)
    {
        try
        {
            var builder = new ToastContentBuilder().AddText(title).AddText(body);
            foreach (var b in buttons.Take(2))
            {
                var button = new ToastButton().SetContent(b.Label).AddArgument(ActionKey, b.Action);
                if (b.Value is not null) button.AddArgument(ValueKey, b.Value);
                builder.AddButton(button);
            }
            builder.Show();
        }
        catch (Exception e)
        {
            // Notifications disabled, no AUMID yet, elevated process …: never crash the tray for a toast.
            _log.Error($"toast failed: {e.GetType().Name}");
        }
    }
}
