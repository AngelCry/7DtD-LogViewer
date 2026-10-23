namespace LogViewer.Core;

/// <summary>
/// Растущий список номеров записей, прошедших фильтр. Хранится чанками,
/// чтобы не выделять и не копировать массив размером с файл.
/// </summary>
/// <remarks>
/// У списка один писатель (<see cref="Append"/>/<see cref="RemoveLast"/>).
/// Читатели видят только опубликованный <see cref="Count"/>: запись элемента
/// выполняется до <see cref="Volatile"/>-записи счётчика.
/// </remarks>
public sealed class VisibleList
{
    private const int ChunkSize = 65536;

    private int[][] _chunks = [];
    private int _count;

    /// <summary>Число видимых записей.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Номер записи по номеру в списке видимых.</summary>
    public int this[int n]
    {
        get
        {
            int count = Volatile.Read(ref _count);
            if ((uint)n >= (uint)count)
            {
                throw new ArgumentOutOfRangeException(nameof(n));
            }

            return _chunks[n / ChunkSize][n % ChunkSize];
        }
    }

    /// <summary>
    /// Бинарный поиск записи <paramref name="entryIndex"/> в списке. Если её нет,
    /// возвращается позиция ближайшей следующей записи; если переданный номер больше
    /// всех видимых — позиция последней. Для пустого списка — -1.
    /// </summary>
    public int IndexOfOrNearest(int entryIndex)
    {
        int count = Volatile.Read(ref _count);
        if (count == 0)
        {
            return -1;
        }

        int low = 0;
        int high = count - 1;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_chunks[middle / ChunkSize][middle % ChunkSize] < entryIndex)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Добавляет номер записи. Номера должны возрастать. Только для писателя.</summary>
    public void Append(int entryIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);

        int count = _count;
        if (count > 0 && entryIndex <= _chunks[(count - 1) / ChunkSize][(count - 1) % ChunkSize])
        {
            throw new ArgumentException(
                "Entry indexes must be added in strictly increasing order.",
                nameof(entryIndex));
        }

        EnsureChunkFor(count);
        _chunks[count / ChunkSize][count % ChunkSize] = entryIndex;
        Volatile.Write(ref _count, count + 1);
    }

    /// <summary>Удаляет последний добавленный номер. Только для писателя.</summary>
    public void RemoveLast()
    {
        int count = _count;
        if (count == 0)
        {
            throw new InvalidOperationException("The visible list is empty.");
        }

        Volatile.Write(ref _count, count - 1);
    }

    private void EnsureChunkFor(int index)
    {
        int requiredChunks = index / ChunkSize + 1;
        int currentChunks = _chunks.Length;
        if (requiredChunks <= currentChunks)
        {
            return;
        }

        int newChunkCount = Math.Max(requiredChunks, currentChunks * 2);
        Array.Resize(ref _chunks, newChunkCount);

        for (int chunk = currentChunks; chunk < newChunkCount; chunk++)
        {
            _chunks[chunk] = new int[ChunkSize];
        }
    }
}
