using Microsoft.Toolkit.Uwp.Notifications;
using NtfyBar.Core;

namespace NtfyBar.App;

/// <summary>
/// The tray app: icon + menu, Core engine, toasts, hotkey, single-instance args.
/// C0 skeleton: subscribes from config.json and shows one toast per live message. The rich
/// behaviour (design §3.2 / §7.1) plugs in at the TODO(UI) seams below.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly ILog _log = new FileLog(FileLog.DefaultDirectory);
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status = new("Starting…") { Enabled = false };
    private readonly Engine _engine;
    private readonly HotKey? _hotKey;
    private readonly CancellationTokenSource _quit = new();

    public TrayContext(string[] args)
    {
        _log.Info($"NtfyBar {Application.ProductVersion} starting");

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        // TODO(UI): Recent (last 20), Send Clipboard, Publish…, Open Downloads, Settings…, Start with Windows (design §7.1 "Tray menu").
        menu.Items.Add("Quit NtfyBar", null, (_, _) => ExitThread());
        _icon = new NotifyIcon { Icon = LoadIcon(), Text = "NtfyBar", ContextMenuStrip = menu, Visible = true };

        Toasts.Init(OnToastActivated, _log);

        _engine = new Engine(new ConfigStore(protector: new DpapiProtector(), log: _log), _log);
        _engine.StatusChanged += (_, _) => UpdateStatus();
        _engine.MessageReceived += OnMessage;
        _engine.MissedSummaryReady += OnMissedSummary;
        if (!_engine.Start())
            Toasts.Show("NtfyBar", "config.json is not valid JSON. Fix it (or delete it) and restart; nothing was overwritten.");
        UpdateStatus();
        // TODO(UI): first run (no topics) → open the Settings form (design §7.1 "first-run wizard").

        _hotKey = HotKey.TryRegister(_engine.Config.Publish.Hotkey, OnHotKey, out var hotKeyError);
        if (hotKeyError is not null)
        {
            _log.Warn("hotkey not registered");
            Toasts.Show("NtfyBar hotkey not available", hotKeyError);
        }

        _ = SingleInstance.ListenAsync(OnArgs, _quit.Token);
        OnArgs(args);
    }

    private void UpdateStatus()
    {
        var text = _engine.OverallStatus?.Describe() ?? "No topics configured";
        _status.Text = text;
        var tip = "NtfyBar — " + text;
        _icon.Text = tip.Length <= 63 ? tip : tip[..63];  // NotifyIcon.Text limit
    }

    private void OnMessage(IncomingMessage m)
    {
        // TODO(UI): add every message to Recent (OwnEcho as read; Missed listed for one-click copy).
        if (m.Handling != Handling.Live) return;
        // TODO(UI): act per design §3.2 by m.Kind: CopyToClipboard (Text; TextAttachment via _engine.DownloadTextAsync),
        // DownloadAttachments + CopyAttachment (File/Image via _engine.DownloadAsync → CF_HDROP / Clipboard.SetImage),
        // ExpiredAttachment → "attachment expired" toast; buttons Copy / Open / Open folder; collapse ≥ 2 files within 5 s.
        if (!m.Topic.Notify) return;
        var body = m.Kind == ClipKind.Text ? m.Message.Message ?? "" : m.Message.Attachment?.Name ?? "attachment";
        Toasts.Show(m.Message.Title ?? m.Topic.DisplayLabel, Publisher.Preview(body));
    }

    private void OnMissedSummary(MissedSummary s)
    {
        if (s.Topic.Notify)
            Toasts.Show("NtfyBar", $"{s.Count} {(s.Count == 1 ? "message" : "messages")} on “{s.Topic.DisplayLabel}” while away");
    }

    private void OnHotKey()
    {
        _log.Info("hotkey pressed");
        // TODO(UI): Send Clipboard to Config.Publish.LastTopicId (files → PublishFilesAsync, image → PublishImageAsync, else text).
        // C0 spike check until then: proves the hotkey and a toast button round trip.
        Toasts.Show("NtfyBar", "Hotkey received. Send Clipboard is not built yet.", new ToastButtonSpec("Test button", "spike"));
    }

    private void OnToastActivated(ToastArguments args)
    {
        args.TryGetValue(Toasts.ActionKey, out var action);
        _log.Info($"toast activated: {action ?? "(body)"}");
        // TODO(UI): dispatch "copy" / "open" / "open-folder" buttons (value = Recent item id or saved path).
        if (action == "spike") Toasts.Show("NtfyBar", "Toast button received.");
    }

    private void OnArgs(string[] args)
    {
        // Toast clicks launch the exe with -ToastActivated; Toasts.Init handles those.
        var files = args.SkipWhile(a => a != "--send").Skip(1).ToList();
        if (files.Count == 0) return;
        _log.Info($"Send To with {files.Count} file(s)");
        // TODO(UI): Explorer Send To → _engine.PublishFilesAsync(lastTopicId, files) + result toast (design §3.1).
        Toasts.Show("NtfyBar", $"Received {files.Count} file(s) from Send To. Sending is not built yet.");
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("NtfyBar.ico")!;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    protected override void ExitThreadCore()
    {
        _quit.Cancel();
        _hotKey?.Dispose();
        _engine.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _log.Info("NtfyBar stopped");
        base.ExitThreadCore();
    }
}
