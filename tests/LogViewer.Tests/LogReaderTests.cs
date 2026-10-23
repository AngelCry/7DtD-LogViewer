using System.Text;
using LogViewer.Core;

namespace LogViewer.Tests;

public sealed class LogReaderTests : IDisposable
{
    private readonly string _tempDir;
    private int _fileCounter;

    public LogReaderTests()
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

    [Fact]
    public void GetPreview_SingleLine()
    {
        const string line = "2024-10-23T14:23:45 1.0 INF [A] hello";
        using LogFile file = OpenFile(line + "\n");
        LogIndex index = Index(file);
        var reader = new LogReader(file, index);

        EntryPreview preview = reader.GetPreview(0);

        Assert.Equal(line, preview.FirstLine);
        Assert.Equal(1, preview.LineCount);
        Assert.False(preview.Truncated);
    }

    [Fact]
    public void GetPreview_MultiLineWithCrLf()
    {
        const string first = "2024-10-23T14:23:45 1.0 INF [A] first";
        using LogFile file = OpenFile($"{first}\r\n  at Foo\r\n  at Bar\r\n");
        LogIndex index = Index(file);
        var reader = new LogReader(file, index);

        EntryPreview preview = reader.GetPreview(0);

        Assert.Equal(first, preview.FirstLine);
        Assert.Equal(3, preview.LineCount);
        Assert.False(preview.Truncated);
        Assert.Equal($"{first}\r\n  at Foo\r\n  at Bar", reader.GetFullText(0));
    }

    [Fact]
    public void GetPreview_CyrillicIsDecoded()
    {
        const string line = "2024-10-23T14:23:45 1.0 INF [Мир] Привет, мир!";
        using LogFile file = OpenFile(line + "\n");
        LogIndex index = Index(file);
        var reader = new LogReader(file, index);

        EntryPreview preview = reader.GetPreview(0);

        Assert.Equal(line, preview.FirstLine);
        Assert.Equal(line, reader.GetFullText(0));
    }

    [Fact]
    public void GetPreview_LongEntry_TruncatesAtUtf8Boundary()
    {
        const string prefix = "2024-10-23T14:23:45 1.0 INF a";
        string content = prefix + new string('Я', 20_000) + "\n";
        using LogFile file = OpenFile(content);
        LogIndex index = Index(file);
        var reader = new LogReader(file, index);

        EntryPreview preview = reader.GetPreview(0);

        Assert.True(preview.Truncated);
        Assert.Equal(1, preview.LineCount);
        Assert.EndsWith("… [truncated]", preview.FirstLine);
        Assert.False(preview.FirstLine.Contains('\uFFFD'));
        Assert.True(preview.FirstLine.Length > 10_000);
    }

    [Fact]
    public void GetFullText_ReturnsWholeEntryWithoutTrailingNewline()
    {
        const string content = "2024-10-23T14:23:45 1.0 INF [A] first\n  at Foo\n";
        using LogFile file = OpenFile(content);
        LogIndex index = Index(file);
        var reader = new LogReader(file, index);

        string text = reader.GetFullText(0);

        Assert.Equal("2024-10-23T14:23:45 1.0 INF [A] first\n  at Foo", text);
    }

    [Fact]
    public void GetFullText_LongEntry_TruncatesAndAppendsMarker()
    {
        string content = "2024-10-23T14:23:45 1.0 INF " +
                         new string('x', LogReader.FullTextMaxBytes + 1000) + "\n";
        using LogFile file = OpenFile(content);
        LogIndex index = Index(file);
        var reader = new LogReader(file, index);

        string text = reader.GetFullText(0);

        Assert.EndsWith("… [truncated]", text);
        Assert.True(text.Length <= LogReader.FullTextMaxBytes + "… [truncated]".Length);
    }

    [Fact]
    public void GetPreview_ControlCharactersInFirstLine_ReplacedWithSpaces()
    {
        using LogFile file = OpenFile("2024-10-23T14:23:45 1.0 INF [A] a\tb\u0001c\n");
        LogIndex index = Index(file);
        var reader = new LogReader(file, index);

        EntryPreview preview = reader.GetPreview(0);

        Assert.Equal("2024-10-23T14:23:45 1.0 INF [A] a b c", preview.FirstLine);
    }

    [Fact]
    public void ReadError_IsReturnedAsText()
    {
        using (LogFile file = OpenFile("2024-10-23T14:23:45 1.0 INF [A] hello\n"))
        {
            LogIndex index = Index(file);
            var reader = new LogReader(file, index);
            file.Dispose();

            EntryPreview preview = reader.GetPreview(0);
            string fullText = reader.GetFullText(0);

            Assert.StartsWith("<read error:", preview.FirstLine);
            Assert.Equal(0, preview.LineCount);
            Assert.StartsWith("<read error:", fullText);
        }
    }

    [Fact]
    public void RepeatedGetPreview_UsesCache_InvalidateForcesRead()
    {
        using LogFile file = OpenFile("2024-10-23T14:23:45 1.0 INF [A] hello\n");
        LogIndex index = Index(file);
        var reader = new LogReader(file, index);

        EntryPreview first = reader.GetPreview(0);
        long readsAfterFirst = reader.ReadCallCount;
        Assert.True(readsAfterFirst > 0);

        EntryPreview second = reader.GetPreview(0);
        Assert.Equal(readsAfterFirst, reader.ReadCallCount);
        Assert.Equal(first, second);

        reader.Invalidate(0);
        _ = reader.GetPreview(0);
        Assert.True(reader.ReadCallCount > readsAfterFirst);

        long readsBeforeClear = reader.ReadCallCount;
        reader.ClearCache();
        _ = reader.GetPreview(0);
        Assert.True(reader.ReadCallCount > readsBeforeClear);
    }

    [Fact]
    public async Task LoggerSession_ExposesReader()
    {
        const string line = "2024-10-23T14:23:45 1.0 INF [A] hello";
        string path = CreateFile(line + "\n");

        using var session = LogSession.Open(path);
        await session.IndexAsync(null, CancellationToken.None);

        Assert.NotNull(session.Reader);
        Assert.Equal(line, session.Reader.GetFullText(0));
    }

    private LogFile OpenFile(string content)
    {
        return LogFile.Open(CreateFile(content));
    }

    private string CreateFile(string content)
    {
        string path = Path.Combine(_tempDir, $"reader{++_fileCounter}.log");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static LogIndex Index(LogFile file)
    {
        var index = new LogIndex();
        new LogIndexer().IndexRange(file, index, 0, file.Length, null, CancellationToken.None);
        return index;
    }
}
