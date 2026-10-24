namespace NtfyBar.Core;

/// <summary>A received message with everything the UI needs to act on it.</summary>
/// <param name="IsCatchUp">Replayed from the server cache (older than the stream's <c>open</c>); see <see cref="Handling"/> for the decision.</param>
public sealed record IncomingMessage(
    Guid ServerId, TopicConfig Topic, NtfyMessage Message, ClipKind Kind, Handling Handling, bool IsCatchUp, DateTimeOffset ReceivedAt);

/// <summary>"N messages while away" for one topic, raised once a catch-up burst has gone quiet.</summary>
public sealed record MissedSummary(TopicConfig Topic, int Count);

/// <summary>
/// The app's Core facade: owns config, one <see cref="NtfyStream"/> per server, the own-echo guard,
/// publishing and attachment downloads. No UI types. Events run on the
/// <see cref="SynchronizationContext"/> that called <see cref="Start"/> (the UI thread in the app).
/// </summary>
public sealed class Engine : IDisposable
{
    private readonly ConfigStore _store;
    private readonly ILog _log;
    private readonly HttpClient _http;
    private readonly StreamTiming? _timing;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _missedDebounce;
    private readonly EchoGuard _echo;
    private readonly Publisher _publisher;
    private readonly AttachmentDownloader _downloader;
    private readonly Dictionary<Guid, (NtfyStream Stream, string Signature)> _streams = [];
    private readonly Dictionary<Guid, int> _missed = [];
    private Dictionary<Guid, StreamCursor> _cursors = [];
    private readonly object _stateGate = new();
    private CancellationTokenSource? _missedFlush;

