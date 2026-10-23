namespace LogViewer.Core;

/// <summary>
/// Фильтр записей: набор уровней и текст (подстрока или регулярное выражение).
/// </summary>
public sealed record LogFilter(LogLevelMask Levels, string Text, bool IsRegex)
{
    /// <summary>Фильтр не ограничивает ничего: все уровни и пустой текст.</summary>
    public bool IsEmpty => Levels == LogLevelMask.All && string.IsNullOrEmpty(Text);
}
