using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace LogViewer.Core;

/// <summary>
/// Фильтрация записей по уровню и тексту. Проход выполняется в фоне и читает
/// файл окнами, поэтому число обращений к диску не зависит от числа записей.
/// </summary>
public static class FilterEngine
{
    internal const int DefaultWindowSize = 4 * 1024 * 1024;

    /// <summary>Максимальная длина текста записи, участвующая в поиске.</summary>
    internal const int TextMaxBytes = 256 * 1024;

    private static readonly RegexOptions BaseRegexOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly TimeSpan FallbackRegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Проверяет корректность фильтра. Единственная ошибка — невалидное
    /// регулярное выражение.
    /// </summary>
    public static bool TryValidate(LogFilter filter, out string? error)
    {
        error = null;
        if (filter is null)
        {
            error = "Filter is null.";
            return false;
        }

        if (!NeedsRegex(filter))
        {
            return true;
        }

        try
        {
            _ = CreateRegex(filter.Text);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Фильтрует записи в фоне. Работает со снимком <see cref="LogIndex.Count"/>
    /// на момент запуска. При отмене бросает <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Невалидное регулярное выражение.</exception>
    public static Task<FilterResult> RunAsync(
        LogFile file,
        LogIndex index,
        LogFilter filter,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        return RunAsync(file, index, filter, progress, ct, DefaultWindowSize);
    }

    internal static Task<FilterResult> RunAsync(
        LogFile file,
        LogIndex index,
        LogFilter filter,
        IProgress<int>? progress,
        CancellationToken ct,
        int windowSize)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);

        Regex? regex = null;
        if (NeedsRegex(filter))
        {
            try
            {
                regex = CreateRegex(filter.Text);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(ex.Message, nameof(filter), ex);
            }
        }

        return Task.Run(
            () => Run(file, index, filter, regex, progress, windowSize, ct),
            CancellationToken.None);
    }

    /// <summary>
    /// Инкрементально расширяет существующий результат фильтрации для новых записей индекса.
    /// Обрабатывает диапазон <c>[previous.ProcessedCount - 1, index.Count)</c>.
    /// </summary>
    public static Task<FilterResult> ExtendAsync(
        LogFile file,
        LogIndex index,
        FilterResult previous,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(previous);

        Regex? regex = null;
        if (NeedsRegex(previous.Filter))
        {
            try
            {
                regex = CreateRegex(previous.Filter.Text);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(ex.Message, nameof(previous), ex);
            }
        }

        return Task.Run(
            () => Extend(file, index, previous, regex, progress, ct),
            CancellationToken.None);
    }

    private static FilterResult Run(
        LogFile file,
        LogIndex index,
        LogFilter filter,
        Regex? regex,
        IProgress<int>? progress,
        int windowSize,
        CancellationToken ct)
    {
        int total = index.Count;
        if (filter.IsEmpty)
        {
            return new FilterResult(filter, null, total);
        }

        var visible = new VisibleList();
        if (string.IsNullOrEmpty(filter.Text))
        {
            RunLevelOnly(index, filter.Levels, total, 0, visible, progress, ct);
            return new FilterResult(filter, visible, total);
        }

        RunWithText(file, index, filter, regex!, total, 0, visible, progress, windowSize, ct);
        return new FilterResult(filter, visible, total);
    }

    private static FilterResult Extend(
        LogFile file,
        LogIndex index,
        FilterResult previous,
        Regex? regex,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        int total = index.Count;
        LogFilter filter = previous.Filter;

        if (filter.IsEmpty || previous.Visible is null)
        {
            return new FilterResult(filter, previous.Visible, total);
        }

        VisibleList visible = previous.Visible;
        int startIndex = Math.Max(0, previous.ProcessedCount - 1);
        bool levelOnly = string.IsNullOrEmpty(filter.Text);

        if (startIndex < total)
        {
            int visibleCount = visible.Count;
            bool isLastVisible = visibleCount > 0 && visible[visibleCount - 1] == startIndex;
            bool matchesStart = EvaluateEntry(file, index, startIndex, filter, regex, levelOnly);

            if (isLastVisible && !matchesStart)
            {
                visible.RemoveLast();
            }
            else if (!isLastVisible && matchesStart)
            {
                visible.Append(startIndex);
            }

            if (levelOnly)
            {
                RunLevelOnly(index, filter.Levels, total, startIndex + 1, visible, progress, ct);
            }
            else
            {
                RunWithText(file, index, filter, regex!, total, startIndex + 1, visible, progress, DefaultWindowSize, ct);
            }
        }

        return new FilterResult(filter, visible, total);
    }

