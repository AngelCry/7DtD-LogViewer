using System.Globalization;
using System.Text;
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
    private readonly ToolTip _filterToolTip;
    private readonly System.Windows.Forms.Timer _filterDebounceTimer;

    private LogSession? _session;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _filterCts;
    private FilterResult? _filterResult;
    private int _filterVersion;
    private bool _suppressFilterEvents;
    private int _detailsEntryIndex = -1;

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
        _listView.Columns.Add("Entry", 1000, HorizontalAlignment.Left);
        _listView.RetrieveVirtualItem += ListView_RetrieveVirtualItem;
        _listView.CacheVirtualItems += ListView_CacheVirtualItems;
        _listView.ItemSelectionChanged += ListView_ItemSelectionChanged;

        _infoCheck = CreateLevelCheckBox("INF");
        _warningCheck = CreateLevelCheckBox("WRN");
        _errorCheck = CreateLevelCheckBox("ERR");
        _exceptionCheck = CreateLevelCheckBox("EXC");
        _otherCheck = CreateLevelCheckBox("Other");

        _textFilterBox = new TextBox
        {
            Width = 260,
            PlaceholderText = "Text filter",
            AccessibleName = "Text filter",
            Margin = new Padding(8, 1, 12, 0),
        };
        _textFilterBox.TextChanged += FilterInputChanged;

        _regexCheck = new CheckBox
        {
            Text = "Regex",
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 0),
        };
        _regexCheck.CheckedChanged += FilterInputChanged;

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
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);

        Controls.Add(_splitContainer);
        Controls.Add(statusStrip);
        Controls.Add(topPanel);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _refreshTimer.Tick += (_, _) => UpdateVirtualListSize();

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
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C) && _listView.Focused)
        {
            CopySelectedEntriesAsync();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
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
                _refreshTimer.Stop();
                UpdateVirtualListSize();
                SetIndexingState(false);

                // Индекс мог вырасти, пока фильтр уже был активен: применяем его заново.
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
        var item = new ListViewItem(FormatPreview(preview));
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
            // Прогрев кэша — оптимизация; сбой (например, файл закрывается)
            // не должен ронять отрисовку.
        }
    }

    private static string FormatPreview(EntryPreview preview)
    {
        if (preview.LineCount <= 1)
        {
            return preview.FirstLine;
        }

        int additionalLines = preview.LineCount - 1;
        return preview.Truncated
            ? $"{preview.FirstLine} [+{additionalLines} lines…]"
            : $"{preview.FirstLine} [+{additionalLines} lines]";
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

    /// <summary>
    /// Маппинг индекса строки списка в индекс записи <see cref="LogIndex"/>.
    /// При активном фильтре индекс строки — позиция в <see cref="FilterResult.Visible"/>;
    /// -1, если позиция вне списка.
    /// </summary>
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

        // Очень большое непрерывное выделение: читаем блоками, но без вставки
        // разделителей между записями и без промежуточных строк.
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
        LogSession? session = _session;
        if (session is null)
        {
            return;
        }

        int count = _filterResult?.Visible?.Count ?? session.Index.Count;
        if (_listView.VirtualListSize != count)
        {
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

        // Ввод изменился — результат уже запущенного прохода неактуален.
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
            // Фильтр не применяется: остаётся предыдущий результат.
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
            // Отменённый запуск не применяется.
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

        // Восстанавливаем выбор на ближайшей видимой записи.
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

        return new LogFilter(levels, _textFilterBox.Text, _regexCheck.Checked);
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

        // Инвалидируем все запущенные фильтрации: их результаты уже неактуальны.
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
