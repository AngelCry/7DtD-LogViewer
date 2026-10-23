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

        // Regex создаётся до запуска задачи, чтобы невалидный шаблон падал
        // синхронно, а не внутри фоновой задачи.
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
            RunLevelOnly(index, filter.Levels, total, visible, progress, ct);
            return new FilterResult(filter, visible, total);
        }

        RunWithText(file, index, filter, regex!, total, visible, progress, windowSize, ct);
        return new FilterResult(filter, visible, total);
    }

    private static void RunLevelOnly(
        LogIndex index,
        LogLevelMask levels,
        int total,
        VisibleList visible,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        int lastPercent = -1;
        for (int i = 0; i < total; i++)
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
            for (int i = 0; i < total; i++)
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
                    // Запись выходит за окно (или больше него) — читается отдельно.
                    scratch ??= pool.Rent(TextMaxBytes);
                    int read = file.Read(entryOffset, scratch.AsSpan(0, textLength));
                    bytes = scratch.AsSpan(0, read);
                }

                if (Matches(filter.Levels, index.Level(i)) && Contains(bytes, filter, regex))
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

    private static bool Contains(ReadOnlySpan<byte> bytes, LogFilter filter, Regex regex)
    {
        string text = Encoding.UTF8.GetString(bytes);
        return filter.IsRegex
            ? SafeIsMatch(regex, text)
            : text.Contains(filter.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SafeIsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Запись, на которой сработал таймаут, считается неподходящей.
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
            // Не все конструкции поддерживаются NonBacktracking; для них —
            // обычный движок с ограничением времени.
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