    private static bool EvaluateEntry(
        LogFile file,
        LogIndex index,
        int i,
        LogFilter filter,
        Regex? regex,
        bool levelOnly)
    {
        LogLevel level = index.Level(i);
        if (!Matches(filter.Levels, level))
        {
            return false;
        }

        if (levelOnly)
        {
            return true;
        }

        long entryOffset = index.Offset(i);
        long entryEnd = index.End(i);
        int textLength = (int)Math.Min(entryEnd - entryOffset, TextMaxBytes);

        ArrayPool<byte> pool = ArrayPool<byte>.Shared;
        byte[] scratch = pool.Rent(textLength);
        try
        {
            int read = file.Read(entryOffset, scratch.AsSpan(0, textLength));
            ReadOnlySpan<byte> bytes = scratch.AsSpan(0, read);
            return MatchesEntry(level, bytes, filter, regex);
        }
        finally
        {
            pool.Return(scratch);
        }
    }

    /// <summary>
    /// Общий метод сопоставления записи с фильтром (уровень + текст/регулярное выражение).
    /// Используется как <see cref="RunAsync"/>, так и <see cref="ExtendAsync"/>.
    /// </summary>
    internal static bool MatchesEntry(
        LogLevel level,
        ReadOnlySpan<byte> textBytes,
        LogFilter filter,
        Regex? regex)
    {
        if (!Matches(filter.Levels, level))
        {
            return false;
        }

        if (string.IsNullOrEmpty(filter.Text))
        {
            return true;
        }

        string text = Encoding.UTF8.GetString(textBytes);
        return filter.IsRegex
            ? SafeIsMatch(regex!, text)
            : text.Contains(filter.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static void RunLevelOnly(
        LogIndex index,
        LogLevelMask levels,
        int total,
        int startIndex,
        VisibleList visible,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        int lastPercent = -1;
        for (int i = startIndex; i < total; i++)
        {
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            if (Matches(levels, index.Level(i)))
            {
                visible.Append(i);
            }

            ReportProgress(progress, i + 1, total, ref lastPercent);
        }
    }

    private static void RunWithText(
        LogFile file,
        LogIndex index,
        LogFilter filter,
        Regex regex,
        int total,
        int startIndex,
        VisibleList visible,
        IProgress<int>? progress,
        int windowSize,
        CancellationToken ct)
    {
        ArrayPool<byte> pool = ArrayPool<byte>.Shared;
        byte[] window = pool.Rent(windowSize);
        byte[]? scratch = null;
        long windowStart = -1;
        int windowLength = 0;
        int lastPercent = -1;

        try
        {
            for (int i = startIndex; i < total; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                long entryOffset = index.Offset(i);
                long entryEnd = index.End(i);
                int textLength = (int)Math.Min(entryEnd - entryOffset, TextMaxBytes);

                if (windowStart < 0 || entryOffset < windowStart || entryOffset >= windowStart + windowLength)
                {
                    ct.ThrowIfCancellationRequested();
                    windowStart = entryOffset;
                    windowLength = file.Read(windowStart, window.AsSpan(0, windowSize));
                }

                ReadOnlySpan<byte> bytes;
                if (entryOffset + textLength <= windowStart + windowLength)
                {
                    bytes = window.AsSpan((int)(entryOffset - windowStart), textLength);
                }
                else
                {
                    scratch ??= pool.Rent(TextMaxBytes);
                    int read = file.Read(entryOffset, scratch.AsSpan(0, textLength));
                    bytes = scratch.AsSpan(0, read);
                }

                if (MatchesEntry(index.Level(i), bytes, filter, regex))
                {
                    visible.Append(i);
                }

                ReportProgress(progress, i + 1, total, ref lastPercent);
            }
        }
        finally
        {
            pool.Return(window);
            if (scratch is not null)
            {
                pool.Return(scratch);
            }
        }
    }

    private static bool SafeIsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Regex CreateRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, BaseRegexOptions | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, BaseRegexOptions, FallbackRegexTimeout);
        }
    }

    private static bool NeedsRegex(LogFilter filter) =>
        filter.IsRegex && !string.IsNullOrEmpty(filter.Text);

    private static bool Matches(LogLevelMask levels, LogLevel level) =>
        (levels & ToMask(level)) != 0;

    private static LogLevelMask ToMask(LogLevel level) => level switch
    {
        LogLevel.Info => LogLevelMask.Info,
        LogLevel.Warning => LogLevelMask.Warning,
        LogLevel.Error => LogLevelMask.Error,
        LogLevel.Exception => LogLevelMask.Exception,
        _ => LogLevelMask.Other,
    };

    private static void ReportProgress(IProgress<int>? progress, int done, int total, ref int lastPercent)
    {
        if (progress is null)
        {
            return;
        }

        int percent = total <= 0 ? 100 : (int)((long)done * 100 / total);
        if (percent != lastPercent)
        {
            lastPercent = percent;
            progress.Report(percent);
        }
    }
}
