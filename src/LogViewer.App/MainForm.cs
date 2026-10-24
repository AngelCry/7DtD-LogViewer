using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using LogViewer.Core;

namespace LogViewer.App;

internal sealed class MainForm : Form
{
    private const int LargeCopyThreshold = 10000;
    private const int BackgroundCopyThreshold = 1000;
    private const int MaxSingleReadBytes = 64 * 1024 * 1024;

    /// <summary>Единая таблица цветов подсветки уровней.</summary>
    private static readonly Dictionary<LogLevel, (Color Back, Color Fore)> LevelColors = new()
    {
        [LogLevel.Info] = (Color.Empty, SystemColors.WindowText),
        [LogLevel.Warning] = (Color.FromArgb(0xFF, 0xF5, 0x9D), SystemColors.WindowText),
        [LogLevel.Error] = (Color.FromArgb(0xFF, 0xB7, 0x4D), SystemColors.WindowText),
        [LogLevel.Exception] = (Color.FromArgb(0xD3, 0x2F, 0x2F), Color.White),
        [LogLevel.Other] = (Color.Empty, SystemColors.WindowText),
    };

    private readonly BufferedListView _listView;
    private readonly SplitContainer _splitContainer;
    private readonly TextBox _detailsBox;
    private readonly Button _openButton;
    private readonly Button _cancelButton;
    private readonly ProgressBar _progressBar;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly CheckBox _infoCheck;
    private readonly CheckBox _warningCheck;
    private readonly CheckBox _errorCheck;
    private readonly CheckBox _exceptionCheck;
    private readonly CheckBox _otherCheck;
    private readonly TextBox _textFilterBox;
    private readonly CheckBox _regexCheck;
    private readonly CheckBox _tailCheck;
    private readonly CheckBox _followCheck;
    private readonly ToolTip _filterToolTip;
    private readonly System.Windows.Forms.Timer _filterDebounceTimer;
    private readonly ToolStripStatusLabel _excludesLabel;
    private readonly List<string> _excludes = new();

    private const int ExcludeMaxLength = 100;

