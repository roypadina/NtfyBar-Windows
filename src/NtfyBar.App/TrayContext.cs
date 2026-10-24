using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using NtfyBar.Core;

namespace NtfyBar.App;

/// <summary>
/// The tray app: icon + menu; Core engine messages → clipboard, toasts and Recent (design §3.2);
/// Send Clipboard (menu + hotkey), Publish…, Settings…, Explorer Send To (args from the single-instance
/// pipe). Everything runs on the UI thread: Engine events, toast clicks and pipe args arrive there.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    // Toast button actions. Value: a Recent item id (copy, fetch) or a saved file path (open, reveal).
    private const string CopyAction = "copy", FetchAction = "fetch", OpenAction = "open", RevealAction = "reveal", SettingsAction = "settings";

    private readonly ILog _log;
    private readonly Engine _engine;
    private readonly NotifyIcon _icon;
    private readonly Icon _formIcon = LoadIcon(null);
    private readonly ToolStripMenuItem _status = new("Starting…") { Enabled = false };
    private readonly ToolStripMenuItem _recentMenu = new("Recent");
    private readonly ToolStripMenuItem _sendClipboard = new("Send Clipboard");
    private readonly ToolStripMenuItem _autostart = new("Start with Windows");
    private readonly RecentList _recent = new();
    private readonly FileBurst _burst = new(FileBurst.DefaultWindow);
    private readonly CancellationTokenSource _quit = new();
    private int _burstNumber;
    private HotKey? _hotKey;
    private string? _hotKeySpec;
    private PublishForm? _publish;
    private SettingsForm? _settings;

    public TrayContext(string[] args, ILog log)
    {
        _log = log;
        _log.Info($"NtfyBar {Application.ProductVersion} starting");
        var store = new ConfigStore(protector: new DpapiProtector(), log: _log);
        var firstRun = !File.Exists(store.ConfigPath);
        _engine = new Engine(store, _log);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_recentMenu);
        menu.Items.Add(_sendClipboard);
        menu.Items.Add("Publish…", null, (_, _) => ShowPublish([]));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Downloads", null, (_, _) => Shell.OpenFolder(_engine.Config.DownloadDir));
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings(firstRun: false));
        menu.Items.Add(_autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit NtfyBar", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => RefreshMenu();
        _sendClipboard.Click += (_, _) => SendClipboard();
        _autostart.Click += (_, _) => ToggleAutostart();
        _icon = new NotifyIcon { Icon = LoadIcon(SystemInformation.SmallIconSize), Text = "NtfyBar", ContextMenuStrip = menu, Visible = true };

        var toastLaunch = false;
        try
        {
            Toasts.Init(OnToastActivated, _log);
            toastLaunch = ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
            if (toastLaunch) _log.Info("started by a toast click");
        }
        catch (Exception e)
        {
            _log.Warn($"toasts unavailable: {e.GetType().Name}");  // the tray still works; Toasts.Show swallows its own failures
        }

        // Engine events run on the context that calls Start: the UI thread (installed by the first control above).
        _engine.StatusChanged += OnStatusChanged;
        _engine.MessageReceived += OnMessage;
        _engine.MissedSummaryReady += OnMissedSummary;
        var configOk = _engine.Start();
        if (!configOk)
            Toasts.Show("NtfyBar: config.json is not valid", "Fix it (or delete it) and restart; nothing was overwritten.");
        UpdateStatus();
        if (_engine.Config.LaunchAtLogin) SetAutostart(true);  // re-points the entry if the exe moved
        ApplyHotKey();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _ = SingleInstance.ListenAsync(OnArgs, _log, _quit.Token);
        OnArgs(args);
        // First run = the Settings form; not when a toast click started us or config.json needs fixing by hand.
        if (configOk && !toastLaunch && _engine.Config.Topics.Count == 0) ShowSettings(firstRun);
    }

    // ---- Tray menu ----

    private void RefreshMenu()
    {
        _sendClipboard.Text = LastTopic() is { } topic ? $"Send Clipboard to “{MenuText(topic.DisplayLabel)}”" : "Send Clipboard…";
        _autostart.Checked = _engine.Config.LaunchAtLogin;
        _recentMenu.DropDownItems.Clear();
        foreach (var item in _recent.Items)
            _recentMenu.DropDownItems.Add(MenuText(item.Label), null, (_, _) => OnRecentClicked(item));
        if (_recent.Items.Count == 0) _recentMenu.DropDownItems.Add(new ToolStripMenuItem("Nothing yet") { Enabled = false });
    }

    /// <summary>"&amp;" would become a mnemonic underline in a menu.</summary>
    private static string MenuText(string text) => text.Replace("&", "&&");

    private void UpdateStatus()
    {
        var text = _engine.OverallStatus?.Describe() ?? "No topics yet — open Settings";
        _status.Text = text;
        var tip = "NtfyBar — " + text;
        _icon.Text = tip.Length <= 63 ? tip : tip[..63];  // NotifyIcon.Text limit
    }

    private void OnStatusChanged(Guid serverId, StreamStatus status)
    {
        UpdateStatus();
        if (status.IsTerminal)
            Toasts.Show("NtfyBar can't receive", status.Describe(), [new("Settings…", SettingsAction)]);
    }

    private void ToggleAutostart()
    {
        var on = !_engine.Config.LaunchAtLogin;
        if (!SetAutostart(on)) return;
        var c = _engine.Config.Clone();
        c.LaunchAtLogin = on;
        try
        {
            _engine.UpdateConfig(c);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Error($"config not saved: {e.GetType().Name}");
        }
    }

    private bool SetAutostart(bool on)
    {
        try
        {
            Shell.SetAutostart(on);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Error($"autostart not changed: {e.GetType().Name}");
            Toasts.Show("Couldn't change Start with Windows", e.Message);
            return false;
        }
    }

    // ---- Receiving ----

    private async void OnMessage(IncomingMessage m)
    {
        var item = _recent.Add(m);
        _log.Info($"message {m.Message.Id}: {m.Kind}, {m.Handling}");
        if (m.Handling != Handling.Live) return;  // own echo: Recent only (as sent); missed: Recent + the summary toast
        var topic = m.Topic;
        switch (m.Kind)
        {
            case ClipKind.Text:
                if (topic.CopyToClipboard) Copy(item.Text!);
                if (topic.Notify) ShowText(item, item.Text!);
                break;
            case ClipKind.TextAttachment:
                if (!topic.CopyToClipboard)
                {
                    if (topic.Notify) ShowText(item, m.Message.Message ?? "Long text");
                }
                else if (await FetchTextAsync(item) is { } text)
                {
                    Copy(text);
                    if (topic.Notify) ShowText(item, text);
                }
                break;
            case ClipKind.Image or ClipKind.File:
                await OnAttachmentAsync(item);
                break;
            case ClipKind.ExpiredAttachment:
                if (topic.Notify || topic.DownloadAttachments)
                    Toasts.Show(Sender(m), $"{AttachmentName(item)}: attachment expired on the server");
                break;
        }
    }

    private void ShowText(RecentItem item, string text) =>
        Toasts.Show(Sender(item.Message), Publisher.Preview(text), text.Length > 0 ? [new("Copy", CopyAction, item.Id)] : null);

    /// <summary>Download (topic "download files"), then the clipboard (topic "copy files"): pictures as a picture, other files as a file.</summary>
    private async Task OnAttachmentAsync(RecentItem item)
    {
        var m = item.Message;
        if (!m.Topic.DownloadAttachments)
        {
            if (m.Topic.Notify) Toasts.Show(Sender(m), AttachmentName(item), [new("Open", FetchAction, item.Id)]);
            return;
        }
        if (await FetchFileAsync(item) is not { } path) return;
        var copied = m.Topic.CopyAttachment && CopyFile(path, m.Kind == ClipKind.Image);
        // Several files within seconds: one toast that counts up (same tag = replaces the previous one).
        var count = _burst.Add(DateTimeOffset.Now);
        if (count == 1) _burstNumber++;
        if (!m.Topic.Notify) return;
        var tag = $"files-{_burstNumber}";
        if (count == 1)
            Toasts.Show(Sender(m), $"{Path.GetFileName(path)} {(copied ? "saved and copied" : "saved")}",
                [new("Open", OpenAction, path), new("Open folder", RevealAction, path)], tag);
        else
            Toasts.Show($"{count} files saved", copied ? "The last one is on the clipboard." : null, [new("Open folder", RevealAction, path)], tag);
    }

    private void OnMissedSummary(MissedSummary s)
    {
        if (!s.Topic.Notify) return;
        Toasts.Show($"{s.Count} {(s.Count == 1 ? "item" : "items")} on “{s.Topic.DisplayLabel}” while away",
            "Nothing was copied. They are under Recent in the tray menu.", silent: true);
    }

    /// <summary>Recent menu and the toasts' Copy/Open buttons: text → clipboard, file → open (downloading first if needed).</summary>
    private async void OnRecentClicked(RecentItem item)
    {
        switch (item.Message.Kind)
        {
            case ClipKind.Text or ClipKind.TextAttachment:
                if (await FetchTextAsync(item) is { } text) Copy(text);
                break;
            case ClipKind.ExpiredAttachment:
                Toasts.Show(Sender(item.Message), $"{AttachmentName(item)}: attachment expired on the server");
                break;
            default:
                if (await FetchFileAsync(item) is { } path) OpenFile(path);
                break;
        }
    }

    private async Task<string?> FetchTextAsync(RecentItem item)
    {
        if (item.Text is { } known) return known;
        var r = await _engine.DownloadTextAsync(item.Message);
        _log.Info($"text attachment {item.Id}: {r.Status}");
        if (r.Status == DownloadStatus.Saved) return item.Text = r.Text ?? "";
        Toasts.Show(Sender(item.Message), $"Long text not copied: {r.Describe()}");
        return null;
    }

    private async Task<string?> FetchFileAsync(RecentItem item)
    {
        if (item.Path is { } known && File.Exists(known)) return known;
        var r = await _engine.DownloadAsync(item.Message);
        _log.Info($"attachment {item.Id}: {r.Status}");
        if (r is { Status: DownloadStatus.Saved, Path: { } path })
        {
            Shell.MarkFromInternet(path);
            return item.Path = path;
        }
        Toasts.Show(Sender(item.Message), $"{AttachmentName(item)}: {r.Describe()}");
        return null;
    }

    private void Copy(string text)
    {
        if (text.Length == 0) return;
        if (ClipboardIO.SetText(text))
        {
            _log.Info($"copied text ({text.Length} chars)");
            return;
        }
        _log.Warn("clipboard busy: text not copied");
        Toasts.Show("Couldn't copy", "Another app is holding the clipboard. Try again from Recent in the tray menu.");
    }

    private bool CopyFile(string path, bool picture)
    {
        // A picture GDI+ can't decode (WebP, HEIC …) still goes on the clipboard, as a file.
        var ok = (picture && ClipboardIO.SetImageFile(path)) || ClipboardIO.SetFiles(path);
        if (!ok) _log.Warn("clipboard busy: file not copied");
        return ok;
    }

    private void OpenFile(string path)
    {
        if (!Shell.Open(path))
            Toasts.Show("Couldn't open the file", "Windows has no app for this file type.", [new("Open folder", RevealAction, path)]);
    }

    private void OnToastActivated(ToastArguments args)
    {
        args.TryGetValue(Toasts.ActionKey, out var action);
        args.TryGetValue(Toasts.ValueKey, out var value);
        _log.Info($"toast activated: {action ?? "(body)"}");
        switch (action)
        {
            case CopyAction or FetchAction:
                if (_recent.Find(value) is { } item) OnRecentClicked(item);
                else Toasts.Show("That item is no longer in Recent", "NtfyBar was restarted (or 20 newer items arrived) since that notification.");
                break;
            case OpenAction when File.Exists(value):
                OpenFile(value);
                break;
            case RevealAction when File.Exists(value):
                Shell.Reveal(value);
                break;
            case OpenAction or RevealAction:
                Shell.OpenFolder(_engine.Config.DownloadDir);  // the file was moved or deleted
                break;
            case SettingsAction:
                ShowSettings(firstRun: false);
                break;
        }
    }

    private static string Sender(IncomingMessage m) => m.Message.Title is { Length: > 0 } title ? title : m.Topic.DisplayLabel;

    private static string AttachmentName(RecentItem item) => item.Message.Message.Attachment?.Name ?? "Attachment";

    // ---- Sending ----

    private TopicConfig? LastTopic() =>
        _engine.Config.Publish.LastTopicId is { } id && _engine.Config.Topic(id) is { Enabled: true } t ? t : null;

    /// <summary>Tray item and hotkey: the clipboard to the last topic; none yet → Publish….</summary>
    private async void SendClipboard()
    {
        _log.Info("send clipboard");
        if (LastTopic() is not { } topic)
        {
            ShowPublish([]);
            return;
        }
        var clip = ClipboardIO.Read();
        if (clip is null)
            Toasts.Show("Couldn't send", "Another app is holding the clipboard. Try again.");
        else if (clip.Payload == ClipboardPayload.Empty)
            Toasts.Show("Couldn't send: the clipboard is empty");
        else if (clip.Files.Any(Directory.Exists))
            Toasts.Show("Couldn't send: folders can't be sent", "Zip the folder first.");
        else
            ShowResult(await _engine.SendAsync(topic.Id, clip.Text, clip.Png, clip.Files));
    }

    /// <summary>
    /// Command line of this or a second launch. <c>--send &lt;files&gt;</c> (Explorer Send To) publishes
    /// to the last topic; none yet → Publish… with the files filled in.
    /// </summary>
    private async void OnArgs(string[] args)
    {
        if (CommandLine.SendFiles(args) is not { Count: > 0 } files) return;
        _log.Info($"Send To with {files.Count} file(s)");
        if (LastTopic() is not { } topic)
            ShowPublish(files);
        else if (files.Any(Directory.Exists))
            Toasts.Show("Couldn't send: folders can't be sent", "Zip the folder first.");
        else
            ShowResult(await _engine.SendAsync(topic.Id, null, null, files));
    }

    private void ShowResult(SendOutcome r)
    {
        _log.Info($"send {(r.Ok ? "ok" : "failed")}");
        Toasts.Show(r.Text, silent: r.Ok);
    }

    private void ApplyHotKey()
    {
        _hotKey?.Dispose();
        _hotKey = null;
        _hotKeySpec = _engine.Config.Publish.Hotkey;
        if (string.IsNullOrWhiteSpace(_hotKeySpec)) return;
        _hotKey = HotKey.TryRegister(_hotKeySpec, SendClipboard, out var error);
        if (error is null) return;
        _log.Warn("hotkey not registered");
        Toasts.Show("NtfyBar hotkey not available", $"{error}. Pick another one in Settings.", [new("Settings…", SettingsAction)]);
    }

    // ---- Windows ----

    private void ShowPublish(IReadOnlyList<string> files)
    {
        if (_publish is null)
        {
            _publish = new PublishForm(_engine, _formIcon);
            _publish.FormClosed += (_, _) => _publish = null;
        }
        _publish.AddFiles(files);
        Bring(_publish);
    }

    private void ShowSettings(bool firstRun)
    {
        if (_settings is null)
        {
            _settings = new SettingsForm(_engine, _formIcon, _log, firstRun, OnSettingsSaved);
            _settings.FormClosed += (_, _) => _settings = null;
        }
        Bring(_settings);
    }

    private void OnSettingsSaved()
    {
        if (_engine.Config.Publish.Hotkey != _hotKeySpec) ApplyHotKey();
        UpdateStatus();
    }

    private static void Bring(Form form)
    {
        form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    /// <summary>After sleep the old connection is often dead; reconnect now instead of waiting for the 90 s watchdog.</summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // SystemEvents calls back on the subscriber's synchronization context: the UI thread.
        if (e.Mode != PowerModes.Resume) return;
        _log.Info("resumed from sleep: reconnecting");
        _engine.ReconnectAll();
    }

    private static Icon LoadIcon(Size? size)
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("NtfyBar.ico")!;
        return size is { } s ? new Icon(stream, s) : new Icon(stream);
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _quit.Cancel();
        _hotKey?.Dispose();
        _engine.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _log.Info("NtfyBar stopped");
        base.ExitThreadCore();
    }
}
