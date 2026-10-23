namespace LogViewer.Core;

/// <summary>Результат прохода фильтра по снимку индекса.</summary>
public sealed class FilterResult
{
    public FilterResult(LogFilter filter, VisibleList? visible, int processedCount)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(processedCount);

        Filter = filter;
        Visible = visible;
        ProcessedCount = processedCount;
    }

    public LogFilter Filter { get; }

    /// <summary>Видимые записи; <c>null</c> — показывать все.</summary>
    public VisibleList? Visible { get; }

    /// <summary>Сколько записей индекса обработано.</summary>
    public int ProcessedCount { get; }
}