    private LogSession? _session;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _filterCts;
    private FilterResult? _filterResult;
    private int _filterVersion;
    private bool _suppressFilterEvents;
    private int _detailsEntryIndex = -1;
    private int _lastGeneration;
    private DateTime _lastSizeUpdateTick = DateTime.MinValue;

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "7DTDLogViewer");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private sealed class ViewerSettings
    {
        public int[]? ColumnWidths { get; set; }

        public string[]? Excludes { get; set; }
    }

    public MainForm()
    {
        Text = "7 Days to Die Log Viewer";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 700);

        _listView = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            VirtualMode = true,
            HideSelection = false,
            MultiSelect = true,
        };
        _listView.Columns.Add("Time", 220, HorizontalAlignment.Right);
        _listView.Columns.Add("Level", 50, HorizontalAlignment.Left);
        _listView.Columns.Add("Component", 200, HorizontalAlignment.Left);
        _listView.Columns.Add("Message", 600, HorizontalAlignment.Left);
        _listView.RetrieveVirtualItem += ListView_RetrieveVirtualItem;
        _listView.CacheVirtualItems += ListView_CacheVirtualItems;
        _listView.ItemSelectionChanged += ListView_ItemSelectionChanged;
        _listView.KeyDown += ListView_KeyDown;
        _listView.MouseWheel += ListView_MouseWheel;
        _listView.MouseDown += ListView_MouseDown;

        _infoCheck = CreateLevelCheckBox("INF");
        _warningCheck = CreateLevelCheckBox("WRN");
        _errorCheck = CreateLevelCheckBox("ERR");
        _exceptionCheck = CreateLevelCheckBox("EXC");
        _otherCheck = CreateLevelCheckBox("Other");

        _textFilterBox = new TextBox
        {
            Width = 220,
            PlaceholderText = "Text filter",
            AccessibleName = "Text filter",
            Margin = new Padding(8, 1, 8, 0),
        };
        _textFilterBox.TextChanged += FilterInputChanged;

        _regexCheck = new CheckBox
        {
            Text = "Regex",
            AutoSize = true,
            Margin = new Padding(0, 3, 12, 0),
        };
        _regexCheck.CheckedChanged += FilterInputChanged;

        _tailCheck = new CheckBox
        {
            Text = "Tail",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 3, 12, 0),
        };

        _followCheck = new CheckBox
        {
            Text = "Follow",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 0),
        };

        _filterToolTip = new ToolTip();

        var filterPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(6, 7, 6, 5),
            WrapContents = false,
            AutoSize = false,
        };
        filterPanel.Controls.Add(_infoCheck);
        filterPanel.Controls.Add(_warningCheck);
        filterPanel.Controls.Add(_errorCheck);
        filterPanel.Controls.Add(_exceptionCheck);
        filterPanel.Controls.Add(_otherCheck);
        filterPanel.Controls.Add(_textFilterBox);
        filterPanel.Controls.Add(_regexCheck);
        filterPanel.Controls.Add(_tailCheck);
        filterPanel.Controls.Add(_followCheck);

        _detailsBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 9F),
            BackColor = SystemColors.Window,
            ForeColor = SystemColors.WindowText,
            BorderStyle = BorderStyle.None,
        };

        _splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            Panel1MinSize = 120,
            Panel2MinSize = 80,
            SplitterWidth = 5,
        };
        _splitContainer.Panel1.Controls.Add(_listView);
        _splitContainer.Panel1.Controls.Add(filterPanel);
        _splitContainer.Panel2.Controls.Add(_detailsBox);

        _openButton = new Button
        {
            Text = "Open…",
            AutoSize = true,
            Margin = new Padding(0, 0, 6, 0),
        };
        _openButton.Click += OpenButton_Click;

        _cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            Enabled = false,
            Margin = new Padding(0, 0, 0, 0),
        };
        _cancelButton.Click += CancelButton_Click;

        _progressBar = new ProgressBar
        {
            Width = 300,
            Height = 23,
            Margin = new Padding(12, 0, 0, 0),
        };

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(6, 8, 6, 6),
            WrapContents = false,
            AutoSize = false,
        };
        topPanel.Controls.Add(_openButton);
        topPanel.Controls.Add(_cancelButton);
        topPanel.Controls.Add(_progressBar);

        _statusLabel = new ToolStripStatusLabel("No file opened")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _excludesLabel = new ToolStripStatusLabel(string.Empty)
        {
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);
        statusStrip.Items.Add(_excludesLabel);
        Controls.Add(_splitContainer);
        Controls.Add(statusStrip);
        Controls.Add(topPanel);

        var contextMenu = new ContextMenuStrip();
        var excludeMessageItem = new ToolStripMenuItem("Exclude this message");
        excludeMessageItem.Click += ExcludeMessage_Click;
        var excludeComponentItem = new ToolStripMenuItem("Exclude this component");
        excludeComponentItem.Click += ExcludeComponent_Click;
        var clearExcludesItem = new ToolStripMenuItem("Clear all excludes");
        clearExcludesItem.Click += ClearExcludes_Click;
        contextMenu.Items.AddRange(new ToolStripItem[]
        {
            excludeMessageItem,
            excludeComponentItem,
            new ToolStripSeparator(),
            clearExcludesItem,
        });
        _listView.ContextMenuStrip = contextMenu;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _refreshTimer.Tick += RefreshTimer_Tick;

        _filterDebounceTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _filterDebounceTimer.Tick += FilterDebounceTimer_Tick;

        SetIndexingState(false);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        int available = _splitContainer.Height;
        int distance = available * 2 / 3;
        int maxDistance = available - _splitContainer.Panel2MinSize - _splitContainer.SplitterWidth;
        if (distance > maxDistance)
        {
            distance = maxDistance;
        }

        if (distance >= _splitContainer.Panel1MinSize)
        {
            _splitContainer.SplitterDistance = distance;
        }

        LoadColumnWidths();
    }

    private void LoadColumnWidths()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return;
            }

            string json = File.ReadAllText(SettingsFile);
            var settings = JsonSerializer.Deserialize<ViewerSettings>(json);

            int[]? widths = settings?.ColumnWidths;
            if (widths is not null && widths.Length == _listView.Columns.Count)
            {
                for (int i = 0; i < widths.Length; i++)
                {
                    if (widths[i] > 0)
                    {
                        _listView.Columns[i].Width = widths[i];
                    }
                }
            }

            if (settings?.Excludes is { Length: > 0 } excludes)
            {
                _excludes.Clear();
                _excludes.AddRange(excludes);
                UpdateExcludesIndicator();
            }
        }
        catch
        {
            // Ошибки чтения настроек не должны ронять приложение.
        }
    }

    private void SaveColumnWidths()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);

            var settings = new ViewerSettings
            {
                ColumnWidths = new int[_listView.Columns.Count],
                Excludes = _excludes.ToArray(),
            };
            for (int i = 0; i < _listView.Columns.Count; i++)
            {
                settings.ColumnWidths[i] = _listView.Columns[i].Width;
            }

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Ошибки записи настроек не должны ронять приложение.
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C) && _listView.Focused)
        {
            CopySelectedEntriesAsync();
            return true;
        }

        // Отслеживаем навигацию вверх/вниз для Follow
        if (_followCheck.Checked && (keyData is Keys.Up || keyData is Keys.PageUp || keyData is Keys.Home))
        {
            _followCheck.Checked = false;
        }
        else if (keyData == Keys.End)
        {
            _followCheck.Checked = true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ListView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_followCheck.Checked && (e.KeyCode is Keys.Up || e.KeyCode is Keys.PageUp || e.KeyCode is Keys.Home))
        {
            _followCheck.Checked = false;
        }
        else if (e.KeyCode == Keys.End)
        {
            _followCheck.Checked = true;
        }
    }

    private void ListView_MouseWheel(object? sender, MouseEventArgs e)
    {
        if (_followCheck.Checked && e.Delta > 0)
        {
            // Прокрутка вверх колесиком мыши выключает Follow
            _followCheck.Checked = false;
        }
    }

    private void ListView_MouseDown(object? sender, MouseEventArgs e)
    {
        var hit = _listView.HitTest(e.Location);
        if (_followCheck.Checked && hit.Item is not null)
        {
            int visibleCount = _listView.VirtualListSize;
            if (visibleCount > 0 && hit.Item.Index < visibleCount - 1)
            {
                _followCheck.Checked = false;
            }
        }
    }

    private async void OpenButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open log file",
            Filter = "Log files (*.log)|*.log|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        DisposeSession();

        LogSession session;
        try
        {
            session = LogSession.Open(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError("Open failed", ex);
            return;
        }

        _session = session;
        _lastGeneration = session.Generation;
        _detailsEntryIndex = -1;
        _detailsBox.Clear();
        _listView.VirtualListSize = 0;
        _progressBar.Value = 0;
        _statusLabel.Text = "Indexing…";
        SetIndexingState(true);
        _refreshTimer.Start();

        var cts = new CancellationTokenSource();
        _cts = cts;
        var progress = new Progress<int>(percent =>
        {
            if (!IsDisposed && !_progressBar.IsDisposed)
            {
                _progressBar.Value = Math.Clamp(percent, 0, 100);
            }
        });

        bool completed = false;
        try
        {
            await session.IndexAsync(progress, cts.Token);
            completed = true;

            if (ReferenceEquals(_session, session))
            {
                _progressBar.Value = 100;
                string seconds = session.IndexTime.TotalSeconds
                    .ToString("F1", CultureInfo.InvariantCulture);
                _statusLabel.Text =
                    $"Entries: {session.Index.Count}, Index built in {seconds} s";
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_session, session))
            {
                _statusLabel.Text = $"Cancelled, {session.Index.Count} entries";
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_session, session))
            {
                _statusLabel.Text = $"Indexing failed: {ex.Message}";
                ShowError("Indexing failed", ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_session, session))
            {
                UpdateVirtualListSizeForce();
                SetIndexingState(false);

                if (completed && !BuildFilter().IsEmpty)
                {
                    _ = ApplyFilterAsync();
                }
            }

            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelButton_Click(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _statusLabel.Text = "Cancelling…";
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        LogSession? session = _session;
        if (session is null || IsDisposed)
        {
            return;
        }

        // Если идёт первоначальная индексация (когда _cts активен и не пуст), таймер просто обновляет размер списка
        if (_cts is not null)
        {
            UpdateVirtualListSize();
            return;
        }

        if (!_tailCheck.Checked)
        {
            return;
        }

        PollResult pollResult;
        try
        {
            pollResult = await session.PollAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            return;
        }

        if (!ReferenceEquals(_session, session) || IsDisposed)
        {
            return;
        }

        int currentGen = session.Generation;
        if (currentGen != _lastGeneration)
        {
            _lastGeneration = currentGen;
            _filterResult = null;
            _statusLabel.Text = "Log was truncated or rotated, reindexed";
            UpdateVirtualListSizeForce();

            if (!BuildFilter().IsEmpty)
            {
                _ = ApplyFilterAsync();
            }

            return;
        }

        if (pollResult.Kind == PollKind.Grown)
        {
            int oldCount = pollResult.OldCount;
            int newCount = pollResult.NewCount;

            // Если есть активный фильтр, делаем ExtendAsync
            if (_filterResult is not null && !_filterResult.Filter.IsEmpty)
            {
                try
                {
                    _filterResult = await FilterEngine.ExtendAsync(
                        session.File, session.Index, _filterResult, null, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Ошибки инкрементального фильтра не должны ронять таймер
                }
            }

            // Автопрокрутка (Follow)
            if (_followCheck.Checked && newCount > oldCount)
            {
                UpdateVirtualListSizeForce();
                int visibleCount = _filterResult?.Visible?.Count ?? newCount;
                if (visibleCount > 0)
                {
                    _listView.EnsureVisible(visibleCount - 1);
                    _listView.SelectedIndices.Clear();
                    _listView.SelectedIndices.Add(visibleCount - 1);
                }
            }
            else
            {
                UpdateVirtualListSize();
            }
        }
        else
        {
            UpdateVirtualListSize();
        }
    }

    private void ListView_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        LogSession? session = _session;
        int entryIndex = session is null || e.ItemIndex < 0
            ? -1
            : MapToEntryIndex(e.ItemIndex);

        if (session is null || entryIndex < 0 || entryIndex >= session.Index.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        EntryPreview preview = session.Reader.GetPreview(entryIndex);
        var item = new ListViewItem(preview.Timestamp);
        item.SubItems.Add(preview.Level);
        item.SubItems.Add(preview.Component);

        string message = preview.Message;
        if (preview.LineCount > 1)
        {
            message += preview.Truncated
                ? $" [+{preview.LineCount - 1} lines…]"
                : $" [+{preview.LineCount - 1} lines]";
        }
        item.SubItems.Add(message);

        ApplyLevelColors(item, session.Index.Level(entryIndex));
        e.Item = item;
    }

    private void ListView_CacheVirtualItems(object? sender, CacheVirtualItemsEventArgs e)
    {
        LogSession? session = _session;
        if (session is null)
        {
            return;
        }

        int count = _filterResult?.Visible?.Count ?? session.Index.Count;
        int end = Math.Min(e.EndIndex, count - 1);
        try
        {
            for (int i = Math.Max(e.StartIndex, 0); i <= end; i++)
            {
                int entryIndex = MapToEntryIndex(i);
                if (entryIndex >= 0)
                {
                    session.Reader.GetPreview(entryIndex);
                }
            }
        }
        catch (Exception)
        {
            // Прогрев кэша — оптимизация.
        }
    }

    private static void ApplyLevelColors(ListViewItem item, LogLevel level)
    {
        if (!LevelColors.TryGetValue(level, out (Color Back, Color Fore) colors))
        {
            colors = (Color.Empty, SystemColors.WindowText);
        }

        if (colors.Back != Color.Empty)
        {
            item.BackColor = colors.Back;
        }

        item.ForeColor = colors.Fore;
    }

    private void ListView_ItemSelectionChanged(object? sender, ListViewItemSelectionChangedEventArgs e)
    {
        if (_followCheck.Checked && e.IsSelected)
        {
            int visibleCount = _listView.VirtualListSize;
            if (visibleCount > 0 && e.ItemIndex < visibleCount - 1)
            {
                _followCheck.Checked = false;
            }
        }

        int entryIndex = _listView.SelectedIndices.Count > 0
            ? MapToEntryIndex(_listView.SelectedIndices[0])
            : -1;

        if (entryIndex == _detailsEntryIndex)
        {
            return;
        }

        _detailsEntryIndex = entryIndex;
        ShowDetails(entryIndex);
    }

    private void ShowDetails(int entryIndex)
    {
        if (entryIndex < 0 || _session is null)
        {
            _detailsBox.Clear();
            return;
        }

        try
        {
            _detailsBox.Text = _session.Reader.GetFullText(entryIndex);
        }
        catch (Exception ex)
        {
            _detailsBox.Text = $"<read error: {ex.Message}>";
        }

        _detailsBox.SelectionStart = 0;
        _detailsBox.SelectionLength = 0;
    }

    private int MapToEntryIndex(int viewIndex)
    {
        VisibleList? visible = _filterResult?.Visible;
        if (visible is null)
        {
            return viewIndex;
        }

        return (uint)viewIndex < (uint)visible.Count ? visible[viewIndex] : -1;
    }

    private async void CopySelectedEntriesAsync()
    {
        LogSession? session = _session;
        if (session is null || _listView.SelectedIndices.Count == 0)
        {
            return;
        }

        var entries = new int[_listView.SelectedIndices.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = MapToEntryIndex(_listView.SelectedIndices[i]);
        }

        int count = entries.Length;
        bool contiguous = IsContiguous(entries);

        if (count > LargeCopyThreshold)
        {
            DialogResult answer = MessageBox.Show(
                this,
                $"Скопировать {count} записей?",
                "Копирование",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);

            if (answer != DialogResult.OK)
            {
                return;
            }
        }

        bool runInBackground = count > LargeCopyThreshold
            || (!contiguous && count > BackgroundCopyThreshold);

        try
        {
            string text;
            if (runInBackground)
            {
                _statusLabel.Text = $"Copying {count} entries…";
                text = await Task.Run(() => BuildCopyText(session, entries, contiguous));
            }
            else
            {
                text = BuildCopyText(session, entries, contiguous);
            }

            if (text.Length > 0)
            {
                Clipboard.SetText(text);
            }

            _statusLabel.Text = $"Copied {count} entries";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Copy failed: {ex.Message}";
            ShowError("Copy failed", ex);
        }
    }

    private static bool IsContiguous(int[] entries)
    {
        for (int i = 1; i < entries.Length; i++)
        {
            if (entries[i] != entries[i - 1] + 1)
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildCopyText(LogSession session, int[] entries, bool contiguous)
    {
        if (contiguous)
        {
            long start = session.Index.Offset(entries[0]);
            long end = session.Index.End(entries[^1]);
            return ReadRangeAsText(session.File, start, end);
        }

        var builder = new StringBuilder();
        for (int i = 0; i < entries.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(session.Reader.GetFullText(entries[i]));
        }

        return builder.ToString();
    }

    private static string ReadRangeAsText(LogFile file, long start, long end)
    {
        long length = end - start;
        if (length <= 0)
        {
            return string.Empty;
        }

        if (length <= MaxSingleReadBytes)
        {
            byte[] buffer = new byte[length];
            int read = file.Read(start, buffer);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        var builder = new StringBuilder();
        byte[] chunk = new byte[1 << 20];
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(chunk.Length)];
        var decoder = Encoding.UTF8.GetDecoder();

        long position = start;
        while (position < end)
        {
            int toRead = (int)Math.Min(chunk.Length, end - position);
            int read = file.Read(position, chunk.AsSpan(0, toRead));
            if (read <= 0)
            {
                break;
            }

            int charCount = decoder.GetChars(chunk, 0, read, chars, 0, flush: false);
            builder.Append(chars, 0, charCount);
            position += read;
        }

        return builder.ToString();
    }

    private void UpdateVirtualListSize()
    {
        // Обновлять пачкой не чаще раза в 250 мс
        if ((DateTime.UtcNow - _lastSizeUpdateTick).TotalMilliseconds < 250)
        {
            return;
        }

        UpdateVirtualListSizeForce();
    }

    private void UpdateVirtualListSizeForce()
    {
        _lastSizeUpdateTick = DateTime.UtcNow;
        LogSession? session = _session;
        if (session is null)
        {
            return;
        }

        int count = _filterResult?.Visible?.Count ?? session.Index.Count;
        if (_listView.VirtualListSize != count)
        {
            // Защита от уменьшения списка (если список уменьшился, проверяем SelectedIndices)
            if (_listView.SelectedIndices.Count > 0)
            {
                int maxSelected = _listView.SelectedIndices[^1];
                if (maxSelected >= count)
                {
                    _listView.SelectedIndices.Clear();
                    if (_followCheck.Checked && count > 0)
                    {
                        _listView.SelectedIndices.Add(count - 1);
                    }
                }
            }

            _listView.VirtualListSize = count;
        }
    }

    private CheckBox CreateLevelCheckBox(string text)
    {
        var checkBox = new CheckBox
        {
            Text = text,
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 3, 12, 0),
        };
        checkBox.CheckedChanged += FilterInputChanged;
        return checkBox;
    }

    private void FilterInputChanged(object? sender, EventArgs e)
    {
        if (_suppressFilterEvents || _session is null)
        {
            return;
        }

        _filterVersion++;
        _filterCts?.Cancel();
        _filterCts = null;

        ClearFilterError();
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private void FilterDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _filterDebounceTimer.Stop();
        _ = ApplyFilterAsync();
    }

    private async Task ApplyFilterAsync()
    {
        LogSession? session = _session;
        if (session is null || IsDisposed)
        {
            return;
        }

        LogFilter filter = BuildFilter();
        if (!FilterEngine.TryValidate(filter, out string? error))
        {
            _filterVersion++;
            _filterCts?.Cancel();
            _filterCts = null;
            SetFilterError(error ?? "Invalid regular expression.");
            return;
        }

        ClearFilterError();

        int version = ++_filterVersion;
        var cts = new CancellationTokenSource();
        _filterCts?.Cancel();
        _filterCts = cts;

        int selectedEntry = GetSelectedEntryIndex();
        _statusLabel.Text = "Filtering…";

        var progress = new Progress<int>(percent =>
        {
            if (!IsDisposed && version == _filterVersion)
            {
                _statusLabel.Text = $"Filtering… {Math.Clamp(percent, 0, 100)}%";
            }
        });

        FilterResult? result = null;
        try
        {
            result = await FilterEngine.RunAsync(
                session.File,
                session.Index,
                filter,
                progress,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed && version == _filterVersion && ReferenceEquals(session, _session))
            {
                _statusLabel.Text = $"Filter failed: {ex.Message}";
                ShowError("Filter failed", ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_filterCts, cts))
            {
                _filterCts = null;
            }

            cts.Dispose();
        }

        if (result is null
            || IsDisposed
            || version != _filterVersion
            || !ReferenceEquals(session, _session))
        {
            return;
        }

        ApplyFilterResult(session, result, selectedEntry);
    }

    private void ApplyFilterResult(LogSession session, FilterResult result, int selectedEntry)
    {
        _filterResult = result;
        int visibleCount = result.Visible?.Count ?? session.Index.Count;
        _listView.VirtualListSize = visibleCount;
        _statusLabel.Text = $"Shown: {visibleCount} of {session.Index.Count}";

        int viewIndex = -1;
        if (selectedEntry >= 0 && visibleCount > 0)
        {
            viewIndex = result.Visible?.IndexOfOrNearest(selectedEntry) ?? selectedEntry;
            if (viewIndex >= visibleCount)
            {
                viewIndex = visibleCount - 1;
            }
        }

        int entryIndex = viewIndex >= 0 ? MapToEntryIndex(viewIndex) : -1;
        if (entryIndex < 0)
        {
            _detailsEntryIndex = -1;
            _listView.SelectedIndices.Clear();
            _detailsBox.Clear();
            return;
        }

        _listView.SelectedIndices.Clear();
        _detailsEntryIndex = entryIndex;
        _listView.SelectedIndices.Add(viewIndex);
        _listView.EnsureVisible(viewIndex);
        ShowDetails(entryIndex);
    }

    private LogFilter BuildFilter()
    {
        LogLevelMask levels = LogLevelMask.None;
        if (_infoCheck.Checked)
        {
            levels |= LogLevelMask.Info;
        }

        if (_warningCheck.Checked)
        {
            levels |= LogLevelMask.Warning;
        }

        if (_errorCheck.Checked)
        {
            levels |= LogLevelMask.Error;
        }

        if (_exceptionCheck.Checked)
        {
            levels |= LogLevelMask.Exception;
        }

        if (_otherCheck.Checked)
        {
            levels |= LogLevelMask.Other;
        }

        return new LogFilter(levels, _textFilterBox.Text, _regexCheck.Checked, _excludes.ToArray());
    }

    private void ExcludeMessage_Click(object? sender, EventArgs e)
    {
        LogSession? session = _session;
        if (session is null || _listView.SelectedIndices.Count == 0)
        {
            return;
        }

        int entryIndex = MapToEntryIndex(_listView.SelectedIndices[0]);
        if (entryIndex < 0)
        {
            return;
        }

        EntryPreview preview = session.Reader.GetPreview(entryIndex);
        AddExclude(preview.Message);
    }

    private void ExcludeComponent_Click(object? sender, EventArgs e)
    {
        LogSession? session = _session;
        if (session is null || _listView.SelectedIndices.Count == 0)
        {
            return;
        }

        int entryIndex = MapToEntryIndex(_listView.SelectedIndices[0]);
        if (entryIndex < 0)
        {
            return;
        }

        EntryPreview preview = session.Reader.GetPreview(entryIndex);
        if (string.IsNullOrEmpty(preview.Component))
        {
            return;
        }

        AddExclude(preview.Component);
    }

    private void AddExclude(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string exclude = value.Length > ExcludeMaxLength ? value[..ExcludeMaxLength] : value;
        if (_excludes.Contains(exclude, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _excludes.Add(exclude);
        UpdateExcludesIndicator();
        _ = ApplyFilterAsync();
    }

    private void ClearExcludes_Click(object? sender, EventArgs e)
    {
        if (_excludes.Count == 0)
        {
            return;
        }

        _excludes.Clear();
        UpdateExcludesIndicator();
        _ = ApplyFilterAsync();
    }

    private void UpdateExcludesIndicator()
    {
        _excludesLabel.Text = _excludes.Count > 0 ? $"[Excludes: {_excludes.Count}]" : string.Empty;
    }

    private int GetSelectedEntryIndex()
    {
        return _listView.SelectedIndices.Count > 0
            ? MapToEntryIndex(_listView.SelectedIndices[0])
            : -1;
    }

    private void SetFilterError(string error)
    {
        _textFilterBox.BackColor = Color.FromArgb(0xFF, 0xCD, 0xD2);
        _filterToolTip.SetToolTip(_textFilterBox, error);
        _statusLabel.Text = "Invalid regex";
    }

    private void ClearFilterError()
    {
        if (_textFilterBox.BackColor != SystemColors.Window)
        {
            _textFilterBox.BackColor = SystemColors.Window;
        }

        _filterToolTip.SetToolTip(_textFilterBox, string.Empty);
    }

    private void ResetFilterControls()
    {
        _suppressFilterEvents = true;
        try
        {
            _infoCheck.Checked = true;
            _warningCheck.Checked = true;
            _errorCheck.Checked = true;
            _exceptionCheck.Checked = true;
            _otherCheck.Checked = true;
            _textFilterBox.Text = string.Empty;
            _regexCheck.Checked = false;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        ClearFilterError();
    }

    private void SetIndexingState(bool indexing)
    {
        _openButton.Enabled = !indexing;
        _cancelButton.Enabled = indexing;
    }

    private void DisposeSession()
    {
        _refreshTimer.Stop();
        _filterDebounceTimer.Stop();

        _filterVersion++;
        _filterCts?.Cancel();
        _filterCts = null;
        _filterResult = null;
        ResetFilterControls();

        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        LogSession? session = _session;
        _session = null;
        _detailsEntryIndex = -1;
        _detailsBox.Clear();
        _listView.VirtualListSize = 0;
        session?.Dispose();
        cts?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _filterDebounceTimer.Stop();
        SaveColumnWidths();
        DisposeSession();
        _refreshTimer.Dispose();
        _filterDebounceTimer.Dispose();
        _filterToolTip.Dispose();
        base.OnFormClosed(e);
    }

    private void ShowError(string title, Exception exception)
    {
        MessageBox.Show(
            this,
            exception.Message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