    /// <param name="handler">Tests pass a fake; the default never follows redirects and keeps no cookies.</param>
    /// <param name="missedDebounce">Quiet time after the last missed message before <see cref="MissedSummaryReady"/> (default 2 s).</param>
    public Engine(ConfigStore store, ILog? log = null, HttpMessageHandler? handler = null, StreamTiming? timing = null,
        Func<DateTimeOffset>? clock = null, TimeSpan? missedDebounce = null)
    {
        _store = store;
        _log = log ?? NullLog.Instance;
        _timing = timing;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _missedDebounce = missedDebounce ?? TimeSpan.FromSeconds(2);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,  // the stream is long-lived; requests set their own deadlines
        };
        _echo = new EchoGuard(_clock);
        _publisher = new Publisher(_http, _echo, _log);
        _downloader = new AttachmentDownloader(_http, _log);
    }

    /// <summary>Current config. Edit a <see cref="AppConfig.Clone"/> and pass it to <see cref="UpdateConfig"/>.</summary>
    public AppConfig Config { get; private set; } = new();

    /// <summary>Worst status across servers (auth error &gt; redirect &gt; reconnecting &gt; connecting &gt; connected); null = nothing subscribed.</summary>
    public StreamStatus? OverallStatus => _streams.Values.Select(v => v.Stream.Status).OrderByDescending(s => s.State switch
    {
        StreamState.AuthFailed => 5,
        StreamState.Redirected => 4,
        StreamState.Reconnecting => 3,
        StreamState.Connecting => 2,
        StreamState.Stopped => 1,
        _ => 0,
    }).FirstOrDefault();

    public event Action<IncomingMessage>? MessageReceived;
    /// <summary>(server id, new status).</summary>
    public event Action<Guid, StreamStatus>? StatusChanged;
    public event Action<MissedSummary>? MissedSummaryReady;

    /// <summary>Loads config + state and connects. False when config.json is invalid: defaults are used and the file is left untouched.</summary>
    public bool Start()
    {
        var loaded = _store.LoadConfig();
        if (loaded is not null) Config = loaded;
        _cursors = _store.LoadState();
        ApplyStreams();
        return loaded is not null;
    }

    /// <summary>Re-reads config.json. Invalid → keeps the current config and returns false.</summary>
    public bool Reload()
    {
        if (_store.LoadConfig() is not { } loaded) return false;
        Config = loaded;
        ApplyStreams();
        return true;
    }

    /// <summary>
    /// Saves (secrets protected on disk) and applies: streams whose URL, credentials or topics changed
    /// restart, and so do streams stopped by 401/403/3xx (saving Settings = "try again").
    /// </summary>
    public void UpdateConfig(AppConfig config)
    {
        _store.SaveConfig(config);
        Config = config;
        ApplyStreams();
    }

    /// <summary>After wake from sleep or a network change. Streams stopped by 401/403/3xx stay stopped.</summary>
    public void ReconnectAll()
    {
        foreach (var (stream, _) in _streams.Values) stream.ReconnectNow();
    }

    public void Stop()
    {
        foreach (var (stream, _) in _streams.Values) stream.Stop();
        _streams.Clear();
        SaveState();
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }

    // Publishing: the sender tag and default title come from Config.Publish.

    /// <param name="title">null = <see cref="PublishSettings.Title"/>; "" = no title.</param>
    public Task<PublishResult> PublishTextAsync(Guid topicId, string text, string? title = null, IReadOnlyList<string>? tags = null, int priority = 3, CancellationToken ct = default) =>
        Resolve(topicId, title, tags, priority) is { } t
            ? _publisher.PublishTextAsync(t.Server, t.Topic, text, t.Options, ct)
            : Task.FromResult(new PublishResult(PublishStatus.InvalidTarget));

    public Task<IReadOnlyList<PublishResult>> PublishFilesAsync(Guid topicId, IReadOnlyList<string> paths, string? title = null, IReadOnlyList<string>? tags = null, int priority = 3, CancellationToken ct = default) =>
        Resolve(topicId, title, tags, priority) is { } t
            ? _publisher.PublishFilesAsync(t.Server, t.Topic, paths, t.Options, ct)
            : Task.FromResult<IReadOnlyList<PublishResult>>([new PublishResult(PublishStatus.InvalidTarget)]);

    /// <summary>A clipboard image, sent as <c>clip.png</c>.</summary>
    public Task<PublishResult> PublishImageAsync(Guid topicId, byte[] png, string? title = null, IReadOnlyList<string>? tags = null, int priority = 3, CancellationToken ct = default) =>
        Resolve(topicId, title, tags, priority) is { } t
            ? _publisher.PublishBytesAsync(t.Server, t.Topic, png, "clip.png", t.Options, ct)
            : Task.FromResult(new PublishResult(PublishStatus.InvalidTarget));

    // Downloads: credentials only to the message's own server origin.

    /// <summary>File or image attachment into <see cref="AppConfig.DownloadDir"/>.</summary>
    public Task<DownloadResult> DownloadAsync(IncomingMessage m, CancellationToken ct = default) =>
        Attachment(m) is { } a
            ? _downloader.DownloadFileAsync(a, Config.Server(m.ServerId), Config.DownloadDir, ct)
            : Task.FromResult(new DownloadResult(DownloadStatus.Expired));

    /// <summary><see cref="ClipKind.TextAttachment"/> → its text (<see cref="DownloadResult.Text"/>).</summary>
    public Task<DownloadResult> DownloadTextAsync(IncomingMessage m, CancellationToken ct = default) =>
        Attachment(m) is { } a
            ? _downloader.DownloadTextAsync(a, Config.Server(m.ServerId), ct)
            : Task.FromResult(new DownloadResult(DownloadStatus.Expired));

    private static NtfyAttachment? Attachment(IncomingMessage m) =>
        m.Kind == ClipKind.ExpiredAttachment ? null : m.Message.Attachment;

    private (ServerConfig Server, string Topic, PublishOptions Options)? Resolve(Guid topicId, string? title, IReadOnlyList<string>? tags, int priority)
    {
        if (Config.Topic(topicId) is not { } t || Config.Server(t.ServerId) is not { } s) return null;
        return (s, t.Topic, new PublishOptions(Config.Publish.DeviceTag, title ?? Config.Publish.Title, tags, priority));
    }

    private void ApplyStreams()
    {
        var wanted = new Dictionary<Guid, (ServerConfig Server, List<string> Topics)>();
        foreach (var t in Config.Topics)
        {
            if (!t.Enabled || !TopicConfig.IsValidTopicName(t.Topic) || Config.Server(t.ServerId) is not { BaseUri: not null } s) continue;
            if (!wanted.TryGetValue(s.Id, out var w)) wanted[s.Id] = w = (s, []);
            if (!w.Topics.Contains(t.Topic)) w.Topics.Add(t.Topic);
        }
        foreach (var id in _streams.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            _streams[id].Stream.Stop();
            _streams.Remove(id);
        }
        foreach (var (id, w) in wanted)
        {
            var baseUri = w.Server.BaseUri!;
            var signature = string.Join('\n', [baseUri.AbsoluteUri, w.Server.Auth.HeaderValue ?? "", .. w.Topics.Order(StringComparer.Ordinal)]);
            if (_streams.TryGetValue(id, out var old))
            {
                if (old.Signature == signature && !old.Stream.Status.IsTerminal) continue;
                old.Stream.Stop();
            }
            if (!_cursors.TryGetValue(id, out var cursor)) _cursors[id] = cursor = new StreamCursor();
            var stream = new NtfyStream(_http, baseUri, w.Server.Auth.HeaderValue, w.Topics, cursor, _log, _timing);
            stream.StatusChanged += s => StatusChanged?.Invoke(id, s);
            stream.MessageReceived += (m, isCatchUp) => OnMessage(id, m, isCatchUp);
            _streams[id] = (stream, signature);
            stream.Start();
        }
    }

    private void OnMessage(Guid serverId, NtfyMessage m, bool isCatchUp)
    {
        SaveState();  // shortcut: one small file write per message; debounce if catch-ups ever get large
        var topic = Config.Topics.FirstOrDefault(t => t.ServerId == serverId && t.Topic == m.Topic && t.Enabled);
        if (topic is null) return;
        var now = _clock();
        var handling = Clip.Decide(m, isCatchUp, _echo.IsOwnEcho(m, Config.Publish.DeviceTag), topic.ProcessMissed, now);
        MessageReceived?.Invoke(new IncomingMessage(serverId, topic, m, Clip.Classify(m, now), handling, isCatchUp, now));
        if (handling == Handling.Missed) CountMissed(topic);
    }

    private void CountMissed(TopicConfig topic)
    {
        CancellationTokenSource cts;
        lock (_missed)
        {
            _missed[topic.Id] = _missed.GetValueOrDefault(topic.Id) + 1;
            _missedFlush?.Cancel();
            _missedFlush = cts = new CancellationTokenSource();
        }
        _ = FlushMissedAsync(cts.Token);
    }

    private async Task FlushMissedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_missedDebounce, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        List<KeyValuePair<Guid, int>> counts;
        lock (_missed)
        {
            if (ct.IsCancellationRequested) return;
            counts = [.. _missed];
            _missed.Clear();
        }
        foreach (var (topicId, count) in counts)
        {
            if (Config.Topic(topicId) is not { } t) continue;
            try
            {
                MissedSummaryReady?.Invoke(new MissedSummary(t, count));
            }
            catch (Exception e)
            {
                _log.Error($"missed-summary handler threw {e.GetType().Name}");
            }
        }
    }

    private void SaveState()
    {
        lock (_stateGate)
        {
            try
            {
                _store.SaveState(_cursors);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"state.json not saved: {e.GetType().Name}");
            }
        }
    }
}
