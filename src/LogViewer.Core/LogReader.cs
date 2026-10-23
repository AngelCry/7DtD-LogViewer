using System.Text;

namespace LogViewer.Core;

public readonly record struct EntryPreview(string FirstLine, int LineCount, bool Truncated);

/// <summary>
/// Чтение записей лога по индексу. Превью кэшируются в LRU-кэше,
/// полный текст читается по запросу.
/// </summary>
public sealed class LogReader
{
    public const int PreviewMaxBytes = 32 * 1024;
    public const int FullTextMaxBytes = 1024 * 1024;

    private const string TruncatedSuffix = "… [truncated]";
    private const string ReadErrorPrefix = "<read error: ";

    private readonly LogFile _file;
    private readonly LogIndex _index;
    private readonly LruCache<int, EntryPreview> _previewCache;
    private long _readCallCount;

    public LogReader(LogFile file, LogIndex index, int cacheCapacity = 2000)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(index);

        _file = file;
        _index = index;
        _previewCache = new LruCache<int, EntryPreview>(cacheCapacity);
    }

    /// <summary>Диагностика: число фактических обращений к файлу.</summary>
    internal long ReadCallCount => Interlocked.Read(ref _readCallCount);

    public EntryPreview GetPreview(int i)
    {
        if (_previewCache.TryGet(i, out EntryPreview cached))
        {
            return cached;
        }

        if (!TryReadText(i, PreviewMaxBytes, out string text, out bool truncated, out string? error))
        {
            return new EntryPreview(error!, 0, false);
        }

        string visibleText = text.TrimEnd('\r', '\n');
        if (truncated)
        {
            visibleText += TruncatedSuffix;
        }

        var preview = new EntryPreview(
            ExtractFirstLine(visibleText),
            CountLines(text),
            truncated);

        _previewCache.Set(i, preview);
        return preview;
    }

    public string GetFullText(int i)
    {
        if (!TryReadText(i, FullTextMaxBytes, out string text, out bool truncated, out string? error))
        {
            return error!;
        }

        text = text.TrimEnd('\r', '\n');
        return truncated ? text + TruncatedSuffix : text;
    }

    public void Invalidate(int i)
    {
        _previewCache.Remove(i);
    }

    public void ClearCache()
    {
        _previewCache.Clear();
    }

    private bool TryReadText(
        int i,
        int limitBytes,
        out string text,
        out bool truncated,
        out string? error)
    {
        text = string.Empty;
        truncated = false;
        error = null;

        // Проверка индекса должна бросать ArgumentOutOfRangeException, а не
        // маскироваться под ошибку чтения.
        long offset = _index.Offset(i);
        long end = _index.End(i);
        long entryLength = end - offset;
        truncated = entryLength > limitBytes;

        int toRead = (int)Math.Min(entryLength, limitBytes);
        if (toRead <= 0)
        {
            return true;
        }

        byte[] buffer = new byte[toRead];
        int read;
        try
        {
            Interlocked.Increment(ref _readCallCount);
            read = _file.Read(offset, buffer);
        }
        catch (Exception ex)
        {
            error = $"{ReadErrorPrefix}{ex.Message}>";
            return false;
        }

        if (read <= 0)
        {
            return true;
        }

        int length = read;
        if (truncated)
        {
            length = FindUtf8Boundary(buffer.AsSpan(0, read));
        }

        text = Encoding.UTF8.GetString(buffer, 0, length);
        return true;
    }

    /// <summary>
    /// Возвращает конец последнего полного UTF-8 символа в буфере. Если буфер
    /// обрывается в середине многобайтового символа, этот символ отбрасывается.
    /// </summary>
    private static int FindUtf8Boundary(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.Length;
        if (length == 0)
        {
            return 0;
        }

        int i = length;
        while (i > 0 && (bytes[i - 1] & 0xC0) == 0x80)
        {
            i--;
        }

        if (i == 0)
        {
            return 0;
        }

        byte lead = bytes[i - 1];
        int sequenceLength = lead switch
        {
            < 0x80 => 1,
            >= 0xC0 and < 0xE0 => 2,
            >= 0xE0 and < 0xF0 => 3,
            >= 0xF0 and < 0xF8 => 4,
            _ => 1,
        };

        int sequenceStart = i - 1;
        return sequenceStart + sequenceLength > length ? sequenceStart : length;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        int lines = 0;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        if (text[^1] != '\n')
        {
            lines++;
        }

        return lines;
    }

    private static string ExtractFirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        string firstLine = newline >= 0 ? text[..newline] : text;

        if (firstLine.EndsWith('\r'))
        {
            firstLine = firstLine[..^1];
        }

        char[]? chars = null;
        for (int i = 0; i < firstLine.Length; i++)
        {
            if (firstLine[i] < 0x20)
            {
                chars ??= firstLine.ToCharArray();
                chars[i] = ' ';
            }
        }

        return chars is null ? firstLine : new string(chars);
    }
}
