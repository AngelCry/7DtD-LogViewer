using System.Globalization;
using LogViewer.Core;

namespace LogViewer.App;

internal sealed class MainForm : Form
{
    private readonly BufferedListView _listView;
    private readonly Button _openButton;
    private readonly Button _cancelButton;
    private readonly ProgressBar _progressBar;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private LogSession? _session;
    private CancellationTokenSource? _cts;

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

        Controls.Add(_listView);
        Controls.Add(statusStrip);
        Controls.Add(topPanel);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _refreshTimer.Tick += (_, _) => UpdateVirtualListSize();

        SetIndexingState(false);
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

        try
        {
            await session.IndexAsync(progress, cts.Token);

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
        if (session is null
            || e.ItemIndex < 0
            || e.ItemIndex >= session.Index.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        LogLevel level = session.Index.Level(e.ItemIndex);
        e.Item = new ListViewItem($"entry {e.ItemIndex} [{level}]");
    }

    private void UpdateVirtualListSize()
    {
        LogSession? session = _session;
        if (session is null)
        {
            return;
        }

        int count = session.Index.Count;
        if (_listView.VirtualListSize != count)
        {
            _listView.VirtualListSize = count;
        }
    }

    private void SetIndexingState(bool indexing)
    {
        _openButton.Enabled = !indexing;
        _cancelButton.Enabled = indexing;
    }

    private void DisposeSession()
    {
        _refreshTimer.Stop();

        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        LogSession? session = _session;
        _session = null;
        _listView.VirtualListSize = 0;
        session?.Dispose();
        cts?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        DisposeSession();
        _refreshTimer.Dispose();
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
