using NtfyBar.Core;

namespace NtfyBar.App;

/// <summary>
/// Compose one send to one topic: the message goes first, then each file as its own message.
/// Files come from Add…, drag and drop (onto the window, the list or the message box) or Send To.
/// </summary>
internal sealed class PublishForm : Form
{
    private static readonly string[] Priorities = ["1 · min", "2 · low", "3 · default", "4 · high", "5 · max"];

    private readonly Engine _engine;
    private readonly ComboBox _topic = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DisplayMember = nameof(TopicConfig.DisplayLabel) };
    private readonly TextBox _title = new() { Dock = DockStyle.Fill };
    private readonly TextBox _tags = new() { Dock = DockStyle.Fill, PlaceholderText = "extra tags, comma-separated" };
    private readonly ComboBox _priority = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly TextBox _message = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly ListBox _files = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly Button _send = new() { Text = "Send", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(560, 0) };

    public PublishForm(Engine engine, Icon icon)
    {
        _engine = engine;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Publish — NtfyBar";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 560);
        MinimumSize = new Size(420, 440);
        AllowDrop = true;

        var topics = engine.Config.Topics.Where(t => t.Enabled).ToList();
        _topic.Items.AddRange([.. topics]);
        var last = topics.FirstOrDefault(t => t.Id == engine.Config.Publish.LastTopicId);
        _topic.SelectedItem = last ?? topics.FirstOrDefault();
        _title.Text = engine.Config.Publish.Title;
        _priority.Items.AddRange(Priorities);
        _priority.SelectedIndex = 2;
        _message.AccessibleName = "Message";
        _files.AccessibleName = "Files";

        var add = new Button { Text = "Add…", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        add.Click += (_, _) => Browse();
        remove.Click += (_, _) =>
        {
            foreach (var item in _files.SelectedItems.Cast<string>().ToList()) _files.Items.Remove(item);
        };
        _send.Click += (_, _) => Send();

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(grid, "Topic", _topic);
        AddRow(grid, "Title", _title);
        AddRow(grid, "Tags", _tags);
        AddRow(grid, "Priority", _priority);
        AddRow(grid, "Message", _message, 60);
        AddRow(grid, "Files", _files, 40);
        AddRow(grid, "", Flow(add, remove, new Label { Text = "or drop files here", AutoSize = true, Anchor = AnchorStyles.Left }));
        AddRow(grid, "", Flow(_send, _status));
        Controls.Add(grid);

        foreach (Control c in new Control[] { this, _files, _message })
        {
            c.AllowDrop = true;
            c.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths);
            };
        }

        if (topics.Count == 0) SetStatus("No topics yet: add one in Settings.");
        ResumeLayout();
    }

    /// <summary>Adds files (Send To, drop, Add…); folders are refused, duplicates skipped.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var folders = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) folders++;
            else if (!_files.Items.Contains(path)) _files.Items.Add(path);
        }
        if (folders > 0) SetStatus("Folders can't be sent: zip them first.");
    }

    /// <summary>Enter is a new line in the message; Ctrl+Enter sends.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter))
        {
            Send();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "Add files" };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
    }

    private async void Send()
    {
        if (!_send.Enabled) return;
        if (_topic.SelectedItem is not TopicConfig topic)
        {
            SetStatus("Pick a topic first (add one in Settings).");
            return;
        }
        var files = _files.Items.Cast<string>().ToList();
        if (string.IsNullOrWhiteSpace(_message.Text) && files.Count == 0)
        {
            SetStatus("Type a message or add files.");
            return;
        }
        var tags = _tags.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _send.Enabled = false;
        SetStatus("Sending…");
        var result = await _engine.SendAsync(topic.Id, _message.Text, null, files, _title.Text.Trim(), tags, _priority.SelectedIndex + 1);
        if (IsDisposed) return;
        _send.Enabled = true;
        SetStatus(result.Text);
        if (!result.Ok) return;
        _message.Clear();
        _files.Items.Clear();
    }

    private void SetStatus(string text) => _status.Text = text;

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
}
