namespace LogViewer.Core;

/// <summary>Набор уровней для фильтрации записей.</summary>
[Flags]
public enum LogLevelMask
{
    None = 0,
    Info = 1,
    Warning = 2,
    Error = 4,
    Exception = 8,
    Other = 16,
    All = Info | Warning | Error | Exception | Other,
}
