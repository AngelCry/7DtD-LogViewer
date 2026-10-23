using System.Diagnostics;

namespace LogViewer.Core;

/// <summary>
/// Открытый лог: файл, индекс и операции индексации. Владеет <see cref="LogFile"/>
/// и гарантирует, что при освобождении фоновые задачи отменены и завершены.
/// </summary>
public sealed class LogSession : IDisposable
{
    private readonly LogFile _file;
    private readonly LogIndex _index = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _indexTask;
    private long _indexTimeTicks;
    private bool _disposed;

    private LogSession(LogFile file)
    {
        _file = file;
    }

    public LogFile File => _file;

    public LogIndex Index => _index;

    public TimeSpan IndexTime => TimeSpan.FromTicks(Interlocked.Read(ref _indexTimeTicks));

    public static LogSession Open(string path)
    {
        return new LogSession(LogFile.Open(path));
    }

    /// <summary>
    /// Индексирует файл в фоне. Повторный вызов после завершения продолжает
    /// индексацию с <see cref="LogIndex.IndexedLength"/>.
    /// </summary>
    public Task IndexAsync(IProgress<int>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_indexTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("Indexing is already running.");
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts?.Dispose();
            _cts = cts;
            Interlocked.Exchange(ref _indexTimeTicks, 0);
            _indexTask = Task.Run(() => IndexCore(progress, cts.Token), CancellationToken.None);
            return _indexTask;
        }
    }

    private void IndexCore(IProgress<int>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            new LogIndexer().IndexRange(
                _file,
                _index,
                _index.IndexedLength,
                _file.Length,
                progress,
                ct);
        }
        finally
        {
            Interlocked.Exchange(ref _indexTimeTicks, stopwatch.Elapsed.Ticks);
        }
    }

    public void Dispose()
    {
        Task? task;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            task = _indexTask;
            cts = _cts;
            _cts = null;
        }

        cts?.Cancel();

        if (task is not null)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Ошибки индексации не должны ронять закрытие приложения.
            }
        }

        cts?.Dispose();
        _file.Dispose();
    }
}
