using LogGen;
using LogViewer.Core;

namespace LogViewer.Tests;

public sealed class LogSessionTests : IDisposable
{
    private readonly string _tempDir;
    private int _fileCounter;

    public LogSessionTests()
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
    public void Open_ProvidesFileAndEmptyIndex()
    {
        string path = CreateLog(100);

        using var session = LogSession.Open(path);

        Assert.Equal(new FileInfo(path).Length, session.File.Length);
        Assert.Equal(0, session.Index.Count);
        Assert.Equal(TimeSpan.Zero, session.IndexTime);
    }

    [Fact]
    public void Open_MissingFile_ThrowsFileNotFound()
    {
        string path = Path.Combine(_tempDir, "missing.log");

        Assert.Throws<FileNotFoundException>(() => LogSession.Open(path));
    }

    [Fact]
    public async Task IndexAsync_IndexesAllEntriesAndReportsProgress()
    {
        const int count = 5000;
        string path = CreateLog(count);
        using var session = LogSession.Open(path);
        var progress = new RecordingProgress();

        await session.IndexAsync(progress, CancellationToken.None);

        Assert.Equal(count, session.Index.Count);
        Assert.Equal(session.File.Length, session.Index.IndexedLength);
        Assert.True(session.IndexTime > TimeSpan.Zero);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(100, progress.Values[^1]);
    }

    [Fact]
    public async Task IndexAsync_Cancelled_ThrowsAndLeavesConsistentIndex()
    {
        const int count = 100_000;
        string path = CreateLog(count);
        using var session = LogSession.Open(path);
        using var cts = new CancellationTokenSource();
        var progress = new CancelOnProgress(cts, threshold: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.IndexAsync(progress, cts.Token));

        Assert.True(session.Index.Count > 0);
        Assert.True(session.Index.Count < count);
        for (int i = 0; i < session.Index.Count; i++)
        {
            Assert.True(session.Index.End(i) > session.Index.Offset(i));
        }

        Assert.Equal(
            session.Index.IndexedLength,
            session.Index.End(session.Index.Count - 1));
    }

    [Fact]
    public async Task Dispose_DuringIndexing_WaitsForTaskAndDisposesFile()
    {
        string path = CreateLog(200_000);
        var session = LogSession.Open(path);

        Task indexing = session.IndexAsync(null, CancellationToken.None);
        session.Dispose();

        Assert.True(indexing.IsCompleted);
        try
        {
            await indexing;
        }
        catch (OperationCanceledException)
        {
        }

        bool indexThrew = false;
        try
        {
            _ = session.IndexAsync(null, CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            indexThrew = true;
        }

        Assert.True(indexThrew);
        Assert.Throws<ObjectDisposedException>(() => _ = session.File.Length);

        session.Dispose();
    }

    [Fact]
    public async Task Dispose_AfterIndexing_IsIdempotent()
    {
        string path = CreateLog(1000);
        var session = LogSession.Open(path);
        await session.IndexAsync(null, CancellationToken.None);

        session.Dispose();
        session.Dispose();

        Assert.Equal(1000, session.Index.Count);
    }

    private string CreateLog(int count)
    {
        string path = Path.Combine(_tempDir, $"session{++_fileCounter}.log");
        LogGenerator.Generate(path, new LogGenerationOptions(Count: count, Seed: 12345));
        return path;
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
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
}
