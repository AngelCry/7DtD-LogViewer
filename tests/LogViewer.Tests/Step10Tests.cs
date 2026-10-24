using System.Text;
using LogGen;
using LogViewer.Core;
using Xunit;

namespace LogViewer.Tests;

public sealed class Step10Tests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LogViewerTests_" + Guid.NewGuid().ToString("N"));

    public Step10Tests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public async Task PollAsync_NoChangeGrownReset()
    {
        string path = Path.Combine(_tempDir, "test.log");
        LogGenerator.Generate(path, new LogGenerationOptions(Count: 100, Seed: 12345));

        using var session = LogSession.Open(path);
        await session.IndexAsync(null, CancellationToken.None);

        int initialCount = session.Index.Count;
        Assert.Equal(100, initialCount);

        // Poll: NoChange
        PollResult res1 = await session.PollAsync(CancellationToken.None);
        Assert.Equal(PollKind.NoChange, res1.Kind);

        // Append more lines (Grown)
        using (var writer = new StreamWriter(path, append: true, Encoding.UTF8))
        {
            writer.WriteLine("2024-10-23T14:23:45 150.000 INF [World] Additional log line 1");
            writer.WriteLine("2024-10-23T14:23:45 151.000 INF [World] Additional log line 2");
        }

        PollResult res2 = await session.PollAsync(CancellationToken.None);
        Assert.Equal(PollKind.Grown, res2.Kind);
        Assert.Equal(100, res2.OldCount);
        Assert.Equal(102, session.Index.Count);

        // Truncate / Reset (shorter file)
        LogGenerator.Generate(path, new LogGenerationOptions(Count: 50, Seed: 99999));
        int oldGen = session.Generation;

        PollResult res3 = await session.PollAsync(CancellationToken.None);
        Assert.Equal(PollKind.Reset, res3.Kind);
        Assert.True(session.Generation > oldGen);
        Assert.Equal(50, session.Index.Count);
    }

    [Fact]
    public async Task PollAsync_PartialLine_NotIndexedUntilCompleted()
    {
        string path = Path.Combine(_tempDir, "partial.log");
        using (var writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            writer.WriteLine("2024-10-23T14:23:45 100.000 INF [World] Entry 1");
        }

        using var session = LogSession.Open(path);
        await session.IndexAsync(null, CancellationToken.None);
        Assert.Equal(1, session.Index.Count);

        // Write partial line without \n
        using (var fileStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
        {
            writer.Write("2024-10-23T14:23:46 101.000 INF [World] Incomplete entry");
            writer.Flush();
        }

        PollResult res1 = await session.PollAsync(CancellationToken.None);
        Assert.Equal(PollKind.Grown, res1.Kind);
        Assert.Equal(1, session.Index.Count); // Incomplete line must not form a new entry

        // Complete the line with \n
        using (var fileStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
        {
            writer.WriteLine(" finished");
            writer.Flush();
        }

        PollResult res2 = await session.PollAsync(CancellationToken.None);
        Assert.Equal(PollKind.Grown, res2.Kind);
        Assert.Equal(2, session.Index.Count);
        Assert.Contains("Incomplete entry finished", session.Reader.GetFullText(1));
    }

    [Fact]
    public async Task PollAsync_AppendedStackTrace_RaisesLevelAndExpandsEnd()
    {
        string path = Path.Combine(_tempDir, "stacktrace.log");
        using (var writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            writer.WriteLine("2024-10-23T14:23:45 100.000 ERR [System] An error occurred");
        }

        using var session = LogSession.Open(path);
        await session.IndexAsync(null, CancellationToken.None);
        Assert.Equal(1, session.Index.Count);
        Assert.Equal(LogLevel.Error, session.Index.Level(0));
        long initialEnd = session.Index.End(0);

        // Append stack trace continuation across several polls
        using (var writer = new StreamWriter(path, append: true, Encoding.UTF8))
        {
            writer.WriteLine("NullReferenceException: Object reference not set");
        }

        PollResult res1 = await session.PollAsync(CancellationToken.None);
        Assert.Equal(PollKind.Grown, res1.Kind);
        Assert.Equal(1, session.Index.Count);
        Assert.Equal(LogLevel.Exception, session.Index.Level(0));
        Assert.True(session.Index.End(0) > initialEnd);

        long intermediateEnd = session.Index.End(0);

        using (var writer = new StreamWriter(path, append: true, Encoding.UTF8))
        {
            writer.WriteLine("  at Game.World.Update ()");
        }

        PollResult res2 = await session.PollAsync(CancellationToken.None);
        Assert.Equal(PollKind.Grown, res2.Kind);
        Assert.Equal(1, session.Index.Count);
        Assert.Equal(LogLevel.Exception, session.Index.Level(0));
        Assert.True(session.Index.End(0) > intermediateEnd);
        Assert.Contains("Game.World.Update", session.Reader.GetFullText(0));
    }

    [Fact]
    public async Task PollAsync_ResetByHeadModification_WhenFileGrew()
    {
        string path = Path.Combine(_tempDir, "head_mod.log");
        using (var writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            writer.WriteLine("2024-10-23T14:23:45 100.000 INF [Init] Initial log line");
        }

        using var session = LogSession.Open(path);
        await session.IndexAsync(null, CancellationToken.None);
        int gen = session.Generation;
        Assert.Equal(1, session.Index.Count);

        // Rewrite beginning of file with different bytes and make file longer
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(fs, Encoding.UTF8))
        {
            fs.Position = 0;
            writer.WriteLine("2024-10-23T14:23:45 100.000 WRN [Different] Changed completely and made longer than before");
            writer.WriteLine("2024-10-23T14:23:46 101.000 INF [More] Additional line to make it longer");
            writer.Flush();
        }

        PollResult res = await session.PollAsync(CancellationToken.None);
        Assert.Equal(PollKind.Reset, res.Kind);
        Assert.True(session.Generation > gen);
        Assert.Equal(2, session.Index.Count);
    }

    [Fact]
    public void LogSession_Dispose_DuringPollAndIndexing_DoesNotThrow()
    {
        string path = Path.Combine(_tempDir, "dispose_race.log");
        LogGenerator.Generate(path, new LogGenerationOptions(Count: 50_000, Seed: 123));

        var session = LogSession.Open(path);
        var indexTask = session.IndexAsync(null, CancellationToken.None);
        var pollTask = session.PollAsync(CancellationToken.None);

        session.Dispose();

        Assert.True(indexTask.IsCompleted);
        Assert.True(pollTask.IsCompleted);

        // Subsequent Dispose is a no-op
        session.Dispose();
    }

    [Fact]
    public async Task FilterEngine_ExtendAsync_MatchesRunAsync()
    {
        string path = Path.Combine(_tempDir, "extend.log");
        LogGenerator.Generate(path, new LogGenerationOptions(Count: 200, Seed: 42));

        using var file = LogFile.Open(path);
        var index = new LogIndex();
        var indexer = new LogIndexer();
        indexer.IndexRange(file, index, 0, file.Length, null, CancellationToken.None);

        var filter = new LogFilter(LogLevelMask.All, "completed", IsRegex: false);
        FilterResult fullResult = await FilterEngine.RunAsync(file, index, filter, null, CancellationToken.None);

        // Index more
        using (var writer = new StreamWriter(path, append: true, Encoding.UTF8))
        {
            writer.WriteLine("2024-10-23T14:23:45 200.000 INF [World] World generation completed extra");
            writer.WriteLine("2024-10-23T14:23:45 201.000 WRN [World] Normal warning log");
        }

        indexer.IndexRange(file, index, index.IndexedLength, file.Length, null, CancellationToken.None);

        FilterResult extendedResult = await FilterEngine.ExtendAsync(file, index, fullResult, null, CancellationToken.None);
        FilterResult freshFullResult = await FilterEngine.RunAsync(file, index, filter, null, CancellationToken.None);

        Assert.Equal(freshFullResult.Visible?.Count, extendedResult.Visible?.Count);
        int count = freshFullResult.Visible?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(freshFullResult.Visible![i], extendedResult.Visible![i]);
        }
    }
}
