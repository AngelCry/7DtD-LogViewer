using System.Buffers;

namespace LogViewer.Core;

/// <summary>
/// Инкрементальный индексатор лога. Читает файл блоками, разбирает строки на байтах
/// и публикует логические записи в <see cref="LogIndex"/>.
/// </summary>
public sealed class LogIndexer
{
    public const int DefaultBlockSize = 1 << 20;

    private static ReadOnlySpan<byte> ExceptionPrefix => "Exception"u8;

    private readonly int _blockSize;

    public LogIndexer(int blockSize = DefaultBlockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        _blockSize = blockSize;
    }

    /// <summary>
    /// Индексирует диапазон <c>[start, end)</c>. <paramref name="start"/> должен быть равен
    /// <see cref="LogIndex.IndexedLength"/> (или 0 для пустого индекса).
    /// После каждого прочитанного блока публикует достигнутое состояние.
    /// При отмене публикует достигнутое и бросает <see cref="OperationCanceledException"/>.
    /// </summary>
    public void IndexRange(
        LogFile file,
        LogIndex index,
        long start,
        long end,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(end);
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "end must be greater than or equal to start.");
        }

        if (index.Count == 0 ? start != 0 : start != index.IndexedLength)
        {
            throw new ArgumentException(
                "start must be equal to IndexedLength (or 0 for an empty index).",
                nameof(start));
        }

        long contentStart = start;
        if (index.Count == 0 && start == 0 && end >= 3 && HasBom(file))
        {
            contentStart = 3;
        }

        long indexedEnd = index.IndexedLength;
        long rangeLength = end - start;
        long bufferOffset = contentStart;
        int lastPercent = -1;
        bool openEntry = index.Count > 0;

        ArrayPool<byte> pool = ArrayPool<byte>.Shared;
        byte[] buffer = pool.Rent(_blockSize);
        int fill = 0;

        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    index.Publish(indexedEnd);
                    throw new OperationCanceledException(ct);
                }

                if (bufferOffset + fill >= end)
                {
                    break;
                }

                int space = buffer.Length - fill;
                if (space == 0)
                {
                    GrowBuffer(pool, ref buffer, fill);
                    continue;
                }

                int want = (int)Math.Min(space, end - (bufferOffset + fill));
                int read = file.Read(bufferOffset + fill, buffer.AsSpan(fill, want));
                if (read == 0)
                {
                    break;
                }

                fill += read;

                int lineStart = 0;
                int scan = 0;

                while (scan < fill)
                {
                    int relativeNewline = buffer.AsSpan(scan, fill - scan).IndexOf((byte)'\n');
                    if (relativeNewline < 0)
                    {
                        break;
                    }

                    int lineEnd = scan + relativeNewline + 1;
                    ProcessLine(
                        buffer.AsSpan(lineStart, lineEnd - lineStart),
                        bufferOffset + lineStart,
                        index,
                        ref openEntry);

                    indexedEnd = bufferOffset + lineEnd;
                    scan = lineEnd;
                    lineStart = lineEnd;
                }

                int leftover = fill - lineStart;
                if (leftover > 0)
                {
                    Buffer.BlockCopy(buffer, lineStart, buffer, 0, leftover);
                }

                bufferOffset += lineStart;
                fill = leftover;

                index.Publish(indexedEnd);
                ReportProgress(progress, bufferOffset - start, rangeLength, ref lastPercent);
            }

            ReportProgress(progress, bufferOffset - start, rangeLength, ref lastPercent, forceFinal: true);
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    private static bool HasBom(LogFile file)
    {
        Span<byte> bom = stackalloc byte[3];
        return file.Read(0, bom) == 3
            && bom[0] == 0xEF
            && bom[1] == 0xBB
            && bom[2] == 0xBF;
    }

    private static void GrowBuffer(ArrayPool<byte> pool, ref byte[] buffer, int fill)
    {
        int newLength = buffer.Length <= int.MaxValue / 2
            ? buffer.Length * 2
            : int.MaxValue;

        byte[] bigger = pool.Rent(newLength);
        Buffer.BlockCopy(buffer, 0, bigger, 0, fill);
        pool.Return(buffer);
        buffer = bigger;
    }

    private static void ProcessLine(
        ReadOnlySpan<byte> lineWithNewline,
        long lineOffset,
        LogIndex index,
        ref bool openEntry)
    {
        ReadOnlySpan<byte> line = lineWithNewline[..^1];
        if (!line.IsEmpty && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (IsTimestampLine(line))
        {
            index.Append(lineOffset, ParseLevel(line));
            openEntry = true;
        }
        else if (!openEntry)
        {
            // Первая строка файла (или первая строка после BOM) без timestamp
            // всё равно образует запись.
            index.Append(lineOffset, LogLevel.Other);
            openEntry = true;
        }
        else if (IsExceptionContinuation(line))
        {
            index.RaiseLastLevel(LogLevel.Exception);
        }
    }

    internal static bool IsTimestampLine(ReadOnlySpan<byte> line)
    {
        return line.Length >= 19
            && IsDigit(line[0])
            && IsDigit(line[1])
            && IsDigit(line[2])
            && IsDigit(line[3])
            && line[4] == (byte)'-'
            && IsDigit(line[5])
            && IsDigit(line[6])
            && line[7] == (byte)'-'
            && IsDigit(line[8])
            && IsDigit(line[9])
            && line[10] == (byte)'T'
            && IsDigit(line[11])
            && IsDigit(line[12])
            && line[13] == (byte)':'
            && IsDigit(line[14])
            && IsDigit(line[15])
            && line[16] == (byte)':'
            && IsDigit(line[17])
            && IsDigit(line[18]);
    }

    internal static LogLevel ParseLevel(ReadOnlySpan<byte> line)
    {
        // Предполагается, что timestamp уже проверен IsTimestampLine.
        int i = 19;
        if (i >= line.Length || line[i] != (byte)' ')
        {
            return LogLevel.Other;
        }

        i++;
        int numberStart = i;
        while (i < line.Length && (IsDigit(line[i]) || line[i] == (byte)'.'))
        {
            i++;
        }

        if (i == numberStart || i >= line.Length || line[i] != (byte)' ')
        {
            return LogLevel.Other;
        }

        i++;
        if (i + 3 > line.Length
            || !IsAsciiLetter(line[i])
            || !IsAsciiLetter(line[i + 1])
            || !IsAsciiLetter(line[i + 2]))
        {
            return LogLevel.Other;
        }

        byte first = line[i];
        byte second = line[i + 1];
        byte third = line[i + 2];
        i += 3;

        if (i < line.Length && line[i] != (byte)' ')
        {
            return LogLevel.Other;
        }

        if (first == (byte)'I' && second == (byte)'N' && third == (byte)'F')
        {
            return LogLevel.Info;
        }

        if (first == (byte)'W' && second == (byte)'R' && third == (byte)'N')
        {
            return LogLevel.Warning;
        }

        if (first == (byte)'E' && second == (byte)'R' && third == (byte)'R')
        {
            return LogLevel.Error;
        }

        if (first == (byte)'E' && second == (byte)'X' && third == (byte)'C')
        {
            return LogLevel.Exception;
        }

        return LogLevel.Other;
    }

    internal static bool IsExceptionContinuation(ReadOnlySpan<byte> line)
    {
        int start = 0;
        while (start < line.Length && (line[start] == (byte)' ' || line[start] == (byte)'\t'))
        {
            start++;
        }

        ReadOnlySpan<byte> trimmed = line[start..];
        if (trimmed.StartsWith(ExceptionPrefix))
        {
            return true;
        }

        // Первый токен: до пробела/табуляции или двоеточия (например,
        // "NullReferenceException: ...").
        int tokenLength = 0;
        while (tokenLength < trimmed.Length
               && trimmed[tokenLength] != (byte)' '
               && trimmed[tokenLength] != (byte)'\t'
               && trimmed[tokenLength] != (byte)':')
        {
            tokenLength++;
        }

        return tokenLength >= ExceptionPrefix.Length
            && trimmed[..tokenLength].EndsWith(ExceptionPrefix);
    }

    private static void ReportProgress(
        IProgress<int>? progress,
        long done,
        long total,
        ref int lastPercent,
        bool forceFinal = false)
    {
        if (progress is null)
        {
            return;
        }

        long clampedDone = Math.Clamp(done, 0, Math.Max(total, 0));
        int percent = total <= 0 ? 100 : (int)(clampedDone * 100 / total);
        if (forceFinal)
        {
            percent = 100;
        }

        if (percent != lastPercent)
        {
            lastPercent = percent;
            progress.Report(percent);
        }
    }

    private static bool IsDigit(byte value) => (uint)(value - (byte)'0') <= 9;

    private static bool IsAsciiLetter(byte value) =>
        (uint)((value | 0x20) - (byte)'a') <= 'z' - 'a';
}
