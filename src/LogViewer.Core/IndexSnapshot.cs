namespace LogViewer.Core;

/// <summary>
/// Неизменяемый снимок состояния <see cref="LogIndex"/>, доступный читателям.
/// Таблицы чанков, на которые ссылается снимок, никогда не реаллоцируются.
/// </summary>
public sealed class IndexSnapshot
{
    internal IndexSnapshot(int count, long indexedLength, long[][] offsetChunks, byte[][] levelChunks)
    {
        Count = count;
        IndexedLength = indexedLength;
        OffsetChunks = offsetChunks;
        LevelChunks = levelChunks;
    }

    /// <summary>Число проиндексированных записей.</summary>
    public int Count { get; }

    /// <summary>Смещение сразу после последнего проиндексированного перевода строки.</summary>
    public long IndexedLength { get; }

    internal long[][] OffsetChunks { get; }

    internal byte[][] LevelChunks { get; }
}
