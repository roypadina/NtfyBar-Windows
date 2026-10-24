using System.ComponentModel;
using NtfyBar.Core;

namespace NtfyBar.App;

/// <summary>
/// Server, sign-in, topics, publishing defaults, downloads folder, autostart and Send To. Saved
/// secrets are never shown: an empty password/token box keeps the saved one. Edits apply on Save via
/// <see cref="Engine.UpdateConfig"/>. Shortcut: one server in the UI; a hand-edited config with more
/// servers keeps them, new topics go to the first one.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const string SavedSecret = "•••••••• saved, type to replace";
    private static readonly string[] SignIns = ["None", "Username + password", "Access token"];

    private readonly Engine _engine;
    private readonly ILog _log;
    private readonly Action _saved;
    private readonly ServerAuth _savedAuth;
    private readonly TextBox _url = Field();
    private readonly ComboBox _signIn = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly TextBox _user = Field();
    private readonly TextBox _pass = Field(secret: true);
    private readonly TextBox _token = Field(secret: true);
    private readonly Button _test = new() { Text = "Test connection", AutoSize = true };
    private readonly Label _testResult = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly BindingList<TopicConfig> _topics;
    private readonly DataGridView _grid;
    private readonly TextBox _deviceTag = Field();
    private readonly TextBox _title = Field();
    private readonly TextBox _hotkey = Field();
    private readonly TextBox _downloads = Field();
    private readonly CheckBox _autostart = new() { Text = "Start with Windows", AutoSize = true };
    private readonly CheckBox _sendTo = new() { Text = "Add to Explorer's Send To menu", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(600, 0) };

    public SettingsForm(Engine engine, Icon icon, ILog log, bool firstRun, Action saved)
    {
        _engine = engine;
        _log = log;
        _saved = saved;
        var config = engine.Config.Clone();
        var server = config.Servers.FirstOrDefault() ?? new ServerConfig();
        _savedAuth = server.Auth;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Settings — NtfyBar";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 620);
        MinimumSize = new Size(620, 540);

        _url.Text = server.Url;
        _url.PlaceholderText = "https://ntfy.example";
        _signIn.Items.AddRange(SignIns);
        _signIn.SelectedIndex = _savedAuth.Type switch { AuthTypes.Basic => 1, AuthTypes.Token => 2, _ => 0 };
        _signIn.SelectedIndexChanged += (_, _) => UpdateSignInFields();
        _user.Text = _savedAuth.User ?? "";
        _pass.PlaceholderText = string.IsNullOrEmpty(_savedAuth.Pass) ? "" : SavedSecret;
        _token.PlaceholderText = string.IsNullOrEmpty(_savedAuth.Token) ? "" : SavedSecret;
        UpdateSignInFields();
        _test.Click += (_, _) => TestConnection();

        if (firstRun && config.Topics.Count == 0)
            config.Topics.Add(new TopicConfig { Label = "Clipboard", CopyToClipboard = true, DownloadAttachments = true, CopyAttachment = true });
        _topics = new BindingList<TopicConfig>(config.Topics);
        _grid = TopicsGrid(_topics);
        var removeTopic = new Button { Text = "Remove selected topic", AutoSize = true };
        removeTopic.Click += (_, _) =>
        {
            if (_grid.CurrentRow is { IsNewRow: false, DataBoundItem: TopicConfig t }) _topics.Remove(t);
        };

        _deviceTag.Text = config.Publish.DeviceTag;
        _title.Text = config.Publish.Title;
        _hotkey.Text = config.Publish.Hotkey;
        _hotkey.PlaceholderText = "e.g. Ctrl+Alt+Shift+C (Ctrl, Alt, Shift, Win + one key); empty = no hotkey";
        _downloads.Text = config.DownloadDir;
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => BrowseDownloads();
        _autostart.Checked = firstRun || config.LaunchAtLogin;
        _sendTo.Checked = firstRun || Shell.SendToExists;

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();  // a modeless form ignores DialogResult
        CancelButton = cancel;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10), AutoScroll = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (firstRun)
            AddRow(grid, "", Hint("Welcome! Enter your ntfy server, how you sign in and the topic you use as a shared clipboard, then Save."));
        AddRow(grid, "Server URL", _url);
        AddRow(grid, "Sign-in", _signIn);
        AddRow(grid, "Username", _user);
        AddRow(grid, "Password", _pass);
        AddRow(grid, "Access token", _token);
        AddRow(grid, "", Flow(_test, _testResult));
        AddRow(grid, "Topics", _grid, 100);
        AddRow(grid, "", Flow(removeTopic, Hint("Type in the last row to add a topic. Rows without a topic name are not saved.")));
        AddRow(grid, "Device tag", _deviceTag);
        AddRow(grid, "Default title", _title);
        AddRow(grid, "Send hotkey", _hotkey);
        AddRow(grid, "Downloads folder", Pair(_downloads, browse));
        AddRow(grid, "", Flow(_autostart, _sendTo));
        AddRow(grid, "", Flow(save, cancel, _status));
        Controls.Add(grid);
        ResumeLayout();
    }

    private static DataGridView TopicsGrid(BindingList<TopicConfig> topics)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 140),
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EditMode = DataGridViewEditMode.EditOnEnter,
            AccessibleName = "Topics",
        };
        DataGridViewColumn Text(string header, string property, string tip) => new DataGridViewTextBoxColumn
        {
            HeaderText = header, DataPropertyName = property, ToolTipText = tip, FillWeight = 100,
            DefaultCellStyle = { DataSourceNullValue = "" },  // a cleared cell writes "", not DBNull
        };
        DataGridViewColumn Check(string header, string property, string tip) => new DataGridViewCheckBoxColumn
        {
            HeaderText = header, DataPropertyName = property, ToolTipText = tip, AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader,
        };
        grid.Columns.AddRange(
            Text("Label", nameof(TopicConfig.Label), "Your name for the topic, shown in menus and toasts"),
            Text("Topic", nameof(TopicConfig.Topic), "The ntfy topic name: letters, digits, - and _"),
            Check("Enabled", nameof(TopicConfig.Enabled), "Subscribe to this topic"),
            Check("Notify", nameof(TopicConfig.Notify), "Show a toast for each message"),
            Check("Copy text", nameof(TopicConfig.CopyToClipboard), "Put received text on the clipboard"),
            Check("Download files", nameof(TopicConfig.DownloadAttachments), "Save received files and pictures to the downloads folder"),
            Check("Copy files", nameof(TopicConfig.CopyAttachment), "Put downloaded files on the clipboard (pictures as a picture)"),
            Check("Process missed", nameof(TopicConfig.ProcessMissed), "Also copy/download messages that arrived while this PC was off"));
        grid.DataSource = topics;
        // Commit a checkbox click at once, and never show the default error dialog.
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.DataError += (_, e) => e.ThrowException = false;
        return grid;
    }

    private void UpdateSignInFields()
    {
        _user.Enabled = _pass.Enabled = _signIn.SelectedIndex == 1;
        _token.Enabled = _signIn.SelectedIndex == 2;
    }

    /// <summary>The sign-in from the form; an empty secret box keeps the saved secret. Null + <paramref name="error"/> when incomplete.</summary>
    private ServerAuth? SignIn(out string? error)
    {
        error = null;
        switch (_signIn.SelectedIndex)
        {
            case 1:
                var user = _user.Text.Trim();
                var pass = _pass.Text.Length > 0 ? _pass.Text : _savedAuth.Type == AuthTypes.Basic ? _savedAuth.Pass : null;
                if (user.Length > 0 && !string.IsNullOrEmpty(pass)) return ServerAuth.WithBasic(user, pass);
                error = "Enter the username and the password.";
                return null;
            case 2:
                var token = _token.Text.Trim().Length > 0 ? _token.Text.Trim() : _savedAuth.Type == AuthTypes.Token ? _savedAuth.Token : null;
                if (!string.IsNullOrEmpty(token)) return ServerAuth.WithToken(token);
                error = "Enter the access token.";
                return null;
            default:
                return new ServerAuth();
        }
    }

    private async void TestConnection()
    {
        if (SignIn(out var error) is not { } auth)
        {
            _testResult.Text = error;
            return;
        }
        _grid.EndEdit();
        var topics = _topics.Where(t => t.Enabled).Select(t => (t.Topic ?? "").Trim()).ToList();
        _test.Enabled = false;
        _testResult.Text = "Testing…";
        var result = await _engine.TestConnectionAsync(new ServerConfig { Url = _url.Text.Trim(), Auth = auth }, topics);
        if (IsDisposed) return;
        _test.Enabled = true;
        _testResult.Text = result.Describe();
    }

    /// <summary>The edited config on top of a fresh copy of the current one (keeps e.g. the last publish topic); null + <paramref name="error"/> when invalid.</summary>
    private AppConfig? Build(out string? error)
    {
        _grid.EndEdit();
        var url = _url.Text.Trim();
        if (ServerConfig.ValidatedUri(url) is null)
        {
            error = "The server URL is not valid (example: https://ntfy.example).";
            return null;
        }
        if (SignIn(out error) is not { } auth) return null;
        var topics = _topics.Where(t => !string.IsNullOrWhiteSpace(t.Topic)).ToList();
        foreach (var t in topics)
        {
            t.Topic = t.Topic.Trim();
            t.Label = (t.Label ?? "").Trim();
            if (TopicConfig.IsValidTopicName(t.Topic)) continue;
            error = $"Topic “{t.Topic}” is not valid: use letters, digits, - and _ only (up to 64).";
            return null;
        }
        var deviceTag = _deviceTag.Text.Trim();
        if (deviceTag.Length == 0 || deviceTag.Contains(','))
        {
            error = "The device tag must be one word without commas (default: ntfybar-win).";
            return null;
        }
        var hotkey = _hotkey.Text.Trim();
        if (hotkey.Length > 0 && !HotkeySpec.TryParse(hotkey, out _, out _))
        {
            error = $"“{hotkey}” is not a valid hotkey (example: Ctrl+Alt+Shift+C).";
            return null;
        }
        var downloads = _downloads.Text.Trim();
        if (!Path.IsPathFullyQualified(downloads))
        {
            error = @"The downloads folder must be a full path (example: C:\Users\you\Downloads\NtfyBar).";
            return null;
        }

        var c = _engine.Config.Clone();
        if (c.Servers.Count == 0) c.Servers.Add(new ServerConfig());
        var server = c.Servers[0];
        server.Url = url;
        server.Auth = auth;
        foreach (var t in topics.Where(t => c.Server(t.ServerId) is null)) t.ServerId = server.Id;
        c.Topics = topics;
        c.Publish.DeviceTag = deviceTag;
        c.Publish.Title = _title.Text.Trim();
        c.Publish.Hotkey = hotkey;
        c.DownloadDir = downloads;
        c.LaunchAtLogin = _autostart.Checked;
        return c;
    }

    private void Save()
    {
        if (Build(out var error) is not { } config)
        {
            _status.Text = error;
            return;
        }
        try
        {
            _engine.UpdateConfig(config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Error($"settings not saved: {e.GetType().Name}");
            _status.Text = $"Couldn't save the settings: {e.Message}";
            return;
        }
        var failed = new List<string>();
        Apply("Start with Windows", () => Shell.SetAutostart(config.LaunchAtLogin), failed);
        Apply("Send To", () => Shell.SetSendTo(_sendTo.Checked), failed);
        _saved();
        if (failed.Count > 0)
        {
            _status.Text = $"Saved, but {string.Join(" and ", failed)} could not be changed (see the log).";
            return;
        }
        Close();
    }

    private void Apply(string what, Action action, List<string> failed)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            // Registry or shortcut trouble (policy, missing WScript.Shell …) must not lose the saved settings.
            _log.Error($"{what} not changed: {e.GetType().Name}");
            failed.Add(what);
        }
    }

    private void BrowseDownloads()
    {
        using var dialog = new FolderBrowserDialog { Description = "Folder for received files", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(_downloads.Text)) dialog.SelectedPath = _downloads.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _downloads.Text = dialog.SelectedPath;
    }

    private static TextBox Field(bool secret = false) => new() { Dock = DockStyle.Fill, UseSystemPasswordChar = secret };

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(640, 0) };

    private static void AddRow(TableLayoutPanel grid, string label, Control control, float percent = 0)
    {
        grid.RowStyles.Add(percent > 0 ? new RowStyle(SizeType.Percent, percent) : new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 7, 8, 3) });
        grid.Controls.Add(control);
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        flow.Controls.AddRange(controls);
        return flow;
    }

    private static TableLayoutPanel Pair(Control fill, Control right)
    {
        var pair = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pair.Controls.Add(fill);
        pair.Controls.Add(right);
        return pair;
    }
}
