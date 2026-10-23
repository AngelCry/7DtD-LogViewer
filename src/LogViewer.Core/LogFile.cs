using Microsoft.Win32.SafeHandles;

namespace LogViewer.Core;

/// <summary>
/// Открытый на чтение файл лога. Позволяет читать байты по произвольному смещению,
/// не загружая файл в память. Все методы безопасны для одновременных вызовов из разных потоков.
/// </summary>
public sealed class LogFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly SafeFileHandle _handle;
    private long _readCallCount;
    private bool _disposed;

    private LogFile(FileStream stream)
    {
        _stream = stream;
        _handle = stream.SafeFileHandle;
        Path = stream.Name;
    }

    /// <summary>Полный путь к открытому файлу.</summary>
    public string Path { get; }

    /// <summary>Текущая длина файла в байтах.</summary>
    public long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return RandomAccess.GetLength(_handle);
        }
    }

    /// <summary>
    /// Открывает файл для чтения. Файл может одновременно быть открыт на запись другим процессом.
    /// </summary>
    /// <exception cref="FileNotFoundException">Файл не существует.</exception>
    public static LogFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.RandomAccess);

        return new LogFile(stream);
    }

    /// <summary>
    /// Читает байты в <paramref name="destination"/> начиная со смещения <paramref name="offset"/>
    /// до заполнения буфера или до конца файла.
    /// </summary>
    /// <returns>Число прочитанных байт; 0, если <paramref name="offset"/> за концом файла.</returns>
    public int Read(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        Interlocked.Increment(ref _readCallCount);

        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(_handle, destination[total..], offset + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>Диагностика: число вызовов <see cref="Read"/>.</summary>
    internal long ReadCallCount => Interlocked.Read(ref _readCallCount);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
