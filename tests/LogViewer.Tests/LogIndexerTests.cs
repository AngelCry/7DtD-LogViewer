using System.Text;
using LogViewer.Core;

namespace LogViewer.Tests;

public sealed class LogIndexerTests : IDisposable
{
    private readonly string _tempDir;
    private int _fileCounter;

    public LogIndexerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LogViewerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void EmptyFile_ProducesEmptyIndex(int blockSize)
    {
        LogIndex index = IndexBytes([], blockSize);

        Assert.Equal(0, index.Count);
        Assert.Equal(0, index.IndexedLength);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void OnlyBom_ProducesEmptyIndex(int blockSize)
    {
        LogIndex index = IndexBytes([0xEF, 0xBB, 0xBF], blockSize);

        Assert.Equal(0, index.Count);
        Assert.Equal(0, index.IndexedLength);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void BomBeforeFirstLine_IsSkipped(int blockSize)
    {
        byte[] line = "2024-10-23T14:23:45 123.456 INF [A] message\n"u8.ToArray();
        byte[] content = [0xEF, 0xBB, 0xBF, .. line];

        LogIndex index = IndexBytes(content, blockSize);

        Assert.Equal(1, index.Count);
        Assert.Equal(3, index.Offset(0));
        Assert.Equal(content.Length, index.End(0));
        Assert.Equal(content.Length, index.IndexedLength);
        Assert.Equal(LogLevel.Info, index.Level(0));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void LfAndCrlfLines_AreParsed(int blockSize)
    {
        byte[] first = "2024-10-23T14:23:45 123.456 INF [A] one\r\n"u8.ToArray();
        byte[] second = "2024-10-23T14:23:45 123.457 WRN [B] two\n"u8.ToArray();
        byte[] third = "2024-10-23T14:23:45 123.458 ERR no component\r\n"u8.ToArray();
        byte[] content = [.. first, .. second, .. third];

        LogIndex index = IndexBytes(content, blockSize);

        Assert.Equal(3, index.Count);
        Assert.Equal(0, index.Offset(0));
        Assert.Equal(first.Length, index.Offset(1));
        Assert.Equal(first.Length + second.Length, index.Offset(2));
        Assert.Equal(LogLevel.Info, index.Level(0));
        Assert.Equal(LogLevel.Warning, index.Level(1));
        Assert.Equal(LogLevel.Error, index.Level(2));
        Assert.Equal(first.Length, index.End(0));
        Assert.Equal(first.Length + second.Length, index.End(1));
        Assert.Equal(content.Length, index.End(2));
        Assert.Equal(content.Length, index.IndexedLength);
    }

    [Fact]
    public void CarriageReturnAtBlockBoundary_NewlineInNextBlock_IsHandled()
    {
        // 19 байт timestamp, затем \r на границе блока и \n в следующем.
        byte[] content = "2024-10-23T14:23:45\r\n"u8.ToArray();

        LogIndex index = IndexBytes(content, blockSize: 19);

        Assert.Equal(1, index.Count);
        Assert.Equal(0, index.Offset(0));
        Assert.Equal(content.Length, index.End(0));
        Assert.Equal(LogLevel.Other, index.Level(0));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void CyrillicMessages_AreIndexedWithoutCorruption(int blockSize)
    {
        byte[] first = "2024-10-23T14:23:45 123.456 INF [Мир] Привет, мир!\n"u8.ToArray();
        byte[] second = "2024-10-23T14:23:45 123.457 ERR [Тест] Ошибка: значение не найдено\r\n"u8.ToArray();
        byte[] content = [.. first, .. second];

        LogIndex index = IndexBytes(content, blockSize);

        Assert.Equal(2, index.Count);
        Assert.Equal(0, index.Offset(0));
        Assert.Equal(first.Length, index.Offset(1));
        Assert.Equal(content.Length, index.IndexedLength);
        Assert.Equal(LogLevel.Info, index.Level(0));
        Assert.Equal(LogLevel.Error, index.Level(1));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void MultiLineStackTrace_IsSingleEntry(int blockSize)
    {
        byte[] content = """
            2024-10-23T14:23:45 123.456 INF [A] message
              at Namespace.Class.Method () in file.cs:line 10
              at Namespace.Other.Run () in file.cs:line 20
            continued text
            """u8.ToArray();
        // Сырая строка выше не содержит завершающего \n — добавим его.
        content = [.. content, (byte)'\n'];

        LogIndex index = IndexBytes(content, blockSize);

        Assert.Equal(1, index.Count);
        Assert.Equal(0, index.Offset(0));
        Assert.Equal(content.Length, index.End(0));
        Assert.Equal(content.Length, index.IndexedLength);
        Assert.Equal(LogLevel.Info, index.Level(0));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void ExceptionContinuations_RaiseLevelAndNeverLowerIt(int blockSize)
    {
        byte[] content = """
            2024-10-23T14:23:45 123.456 ERR [A] failed
            NullReferenceException: Object reference not set
              at Namespace.Class.Method ()
            2024-10-23T14:23:45 123.457 WRN [B] warning
            Exception: something bad
            2024-10-23T14:23:45 123.458 EXC [C] explicit exception
              at Namespace.Class.Other ()
            2024-10-23T14:23:45 123.459 ERR [D] failed again
            System.ArgumentException: bad argument
            2024-10-23T14:23:45 123.460 INF [E] just info
              at Namespace.Class.Third ()
            """u8.ToArray();
        content = [.. content, (byte)'\n'];

        LogIndex index = IndexBytes(content, blockSize);

        Assert.Equal(5, index.Count);
        Assert.Equal(LogLevel.Exception, index.Level(0));
        Assert.Equal(LogLevel.Exception, index.Level(1));
        Assert.Equal(LogLevel.Exception, index.Level(2));
        Assert.Equal(LogLevel.Exception, index.Level(3));
        Assert.Equal(LogLevel.Info, index.Level(4));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void ExcToken_IsExceptionLevel(int blockSize)
    {
        LogIndex index = IndexBytes("2024-10-23T14:23:45 1.0 EXC [C] boom\n"u8.ToArray(), blockSize);

        Assert.Equal(LogLevel.Exception, index.Level(0));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void InfInsideMessage_DoesNotChangeErrorLevel(int blockSize)
    {
        LogIndex index = IndexBytes(
            "2024-10-23T14:23:45 1.0 ERR [C] this INF token is just text\n"u8.ToArray(),
            blockSize);

        Assert.Equal(LogLevel.Error, index.Level(0));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void TimestampWithoutRecognizableLevel_IsOther(int blockSize)
    {
        LogIndex index = IndexBytes("2024-10-23T14:23:45 1.0 XYZ [C] message\n"u8.ToArray(), blockSize);

        Assert.Equal(1, index.Count);
        Assert.Equal(LogLevel.Other, index.Level(0));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void TimestampOnly_IsOther(int blockSize)
    {
        LogIndex index = IndexBytes("2024-10-23T14:23:45\n"u8.ToArray(), blockSize);

        Assert.Equal(1, index.Count);
        Assert.Equal(LogLevel.Other, index.Level(0));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void FileStartingWithContinuation_FirstEntryIsOther(int blockSize)
    {
        byte[] first = "leading text without timestamp\n"u8.ToArray();
        byte[] second = "2024-10-23T14:23:45 1.0 INF [C] message\n"u8.ToArray();
        byte[] content = [.. first, .. second];

        LogIndex index = IndexBytes(content, blockSize);

        Assert.Equal(2, index.Count);
        Assert.Equal(0, index.Offset(0));
        Assert.Equal(LogLevel.Other, index.Level(0));
        Assert.Equal(first.Length, index.Offset(1));
        Assert.Equal(LogLevel.Info, index.Level(1));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void LastLineWithoutNewline_IsIndexedOnlyAfterNewlineAppears(int blockSize)
    {
        byte[] first = "2024-10-23T14:23:45 1.0 INF [A] first\n"u8.ToArray();
        byte[] incomplete = "2024-10-23T14:23:45 2.0 WRN [B] second"u8.ToArray();
        string path = CreateFile([.. first, .. incomplete]);

        using LogFile file = LogFile.Open(path);
        var index = new LogIndex();
        var indexer = new LogIndexer(blockSize);
        indexer.IndexRange(file, index, 0, file.Length, null, CancellationToken.None);

        Assert.Equal(1, index.Count);
        Assert.Equal(first.Length, index.IndexedLength);

        using (var writer = new FileStream(
                   path, FileMode.Append, FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            writer.Write("\n"u8);
            writer.Flush();
        }

        long newLength = file.Length;
        indexer.IndexRange(file, index, index.IndexedLength, newLength, null, CancellationToken.None);

        Assert.Equal(2, index.Count);
        Assert.Equal(first.Length, index.Offset(1));
        Assert.Equal(LogLevel.Warning, index.Level(1));
        Assert.Equal(newLength, index.End(1));
        Assert.Equal(newLength, index.IndexedLength);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void ContinuationAppendedLater_ExtendsLastEntry(int blockSize)
    {
        byte[] first = "2024-10-23T14:23:45 1.0 WRN [A] first\n"u8.ToArray();
        string path = CreateFile(first);

        using LogFile file = LogFile.Open(path);
        var index = new LogIndex();
        var indexer = new LogIndexer(blockSize);
        indexer.IndexRange(file, index, 0, file.Length, null, CancellationToken.None);

        Assert.Equal(1, index.Count);
        Assert.Equal(LogLevel.Warning, index.Level(0));

        using (var writer = new FileStream(
                   path, FileMode.Append, FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            writer.Write("Exception: later failure\n"u8);
            writer.Flush();
        }

        long newLength = file.Length;
        indexer.IndexRange(file, index, index.IndexedLength, newLength, null, CancellationToken.None);

        Assert.Equal(1, index.Count);
        Assert.Equal(LogLevel.Exception, index.Level(0));
        Assert.Equal(newLength, index.End(0));
        Assert.Equal(newLength, index.IndexedLength);
    }

    [Fact]
    public void Cancellation_DuringIndexing_LeavesConsistentPublishedIndex()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 5000; i++)
        {
            builder.Append("2024-10-23T14:23:45 1.0 INF [C] message number ").Append(i).Append('\n');
        }

        byte[] content = Encoding.UTF8.GetBytes(builder.ToString());
        string path = CreateFile(content);

        using LogFile file = LogFile.Open(path);
        var index = new LogIndex();
        using var cts = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            new LogIndexer(16).IndexRange(
                file, index, 0, file.Length, new CancelOnProgress(cts, threshold: 10), cts.Token));

        Assert.True(index.Count > 0);
        for (int i = 0; i < index.Count; i++)
        {
            Assert.True(index.End(i) > index.Offset(i));
        }

        Assert.Equal(index.IndexedLength, index.End(index.Count - 1));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(LogIndexer.DefaultBlockSize)]
    public void Progress_ReportsMonotonicPercentAndEndsAtHundred(int blockSize)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            builder.Append("2024-10-23T14:23:45 1.0 INF [C] message ").Append(i).Append('\n');
        }

        byte[] content = Encoding.UTF8.GetBytes(builder.ToString());
        string path = CreateFile(content);
        using LogFile file = LogFile.Open(path);
        var index = new LogIndex();
        var progress = new RecordingProgress();

        new LogIndexer(blockSize).IndexRange(file, index, 0, file.Length, progress, CancellationToken.None);

        Assert.NotEmpty(progress.Values);
        Assert.Equal(100, progress.Values[^1]);
        for (int i = 1; i < progress.Values.Count; i++)
        {
            Assert.True(progress.Values[i] > progress.Values[i - 1]);
        }
    }

    private LogIndex IndexBytes(byte[] content, int blockSize)
    {
        string path = CreateFile(content);
        using LogFile file = LogFile.Open(path);
        var index = new LogIndex();
        new LogIndexer(blockSize).IndexRange(file, index, 0, file.Length, null, CancellationToken.None);
        return index;
    }

    private string CreateFile(byte[] content)
    {
        string path = Path.Combine(_tempDir, $"log{++_fileCounter}.txt");
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class CancelOnProgress(CancellationTokenSource cts, int threshold) : IProgress<int>
    {
        public void Report(int value)
        {
            if (value >= threshold)
            {
                cts.Cancel();
            }
        }
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
    }
}
