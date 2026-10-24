using System.Diagnostics;

namespace LogViewer.Core;

/// <summary>
/// Открытый лог: файл, индекс и операции индексации. Владеет <see cref="LogFile"/>
/// и гарантирует, что при освобождении фоновые задачи отменены и завершены.
/// </summary>
public sealed class LogSession : IDisposable
{
    private readonly LogFile _file;
    private byte[] _initialHead;
    private LogIndex _index = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _indexTask;

    private long _indexTimeTicks;
    private bool _disposed;
    private int _generation;

    private LogSession(LogFile file)
    {
        _file = file;
        Reader = new LogReader(file, _index);

        long len = _file.Length;
        int headLen = (int)Math.Min(256, len);
        _initialHead = new byte[headLen];
        if (headLen > 0)
        {
            _file.Read(0, _initialHead);
        }
    }

    public LogFile File => _file;

    public LogIndex Index => _index;

    public LogReader Reader { get; private set; }

    public int Generation => Volatile.Read(ref _generation);

    public TimeSpan IndexTime => TimeSpan.FromTicks(Interlocked.Read(ref _indexTimeTicks));

    public static LogSession Open(string path)
    {
        return new LogSession(LogFile.Open(path));
    }

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

    public Task<PollResult> PollAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_indexTask is { IsCompleted: false })
            {
                int currentCount = _index.Count;
                return Task.FromResult(new PollResult(PollKind.NoChange, currentCount, currentCount));
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts?.Dispose();
            _cts = cts;
            _indexTask = Task.Run(() => PollCore(cts.Token), CancellationToken.None);
            return (Task<PollResult>)_indexTask;
        }
    }

    private PollResult PollCore(CancellationToken ct)
    {
        int oldCount = _index.Count;
        long currentLength = _file.Length;
        long indexedLength = _index.IndexedLength;

        if (currentLength == indexedLength)
        {
            return new PollResult(PollKind.NoChange, oldCount, oldCount);
        }

        if (currentLength < indexedLength)
        {
            ResetSession();
            var sw = Stopwatch.StartNew();
            new LogIndexer().IndexRange(_file, _index, 0, currentLength, null, ct);
            Interlocked.Exchange(ref _indexTimeTicks, sw.Elapsed.Ticks);
            return new PollResult(PollKind.Reset, oldCount, _index.Count);
        }

        int compareLen = (int)Math.Min(256, Math.Min(currentLength, indexedLength));
        if (compareLen > _initialHead.Length)
        {
            // Initial head was captured when file was shorter than compareLen (< 256 bytes)
            byte[] newHead = new byte[compareLen];
            _file.Read(0, newHead);
            if (!newHead.AsSpan(0, _initialHead.Length).SequenceEqual(_initialHead))
            {
                ResetSession();
                var sw = Stopwatch.StartNew();
                new LogIndexer().IndexRange(_file, _index, 0, currentLength, null, ct);
                Interlocked.Exchange(ref _indexTimeTicks, sw.Elapsed.Ticks);
                return new PollResult(PollKind.Reset, oldCount, _index.Count);
            }

            // Head matches up to previous initialHead, update _initialHead
            _initialHead = newHead;
        }
        else if (compareLen > 0)
        {
            Span<byte> head = stackalloc byte[compareLen];
            _file.Read(0, head);
            if (!head.SequenceEqual(_initialHead.AsSpan(0, compareLen)))
            {
                ResetSession();
                var sw = Stopwatch.StartNew();
                new LogIndexer().IndexRange(_file, _index, 0, currentLength, null, ct);
                Interlocked.Exchange(ref _indexTimeTicks, sw.Elapsed.Ticks);
                return new PollResult(PollKind.Reset, oldCount, _index.Count);
            }
        }

        var sw2 = Stopwatch.StartNew();
        new LogIndexer().IndexRange(_file, _index, indexedLength, currentLength, null, ct);
        Interlocked.Exchange(ref _indexTimeTicks, sw2.Elapsed.Ticks);

        int newCount = _index.Count;
        if (newCount > 0)
        {
            Reader.Invalidate(newCount - 1);
        }

        return new PollResult(PollKind.Grown, oldCount, newCount);
    }

    private void ResetSession()
    {
        _index = new();
        Reader = new LogReader(_file, _index);
        Interlocked.Increment(ref _generation);

        long len = _file.Length;
        int headLen = (int)Math.Min(256, len);
        _initialHead = new byte[headLen];
        if (headLen > 0)
        {
            _file.Read(0, _initialHead);
        }
    }

    private void IndexCore(IProgress<int>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            new LogIndexer().IndexRange(_file, _index, _index.IndexedLength, _file.Length, progress, ct);
            
            // Re-capture _initialHead after initial indexing if file grew or was created empty
            long len = _file.Length;
            int headLen = (int)Math.Min(256, len);
            if (headLen > _initialHead.Length)
            {
                _initialHead = new byte[headLen];
                if (headLen > 0)
                {
                    _file.Read(0, _initialHead);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _indexTimeTicks, sw.Elapsed.Ticks);
        }
    }

    public void Dispose()
    {
        Task? indexTask;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            indexTask = _indexTask;
            cts = _cts;
            _cts = null;
        }

        cts?.Cancel();

        if (indexTask is not null)
        {
            try
            {
                indexTask.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }
        }

        cts?.Dispose();
        _file.Dispose();
    }
}
