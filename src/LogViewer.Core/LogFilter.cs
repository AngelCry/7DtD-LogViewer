namespace LogViewer.Core;

/// <summary>
/// Фильтр записей: набор уровней, текст (подстрока или регулярное выражение)
/// и список исключений (записи, содержащие любую из этих строк, не показываются).
/// </summary>
public sealed record LogFilter(
    LogLevelMask Levels,
    string Text,
    bool IsRegex,
    IReadOnlyList<string>? Excludes = null)
{
    /// <summary>Фильтр не ограничивает ничего: все уровни, пустой текст, нет исключений.</summary>
    public bool IsEmpty =>
        Levels == LogLevelMask.All &&
        string.IsNullOrEmpty(Text) &&
        Excludes is not { Count: > 0 };

    /// <summary>Есть ли строки исключения.</summary>
    public bool HasExcludes => Excludes is { Count: > 0 };
}
