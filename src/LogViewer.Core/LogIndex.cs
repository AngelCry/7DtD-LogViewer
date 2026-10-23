namespace LogViewer.Core;

/// <summary>
/// Индекс логических записей лога. Смещения и уровни хранятся чанками,
/// чтобы не выделять массивы, пропорциональные размеру файла.
/// </summary>
/// <remarks>
/// У индекса один писатель. Читатели работают с опубликованным снимком
/// (<see cref="Snapshot"/>) и никогда не видят наполовину заполненный чанк.
/// </remarks>
public sealed class LogIndex
{
    public const int ChunkSize = 65536;

    private IndexSnapshot _snapshot = new(0, 0, [], []);
    private long[][] _offsetChunks = [];
    private byte[][] _levelChunks = [];
    private int _count;

    /// <summary>Последний опубликованный снимок индекса.</summary>
    public IndexSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public int Count => Snapshot.Count;

    public long IndexedLength => Snapshot.IndexedLength;

    /// <summary>Смещение первого байта записи <paramref name="i"/>.</summary>
    public long Offset(int i)
    {
        IndexSnapshot snapshot = Snapshot;
        if ((uint)i >= (uint)snapshot.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(i));
        }

        return snapshot.OffsetChunks[i / ChunkSize][i % ChunkSize];
    }

    /// <summary>Уровень записи <paramref name="i"/>.</summary>
    public LogLevel Level(int i)
    {
        IndexSnapshot snapshot = Snapshot;
        if ((uint)i >= (uint)snapshot.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(i));
        }

        return (LogLevel)snapshot.LevelChunks[i / ChunkSize][i % ChunkSize];
    }

    /// <summary>
    /// Смещение сразу после записи <paramref name="i"/>: offset следующей записи,
    /// для последней — <see cref="IndexedLength"/>.
    /// </summary>
    public long End(int i)
    {
        IndexSnapshot snapshot = Snapshot;
        if ((uint)i >= (uint)snapshot.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(i));
        }

        return i == snapshot.Count - 1
            ? snapshot.IndexedLength
            : snapshot.OffsetChunks[(i + 1) / ChunkSize][(i + 1) % ChunkSize];
    }

    /// <summary>Добавляет запись. Только для писателя.</summary>
    public void Append(long offset, LogLevel level)
    {
        EnsureChunkFor(_count);

        int chunk = _count / ChunkSize;
        int slot = _count % ChunkSize;
        _offsetChunks[chunk][slot] = offset;
        _levelChunks[chunk][slot] = (byte)level;
        _count++;
    }

    /// <summary>
    /// Повышает уровень последней записи, никогда не понижая его.
    /// На пустом индексе — no-op. Только для писателя.
    /// </summary>
    public void RaiseLastLevel(LogLevel level)
    {
        if (_count == 0)
        {
            return;
        }

        int i = _count - 1;
        ref byte slot = ref _levelChunks[i / ChunkSize][i % ChunkSize];
        byte value = (byte)level;
        if (value > slot)
        {
            Volatile.Write(ref slot, value);
        }
    }

    /// <summary>
    /// Публикует накопленное состояние читателям. Только для писателя.
    /// </summary>
    public void Publish(long indexedLength)
    {
        Volatile.Write(
            ref _snapshot,
            new IndexSnapshot(_count, indexedLength, _offsetChunks, _levelChunks));
    }

    private void EnsureChunkFor(int index)
    {
        int requiredChunks = index / ChunkSize + 1;
        int currentChunks = _offsetChunks.Length;
        if (requiredChunks <= currentChunks)
        {
            return;
        }

        int newChunkCount = Math.Max(requiredChunks, currentChunks * 2);
        Array.Resize(ref _offsetChunks, newChunkCount);
        Array.Resize(ref _levelChunks, newChunkCount);

        for (int chunk = currentChunks; chunk < newChunkCount; chunk++)
        {
            _offsetChunks[chunk] = new long[ChunkSize];
            _levelChunks[chunk] = new byte[ChunkSize];
        }
    }
}
